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
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _lifetime;
    public bool IsConnected => _socket?.State == WebSocketState.Open;
    public ObsWebSocketService(IForgeEventBus events, ForgeLogger log) { _events = events; _log = log; }

    public async Task ConnectAsync(string host, int port, string? password, CancellationToken cancellationToken = default)
    {
        await DisconnectAsync();
        _socket = new ClientWebSocket(); _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await _socket.ConnectAsync(new Uri($"ws://{host}:{port}"), cancellationToken);
            var hello = await ReceiveAsync(_socket, cancellationToken);
            var helloData = hello.GetProperty("d");
            string? authentication = null;
            if (helloData.TryGetProperty("authentication", out var auth))
            {
                if (string.IsNullOrEmpty(password)) throw new UnauthorizedAccessException("OBS WebSocket requires a password.");
                authentication = CreateAuthentication(password, auth.GetProperty("salt").GetString()!, auth.GetProperty("challenge").GetString()!);
            }
            await SendAsync(new { op = 1, d = new { rpcVersion = 1, authentication, eventSubscriptions = 2047 } }, cancellationToken);
            var identified = await ReceiveAsync(_socket, cancellationToken);
            if (identified.GetProperty("op").GetInt32() != 2) throw new InvalidDataException("OBS did not accept the WebSocket identification request.");
            _ = Task.Run(() => ReceiveLoopAsync(_lifetime.Token));
            await _events.PublishAsync(new ObsConnectionChanged(true), cancellationToken);
            await _log.WriteAsync("INFO", "OBS", $"Connected to {host}:{port}");
        }
        catch (Exception ex) { await _log.WriteAsync("ERROR", "OBS", "Connection failed", ex); await DisconnectAsync(); throw; }
    }

    public async Task<JsonElement> RequestAsync(string requestType, object? requestData = null, CancellationToken cancellationToken = default)
    {
        if (!IsConnected) throw new InvalidOperationException("OBS is not connected.");
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        await SendAsync(new { op = 6, d = new { requestType, requestId = id, requestData } }, cancellationToken);
        return await completion.Task;
    }

    public async Task DisconnectAsync()
    {
        _lifetime?.Cancel();
        if (_socket is { State: WebSocketState.Open } socket)
            try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Forge disconnecting", CancellationToken.None); } catch { }
        _socket?.Dispose(); _socket = null; _lifetime?.Dispose(); _lifetime = null;
        foreach (var item in _pending.Values) item.TrySetException(new IOException("OBS disconnected.")); _pending.Clear();
        await _events.PublishAsync(new ObsConnectionChanged(false));
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _socket is { State: WebSocketState.Open } socket)
            {
                var message = await ReceiveAsync(socket, cancellationToken); var op = message.GetProperty("op").GetInt32(); var data = message.GetProperty("d");
                if (op == 7 && data.TryGetProperty("requestId", out var id) && _pending.TryRemove(id.GetString()!, out var pending))
                {
                    var status = data.GetProperty("requestStatus");
                    if (status.GetProperty("result").GetBoolean()) pending.TrySetResult(data.TryGetProperty("responseData", out var result) ? result.Clone() : default);
                    else pending.TrySetException(new InvalidOperationException(status.TryGetProperty("comment", out var comment) ? comment.GetString() : "OBS request failed."));
                }
                else if (op == 5) await _events.PublishAsync(new ObsEvent(data.GetProperty("eventType").GetString()!, data.TryGetProperty("eventData", out var eventData) ? eventData.Clone() : default), cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { await _log.WriteAsync("ERROR", "OBS", "Receive loop stopped", ex); }
        finally { if (IsConnected) await DisconnectAsync(); }
    }

    private async Task SendAsync(object message, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message); await _socket!.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
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
    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
