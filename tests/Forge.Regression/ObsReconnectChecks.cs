using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using Forge.App;
using Forge.PluginSdk;

internal static class ObsReconnectChecks
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "forge-obs-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using var server = new FakeObs();
            var events = new ForgeEventBus();
            var changes = new ConcurrentQueue<bool>();
            using var subscription = events.Subscribe<ObsConnectionChanged>(e => { changes.Enqueue(e.Connected); return Task.CompletedTask; });
            await using var obs = new ObsWebSocketService(events, new ForgeLogger(directory));

            // OBS is unavailable when Forge starts. The first failure must not stop retries.
            server.Available = false;
            try { await obs.ConnectAsync("127.0.0.1", server.Port, "", autoReconnect: true); throw new Exception("Unavailable OBS connected"); }
            catch (WebSocketException) { }
            Check(obs.IsReconnecting && !obs.IsConnected, "Initial failure did not enter retry mode");
            server.Available = true;
            await Until(() => obs.IsConnected);
            Check((await obs.RequestAsync("GetVersion")).GetProperty("ok").GetBoolean(), "Reconnected socket could not serve requests");

            // A dropped socket must fail its pending calls and reconnect to a fresh session.
            var pending = obs.RequestAsync("Hold");
            await Until(() => server.HeldRequests > 0);
            var before = server.Connections;
            server.DropConnections();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Disconnected request unexpectedly succeeded"); }
            catch (IOException) { }
            await Until(() => obs.IsConnected && server.Connections > before);
            Check(changes.Count(c => c) >= 2 && changes.Contains(false), "Missing connection status events");
            Check((await obs.RequestAsync("GetVersion")).GetProperty("ok").GetBoolean(), "Old receive loop damaged the new session");

            before = server.Connections;
            try { await obs.RequestAsync("Close").WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Closed request succeeded"); }
            catch (IOException) { }
            await Until(() => obs.IsConnected && server.Connections > before);

            // Explicit Disconnect cancels retries and remains disconnected.
            await obs.DisconnectAsync();
            before = server.Connections;
            await Task.Delay(3500);
            Check(!obs.IsConnected && !obs.IsReconnecting && server.Connections == before, "Manual disconnect reconnected");

            server.Available = false;
            try { await obs.ConnectAsync("127.0.0.1", server.Port, "", autoReconnect: true); }
            catch (WebSocketException) { }
            await obs.DisconnectAsync();
            server.Available = true;
            await Task.Delay(3500);
            Check(server.Connections == before && !obs.IsReconnecting, "Stop reconnecting did not cancel a pending retry");

            // Turning automatic reconnect off keeps the current connection but stops recovery.
            await obs.ConnectAsync("127.0.0.1", server.Port, "", autoReconnect: true);
            obs.SetAutoReconnect(false);
            Check(obs.IsConnected, "Changing reconnect preference disconnected OBS");
            before = server.Connections;
            server.DropConnections();
            await Until(() => !obs.IsConnected);
            await Task.Delay(3500);
            Check(server.Connections == before && !obs.IsReconnecting, "Disabled auto reconnect retried");

            // A TCP/WebSocket handshake alone must never count as authenticated/identified.
            server.RequirePassword = true;
            try { await obs.ConnectAsync("127.0.0.1", server.Port, ""); throw new Exception("Missing password accepted"); }
            catch (UnauthorizedAccessException) { }
            Check(!obs.IsConnected, "Unauthenticated socket reported connected");
            server.RequirePassword = false;
            await obs.ConnectAsync("127.0.0.1", server.Port, "", autoReconnect: true);
            await obs.DisposeAsync();
            before = server.Connections;
            await Task.Delay(3500);
            Check(server.Connections == before && !obs.IsConnected, "Disposed service reconnected");
            Console.WriteLine("PASS: OBS startup retry, dropped-connection recovery, pending request cleanup, manual stop, disabled retry, identification and shutdown.");
        }
        finally { Directory.Delete(directory, true); }
    }

    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (!condition()) await Task.Delay(25, timeout.Token);
    }

    private sealed class FakeObs : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly ConcurrentBag<WebSocket> _sockets = [];
        private readonly ConcurrentBag<Task> _clients = [];
        private readonly Task _worker;
        private int _connections, _heldRequests;
        public int Connections => Volatile.Read(ref _connections);
        public int HeldRequests => Volatile.Read(ref _heldRequests);
        public volatile bool Available = true;
        public volatile bool RequirePassword;
        public int Port { get; }
        public FakeObs()
        {
            var portFinder = new TcpListener(IPAddress.Loopback, 0);
            portFinder.Start(); Port = ((IPEndPoint)portFinder.LocalEndpoint).Port; portFinder.Stop();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _worker = AcceptAsync();
        }
        private async Task AcceptAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var context = await _listener.GetContextAsync();
                    if (!Available) { context.Response.StatusCode = 503; context.Response.Close(); continue; }
                    _clients.Add(HandleAsync(context));
                }
            }
            catch (Exception) when (_stop.IsCancellationRequested) { }
        }
        private async Task HandleAsync(HttpListenerContext context)
        {
            try
            {
                using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
                _sockets.Add(socket);
                object hello = RequirePassword
                    ? new { op = 0, d = (object)new { rpcVersion = 1, authentication = new { salt = "salt", challenge = "challenge" } } }
                    : new { op = 0, d = (object)new { rpcVersion = 1 } };
                await Send(socket, hello);
                await Read(socket);
                await Send(socket, new { op = 2, d = new { negotiatedRpcVersion = 1 } });
                Interlocked.Increment(ref _connections);
                while (!_stop.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    var request = await Read(socket);
                    var data = request.GetProperty("d");
                    if (data.GetProperty("requestType").GetString() == "Close") { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "OBS exiting", _stop.Token); return; }
                    if (data.GetProperty("requestType").GetString() == "Hold") { Interlocked.Increment(ref _heldRequests); continue; }
                    await Send(socket, new { op = 7, d = new { requestId = data.GetProperty("requestId").GetString(), requestStatus = new { result = true }, responseData = new { ok = true } } });
                }
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException) { }
        }
        private async Task Send(WebSocket socket, object message) => await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, _stop.Token);
        private async Task<JsonElement> Read(WebSocket socket)
        {
            var buffer = new byte[8192];
            var result = await socket.ReceiveAsync(buffer, _stop.Token);
            if (result.MessageType == WebSocketMessageType.Close) throw new IOException("Disconnected");
            using var json = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
            return json.RootElement.Clone();
        }
        public void DropConnections() { foreach (var socket in _sockets) { try { socket.Abort(); } catch (ObjectDisposedException) { } } }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel(); DropConnections(); _listener.Close();
            await _worker; await Task.WhenAll(_clients); _stop.Dispose();
        }
    }
}
