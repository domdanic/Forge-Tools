using Forge.PluginSdk;
using System.Net.WebSockets;
using System.Text.Json;

namespace Forge.App;

public sealed class TwitchChatService : IAsyncDisposable
{
    private static readonly Uri DefaultEndpoint = new("wss://eventsub.wss.twitch.tv/ws?keepalive_timeout_seconds=30");
    private readonly TwitchAuthService _auth;
    private readonly IForgeEventBus _events;
    private readonly ForgeLogger _log;
    private readonly Queue<string> _recentOrder = new();
    private readonly HashSet<string> _recentIds = new(StringComparer.Ordinal);
    private CancellationTokenSource? _lifetime;
    private Task? _worker;
    private bool _includeChat;
    private bool _includeAds;
    private bool _includeRecapEvents;

    public TwitchChatService(TwitchAuthService auth, IForgeEventBus events, ForgeLogger log)
    {
        _auth = auth;
        _events = events;
        _log = log;
    }

    public async Task StartAsync(bool includeChat = true, bool includeAds = false, bool includeRecapEvents = false, CancellationToken cancellationToken = default)
    {
        await StopAsync();
        if (_auth.Identity is null) return;
        _includeChat = includeChat;
        _includeAds = includeAds;
        _includeRecapEvents = includeRecapEvents;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _worker = RunAsync(_lifetime.Token);
    }

    public async Task StopAsync()
    {
        _lifetime?.Cancel();
        if (_worker is not null) try { await _worker; } catch (OperationCanceledException) { }
        _worker = null;
        _lifetime?.Dispose();
        _lifetime = null;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        Uri endpoint = DefaultEndpoint;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(endpoint, cancellationToken);
                endpoint = await ListenAsync(socket, endpoint == DefaultEndpoint, cancellationToken) ?? DefaultEndpoint;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                await _log.WriteAsync("ERROR", "TwitchChat", "Chat EventSub connection stopped", ex);
                endpoint = DefaultEndpoint;
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }

