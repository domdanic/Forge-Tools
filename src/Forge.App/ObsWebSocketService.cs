using Forge.PluginSdk;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Forge.App;

public sealed class ObsWebSocketService : IObsConnection, IAsyncDisposable
{
    private readonly IForgeEventBus _events;
    private readonly ForgeLogger _log;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private Session? _session;
    private bool _disposed;
    public bool IsConnected => _session?.Connection is { Ready: true, Socket.State: WebSocketState.Open };
    public bool IsReconnecting => _session is { AutoReconnect: true } session && !session.Worker.IsCompleted && !session.Lifetime.IsCancellationRequested && !IsConnected;
    public ObsWebSocketService(IForgeEventBus events, ForgeLogger log) { _events = events; _log = log; }

    public void SetAutoReconnect(bool enabled)
    {
        if (_session is { } session) session.AutoReconnect = enabled;
    }

    public async Task ConnectAsync(string host, int port, string? password, CancellationToken cancellationToken = default, bool autoReconnect = false)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), "OBS WebSocket port must be between 1 and 65535.");
        var endpoint = new UriBuilder("ws", host, port).Uri;
        Session session;
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopSessionAsync();
            session = new Session(cancellationToken) { AutoReconnect = autoReconnect };
            _session = session;
            session.Worker = Task.Run(() => RunSessionAsync(session, endpoint, password));
        }
        finally { _lifecycle.Release(); }
        // Report the initial attempt to the UI, while automatic retries continue in the background.
        await session.FirstAttempt.Task;
    }

    public async Task DisconnectAsync()
    {
        await _lifecycle.WaitAsync();
        try { await StopSessionAsync(); }
        finally { _lifecycle.Release(); }
    }

    private async Task StopSessionAsync()
    {
        var session = _session;
        if (session is null) return;
        session.Lifetime.Cancel();
        session.Connection?.Socket.Abort();
        await session.Worker;
        _session = null;
        session.Lifetime.Dispose();
    }

    private async Task RunSessionAsync(Session session, Uri endpoint, string? password)
    {
        var token = session.Lifetime.Token;
        try
        {
            do
            {
                var connection = new Connection();
                session.Connection = connection;
                try
                {
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                    attempt.CancelAfter(TimeSpan.FromSeconds(10));
                    await connection.Socket.ConnectAsync(endpoint, attempt.Token);
                    var hello = await ReceiveAsync(connection.Socket, attempt.Token);
                    var helloData = hello.GetProperty("d");
                    string? authentication = null;
                    if (helloData.TryGetProperty("authentication", out var auth))
                    {
                        if (string.IsNullOrEmpty(password)) throw new UnauthorizedAccessException("OBS WebSocket requires a password.");
                        authentication = CreateAuthentication(password, auth.GetProperty("salt").GetString()!, auth.GetProperty("challenge").GetString()!);
                    }
                    await SendAsync(connection, new { op = 1, d = new { rpcVersion = 1, authentication, eventSubscriptions = 2047 } }, attempt.Token);
                    var identified = await ReceiveAsync(connection.Socket, attempt.Token);
                    if (identified.GetProperty("op").GetInt32() != 2) throw new InvalidDataException("OBS did not accept the WebSocket identification request.");
                    connection.Ready = true;
                    var receiver = ReceiveLoopAsync(connection, token);
                    await PublishConnectionAsync(true);
                    session.FirstAttempt.TrySetResult();
                    await _log.WriteAsync("INFO", "OBS", $"Connected to {endpoint.Host}:{endpoint.Port}");
                    await receiver;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    session.FirstAttempt.TrySetException(ex);
                    await _log.WriteAsync("WARN", "OBS", session.AutoReconnect ? "Connection lost or unavailable; retrying in 3 seconds" : "Connection stopped", ex);
                }
                finally
                {
                    connection.Ready = false;
                    connection.Socket.Abort();
                    connection.Socket.Dispose();
                    foreach (var pending in connection.Pending.Values) pending.TrySetException(new IOException("OBS disconnected."));
                    connection.Pending.Clear();
                    session.Connection = null;
                    await PublishConnectionAsync(false);
                }
                if (!session.AutoReconnect || token.IsCancellationRequested) break;
                await Task.Delay(TimeSpan.FromSeconds(3), token);
            } while (session.AutoReconnect && !token.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { session.FirstAttempt.TrySetCanceled(); }
    }

    private async Task PublishConnectionAsync(bool connected)
    {
        try { await _events.PublishAsync(new ObsConnectionChanged(connected)); }
        catch (Exception ex) { await _log.WriteAsync("WARN", "OBS", "Connection status subscriber failed", ex); }
    }

    public async Task<JsonElement> RequestAsync(string requestType, object? requestData = null, CancellationToken cancellationToken = default)
    {
        var connection = _session?.Connection;
        if (connection is not { Ready: true, Socket.State: WebSocketState.Open }) throw new InvalidOperationException("OBS is not connected.");
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Pending[id] = completion;
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        try
        {
            await SendAsync(connection, new { op = 6, d = new { requestType, requestId = id, requestData } }, cancellationToken);
            return await completion.Task;
        }
        finally { connection.Pending.TryRemove(id, out _); }
    }

    private async Task ReceiveLoopAsync(Connection connection, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && connection.Socket.State == WebSocketState.Open)
        {
            var message = await ReceiveAsync(connection.Socket, cancellationToken);
            var op = message.GetProperty("op").GetInt32(); var data = message.GetProperty("d");
            if (op == 7 && data.TryGetProperty("requestId", out var id) && connection.Pending.TryRemove(id.GetString()!, out var pending))
            {
                var status = data.GetProperty("requestStatus");
                if (status.GetProperty("result").GetBoolean()) pending.TrySetResult(data.TryGetProperty("responseData", out var result) ? result.Clone() : default);
                else pending.TrySetException(new InvalidOperationException(status.TryGetProperty("comment", out var comment) ? comment.GetString() : "OBS request failed."));
            }
            else if (op == 5) await _events.PublishAsync(new ObsEvent(data.GetProperty("eventType").GetString()!, data.TryGetProperty("eventData", out var eventData) ? eventData.Clone() : default), cancellationToken);
        }
    }

    private static async Task SendAsync(Connection connection, object message, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        await connection.SendGate.WaitAsync(cancellationToken);
        try { await connection.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken); }
        finally { connection.SendGate.Release(); }
    }
    private static async Task<JsonElement> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(); var buffer = new byte[8192]; WebSocketReceiveResult result;
        do { result = await socket.ReceiveAsync(buffer, cancellationToken); if (result.MessageType == WebSocketMessageType.Close) throw new IOException("OBS closed the connection."); stream.Write(buffer, 0, result.Count); } while (!result.EndOfMessage);
        using var document = JsonDocument.Parse(stream.ToArray()); return document.RootElement.Clone();
    }
    private static string CreateAuthentication(string password, string salt, string challenge)
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }
    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync();
        try { _disposed = true; await StopSessionAsync(); }
        finally { _lifecycle.Release(); }
    }

    private sealed class Session(CancellationToken token)
    {
        public readonly CancellationTokenSource Lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        public readonly TaskCompletionSource FirstAttempt = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool AutoReconnect;
        public volatile Connection? Connection;
        public Task Worker = Task.CompletedTask;
    }
    private sealed class Connection
    {
        public readonly ClientWebSocket Socket = new();
        public readonly SemaphoreSlim SendGate = new(1, 1);
        public readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> Pending = new();
        public volatile bool Ready;
        public Connection()
        {
            Socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            Socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10);
        }
    }
}