    private async Task<Uri?> ListenAsync(ClientWebSocket socket, bool createSubscription, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var message = await ReceiveAsync(socket, cancellationToken);
            var metadata = message.GetProperty("metadata");
            var messageType = metadata.GetProperty("message_type").GetString();
            if (messageType == "session_welcome")
            {
                if (createSubscription)
                {
                    var sessionId = message.GetProperty("payload").GetProperty("session").GetProperty("id").GetString()
                        ?? throw new InvalidDataException("Twitch chat session did not include an ID.");
                    if (_includeChat) await _auth.CreateChatSubscriptionAsync(sessionId, cancellationToken);
                    if (_includeAds) await _auth.CreateAdSubscriptionAsync(sessionId, cancellationToken);
                    if (_includeRecapEvents) await _auth.CreateRecapSubscriptionsAsync(sessionId, cancellationToken);
                    await _log.WriteAsync("INFO", "TwitchEventSub", "Twitch event subscriptions connected.");
                }
            }
            else if (messageType == "notification") await PublishNotificationAsync(message, metadata, cancellationToken);
            else if (messageType == "session_reconnect")
            {
                var reconnect = message.GetProperty("payload").GetProperty("session").GetProperty("reconnect_url").GetString();
                return Uri.TryCreate(reconnect, UriKind.Absolute, out var uri) ? uri : DefaultEndpoint;
            }
            else if (messageType == "revocation")
            {
                var status = message.GetProperty("payload").GetProperty("subscription").GetProperty("status").GetString();
                throw new InvalidOperationException("Twitch revoked the chat subscription: " + status);
            }
        }
        return null;
    }

    private async Task PublishNotificationAsync(JsonElement envelope, JsonElement metadata, CancellationToken cancellationToken)
    {
        var messageId = metadata.GetProperty("message_id").GetString() ?? Guid.NewGuid().ToString("N");
        if (!_recentIds.Add(messageId)) return;
        _recentOrder.Enqueue(messageId);
        while (_recentOrder.Count > 500) _recentIds.Remove(_recentOrder.Dequeue());

        var payload = envelope.GetProperty("payload");
        var subscriptionType = payload.GetProperty("subscription").GetProperty("type").GetString();
        var item = payload.GetProperty("event");
        if (subscriptionType == "channel.ad_break.begin")
        {
            var duration = item.TryGetProperty("duration_seconds", out var durationElement) ? durationElement.GetInt32() : 0;
            var startedAt = item.TryGetProperty("started_at", out var startedElement) && DateTimeOffset.TryParse(startedElement.GetString(), out var parsed) ? parsed : DateTimeOffset.UtcNow;
            var automatic = item.TryGetProperty("is_automatic", out var automaticElement) && automaticElement.GetBoolean();
            try { await _events.PublishAsync(new TwitchAdBreakStarted(duration, startedAt, automatic), cancellationToken); }
            catch (Exception ex) { await _log.WriteAsync("ERROR", "TwitchEventSub", "An ad event subscriber failed", ex); }
            return;
        }
        try
        {
            var at = GetTimestamp(item, "followed_at") ?? DateTimeOffset.UtcNow;
            switch (subscriptionType)
            {
                case "channel.follow":
                    await _events.PublishAsync(new TwitchFollowed(Get(item, "user_id"), Get(item, "user_login"), Get(item, "user_name"), at), cancellationToken);
                    return;
                case "channel.subscribe":
                    await _events.PublishAsync(new TwitchSubscribed(Get(item, "user_id"), Get(item, "user_login"), Get(item, "user_name"), Get(item, "tier"), GetBool(item, "is_gift"), at), cancellationToken);
                    return;
                case "channel.subscription.message":
                    await _events.PublishAsync(new TwitchSubscriptionMessage(Get(item, "user_id"), Get(item, "user_login"), Get(item, "user_name"), Get(item, "tier"), GetInt(item, "cumulative_months"), GetNullableInt(item, "streak_months"), GetInt(item, "duration_months"), item.TryGetProperty("message", out var subMessage) ? Get(subMessage, "text") : "", at), cancellationToken);
                    return;
                case "channel.subscription.gift":
                    await _events.PublishAsync(new TwitchSubscriptionGifted(Get(item, "user_id"), Get(item, "user_login"), Get(item, "user_name"), Get(item, "tier"), GetInt(item, "total"), GetNullableInt(item, "cumulative_total"), GetBool(item, "is_anonymous"), at), cancellationToken);
                    return;
                case "channel.cheer":
                    await _events.PublishAsync(new TwitchCheered(Get(item, "user_id"), Get(item, "user_login"), Get(item, "user_name"), GetInt(item, "bits"), Get(item, "message"), GetBool(item, "is_anonymous"), at), cancellationToken);
                    return;
                case "channel.raid":
                    await _events.PublishAsync(new TwitchRaided(Get(item, "from_broadcaster_user_id"), Get(item, "from_broadcaster_user_login"), Get(item, "from_broadcaster_user_name"), GetInt(item, "viewers"), at), cancellationToken);
                    return;
            }
        }
        catch (Exception ex) { await _log.WriteAsync("ERROR", "TwitchEventSub", $"A {subscriptionType} event subscriber failed", ex); return; }
        if (subscriptionType != "channel.chat.message") return;
        var userId = item.GetProperty("chatter_user_id").GetString() ?? "";
        var broadcasterId = item.GetProperty("broadcaster_user_id").GetString() ?? "";
        var badges = item.TryGetProperty("badges", out var badgesElement) && badgesElement.ValueKind == JsonValueKind.Array
            ? badgesElement.EnumerateArray().Select(badge => badge.GetProperty("set_id").GetString()).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];
        var chat = new TwitchChatMessage(
            Get(item, "message_id"),
            userId,
            item.GetProperty("chatter_user_login").GetString() ?? "",
            item.GetProperty("chatter_user_name").GetString() ?? "",
            item.GetProperty("message").GetProperty("text").GetString() ?? "",
            userId == broadcasterId || badges.Contains("broadcaster"),
            badges.Contains("moderator"),
            DateTimeOffset.UtcNow,
            NullIfEmpty(Get(item, "source_broadcaster_user_id")),
            NullIfEmpty(Get(item, "source_message_id")),
            GetBool(item, "is_source_only"));
        try { await _events.PublishAsync(chat, cancellationToken); }
        catch (Exception ex) { await _log.WriteAsync("ERROR", "TwitchChat", "A chat event subscriber failed", ex); }
    }

    private static string Get(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static int GetInt(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : 0;
    private static int? GetNullableInt(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null && value.TryGetInt32(out var result) ? result : null;
    private static bool GetBool(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static DateTimeOffset? GetTimestamp(JsonElement item, string name) => item.TryGetProperty(name, out var value) && DateTimeOffset.TryParse(value.GetString(), out var result) ? result : null;

    private static async Task<JsonElement> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[8192];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) throw new IOException("Twitch closed the chat connection.");
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
