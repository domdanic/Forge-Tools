using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Forge.PluginSdk;

namespace Forge.App;

public sealed record TwitchDeviceAuthorization(string DeviceCode, string UserCode, Uri VerificationUri, int ExpiresIn, int Interval);
public sealed record TwitchIdentity(string UserId, string Login, string[] Scopes);

public sealed class TwitchAuthService
{
    public const string ClientId = "bp6dq7ewhr9rqj3g2x64mcz0ae7tat";
    private const string RequestedScopes = "user:read:chat user:write:chat channel:manage:broadcast channel:read:ads bits:read channel:read:subscriptions moderator:read:followers moderator:manage:chat_messages channel:manage:redemptions moderator:read:chatters";
    private readonly HttpClient _http = new();
    private readonly string _credentialPath;
    private TwitchTokens? _tokens;
    public TwitchIdentity? Identity { get; private set; }

    public TwitchAuthService(string credentialsDirectory) => _credentialPath = Path.Combine(credentialsDirectory, "twitch.oauth");

    public async Task<TwitchDeviceAuthorization> BeginAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsync("https://id.twitch.tv/oauth2/device", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["scopes"] = RequestedScopes
        }), cancellationToken);
        await EnsureSuccessAsync(response);
        var dto = await JsonSerializer.DeserializeAsync<DeviceResponse>(await response.Content.ReadAsStreamAsync(cancellationToken), JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Twitch returned an empty device authorization response.");
        return new(dto.device_code, dto.user_code, new(dto.verification_uri), dto.expires_in, Math.Max(dto.interval, 1));
    }

    public async Task<TwitchIdentity> CompleteAsync(TwitchDeviceAuthorization authorization, CancellationToken cancellationToken)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(authorization.ExpiresIn);
        while (DateTimeOffset.UtcNow < expiresAt)
        {
            await Task.Delay(TimeSpan.FromSeconds(authorization.Interval), cancellationToken);
            using var response = await _http.PostAsync("https://id.twitch.tv/oauth2/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["scopes"] = RequestedScopes,
                ["device_code"] = authorization.DeviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
            }), cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                _tokens = JsonSerializer.Deserialize<TwitchTokens>(json, JsonOptions) ?? throw new InvalidOperationException("Twitch returned an invalid token response.");
                SaveTokens();
                Identity = await ValidateAsync(cancellationToken) ?? throw new InvalidOperationException("Twitch authorized the account but token validation failed.");
                return Identity;
            }
            var error = JsonSerializer.Deserialize<TwitchError>(json, JsonOptions)?.message;
            if (error is "authorization_pending") continue;
            if (error is "slow_down") { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken); continue; }
            throw new InvalidOperationException(error ?? $"Twitch authorization failed ({(int)response.StatusCode}).");
        }
        throw new TimeoutException("The Twitch activation code expired.");
    }

    public async Task<TwitchIdentity?> RestoreAsync(CancellationToken cancellationToken = default)
    {
        LoadTokens();
        if (_tokens is null) return null;
        var identity = await ValidateAsync(cancellationToken);
        if (identity is not null && HasRequiredScopes(identity)) { Identity = identity; return identity; }
        if (identity is not null) { SignOutLocal(); return null; }
        if (string.IsNullOrWhiteSpace(_tokens.refresh_token)) { SignOutLocal(); return null; }
        try { await RefreshAsync(cancellationToken); Identity = await ValidateAsync(cancellationToken); return Identity; }
        catch { SignOutLocal(); return null; }
    }

    public async Task<TwitchIdentity?> ValidateAsync(CancellationToken cancellationToken = default)
    {
        if (_tokens is null) return null;
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://id.twitch.tv/oauth2/validate");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokens.access_token);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized) return null;
        await EnsureSuccessAsync(response);
        var data = await JsonSerializer.DeserializeAsync<ValidateResponse>(await response.Content.ReadAsStreamAsync(cancellationToken), JsonOptions, cancellationToken);
        return data is null ? null : new(data.user_id, data.login, data.scopes ?? []);
    }

    public async Task<TwitchCategory?> FindCategoryAsync(string exactName, CancellationToken cancellationToken = default)
    {
        using var response = await SendHelixAsync(HttpMethod.Get, $"games?name={Uri.EscapeDataString(exactName)}", null, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var item = document.RootElement.GetProperty("data").EnumerateArray().FirstOrDefault();
        return item.ValueKind == JsonValueKind.Undefined ? null : new(item.GetProperty("id").GetString()!, item.GetProperty("name").GetString()!);
    }

    public async Task<IReadOnlyList<TwitchCategory>> SearchCategoriesAsync(string query, CancellationToken cancellationToken = default)
    {
        using var response = await SendHelixAsync(HttpMethod.Get, $"search/categories?query={Uri.EscapeDataString(query)}&first=20", null, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty("data").EnumerateArray()
            .Select(item => new TwitchCategory(item.GetProperty("id").GetString()!, item.GetProperty("name").GetString()!))
            .ToList();
    }

    public async Task<TwitchChannel?> GetChannelAsync(CancellationToken cancellationToken = default)
    {
        if (Identity is null) return null;
        using var response = await SendHelixAsync(HttpMethod.Get, $"channels?broadcaster_id={Uri.EscapeDataString(Identity.UserId)}", null, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var item = document.RootElement.GetProperty("data").EnumerateArray().FirstOrDefault();
        return item.ValueKind == JsonValueKind.Undefined ? null : new(item.GetProperty("broadcaster_id").GetString()!, item.GetProperty("game_id").GetString()!, item.GetProperty("game_name").GetString()!, item.GetProperty("title").GetString()!);
    }

    public async Task UpdateCategoryAsync(string categoryId, CancellationToken cancellationToken = default)
    {
        if (Identity is null) throw new InvalidOperationException("Twitch is not connected.");
        using var response = await SendHelixAsync(HttpMethod.Patch, $"channels?broadcaster_id={Uri.EscapeDataString(Identity.UserId)}", new { game_id = categoryId }, cancellationToken);
    }

    public async Task<IReadOnlyList<TwitchCustomReward>> GetCustomRewardsAsync(CancellationToken cancellationToken = default)
    {
        if (Identity is null) throw new InvalidOperationException("Twitch is not connected.");
        using var response = await SendHelixAsync(HttpMethod.Get, $"channel_points/custom_rewards?broadcaster_id={Uri.EscapeDataString(Identity.UserId)}", null, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        using var manageableResponse = await SendHelixAsync(HttpMethod.Get, $"channel_points/custom_rewards?broadcaster_id={Uri.EscapeDataString(Identity.UserId)}&only_manageable_rewards=true", null, cancellationToken);
        using var manageableDocument = JsonDocument.Parse(await manageableResponse.Content.ReadAsStringAsync(cancellationToken));
        var manageable = manageableDocument.RootElement.GetProperty("data").EnumerateArray().Select(x => x.GetProperty("id").GetString() ?? "").ToHashSet(StringComparer.Ordinal);
        return document.RootElement.GetProperty("data").EnumerateArray().Select(item => ParseReward(item, manageable.Contains(item.GetProperty("id").GetString() ?? ""))).ToList();
    }

    public async Task<IReadOnlyList<TwitchChatter>> GetChattersAsync(CancellationToken cancellationToken = default)
    {
        if (Identity is null) throw new InvalidOperationException("Twitch is not connected.");
        var result = new List<TwitchChatter>();
        string? cursor = null;
        do
        {
            var relative = $"chat/chatters?broadcaster_id={Uri.EscapeDataString(Identity.UserId)}&moderator_id={Uri.EscapeDataString(Identity.UserId)}&first=1000";
            if (!string.IsNullOrWhiteSpace(cursor)) relative += "&after=" + Uri.EscapeDataString(cursor);
            using var response = await SendHelixAsync(HttpMethod.Get, relative, null, cancellationToken);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            result.AddRange(document.RootElement.GetProperty("data").EnumerateArray().Select(item => new TwitchChatter(item.GetProperty("user_id").GetString() ?? "", item.GetProperty("user_login").GetString() ?? "", item.GetProperty("user_name").GetString() ?? "")));
            cursor = document.RootElement.TryGetProperty("pagination", out var pagination) && pagination.TryGetProperty("cursor", out var next) ? next.GetString() : null;
        } while (!string.IsNullOrWhiteSpace(cursor));
        return result;
    }

    public async Task<TwitchCustomReward> CreateCustomRewardAsync(TwitchCustomRewardRequest reward, CancellationToken cancellationToken = default)
    {
        if (Identity is null) throw new InvalidOperationException("Twitch is not connected.");
        using var response = await SendHelixAsync(HttpMethod.Post, $"channel_points/custom_rewards?broadcaster_id={Uri.EscapeDataString(Identity.UserId)}", RewardBody(reward), cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return ParseReward(document.RootElement.GetProperty("data").EnumerateArray().First(), true);
    }

    public async Task UpdateCustomRewardAsync(string rewardId, TwitchCustomRewardRequest reward, CancellationToken cancellationToken = default)
    {
        if (Identity is null) throw new InvalidOperationException("Twitch is not connected.");
        using var response = await SendHelixAsync(HttpMethod.Patch, $"channel_points/custom_rewards?broadcaster_id={Uri.EscapeDataString(Identity.UserId)}&id={Uri.EscapeDataString(rewardId)}", RewardBody(reward), cancellationToken);
    }

    public async Task DeleteCustomRewardAsync(string rewardId, CancellationToken cancellationToken = default)
    {
        if (Identity is null) throw new InvalidOperationException("Twitch is not connected.");
        using var response = await SendHelixAsync(HttpMethod.Delete, $"channel_points/custom_rewards?broadcaster_id={Uri.EscapeDataString(Identity.UserId)}&id={Uri.EscapeDataString(rewardId)}", null, cancellationToken);
    }

    public async Task UpdateRedemptionStatusAsync(string rewardId, string redemptionId, string status, CancellationToken cancellationToken = default)
    {
        if (Identity is null) throw new InvalidOperationException("Twitch is not connected.");
        if (status is not ("FULFILLED" or "CANCELED")) throw new ArgumentOutOfRangeException(nameof(status));
        using var response = await SendHelixAsync(HttpMethod.Patch, $"channel_points/custom_rewards/redemptions?broadcaster_id={Uri.EscapeDataString(Identity.UserId)}&reward_id={Uri.EscapeDataString(rewardId)}&id={Uri.EscapeDataString(redemptionId)}", new { status }, cancellationToken);
    }

    private static object RewardBody(TwitchCustomRewardRequest reward) => new { title = reward.Title, prompt = reward.Prompt, cost = reward.Cost, is_enabled = reward.IsEnabled, is_user_input_required = reward.IsUserInputRequired, should_redemptions_skip_request_queue = reward.SkipRequestQueue };
    private static TwitchCustomReward ParseReward(JsonElement item, bool manageable) => new(item.GetProperty("id").GetString() ?? "", item.GetProperty("title").GetString() ?? "", item.GetProperty("prompt").GetString() ?? "", item.GetProperty("cost").GetInt32(), item.GetProperty("is_enabled").GetBoolean(), item.GetProperty("is_paused").GetBoolean(), item.GetProperty("is_user_input_required").GetBoolean(), item.GetProperty("should_redemptions_skip_request_queue").GetBoolean(), manageable);

    public async Task SendChatMessageAsync(string message, CancellationToken cancellationToken = default)
    {
        if (Identity is null) throw new InvalidOperationException("Twitch is not connected.");
        message = message.Trim();
        if (message.Length is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(message), "A Twitch chat message must be between 1 and 500 characters.");
        using var response = await SendHelixAsync(HttpMethod.Post, "chat/messages", new
        {
            broadcaster_id = Identity.UserId,
            sender_id = Identity.UserId,
            message
        }, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var result = document.RootElement.GetProperty("data").EnumerateArray().FirstOrDefault();
        if (result.ValueKind != JsonValueKind.Undefined && result.TryGetProperty("is_sent", out var sent) && !sent.GetBoolean())
        {
            var reason = result.TryGetProperty("drop_reason", out var drop) && drop.TryGetProperty("message", out var detail) ? detail.GetString() : null;
            throw new InvalidOperationException(reason ?? "Twitch did not send the chat message.");
        }
    }

    public async Task DeleteChatMessageAsync(string messageId, CancellationToken cancellationToken = default)
    {
        if (Identity is null) throw new InvalidOperationException("Twitch is not connected.");
        if (!Identity.Scopes.Contains("moderator:manage:chat_messages", StringComparer.Ordinal))
            throw new InvalidOperationException("Reconnect Twitch to grant chat-moderation permission.");
        if (string.IsNullOrWhiteSpace(messageId)) throw new ArgumentException("A Twitch message ID is required.", nameof(messageId));
        using var response = await SendHelixAsync(HttpMethod.Delete,
            $"moderation/chat?broadcaster_id={Uri.EscapeDataString(Identity.UserId)}&moderator_id={Uri.EscapeDataString(Identity.UserId)}&message_id={Uri.EscapeDataString(messageId)}",
            null, cancellationToken);
    }

    public async Task<TwitchAdSchedule?> GetAdScheduleAsync(CancellationToken cancellationToken = default)
    {
        if (Identity is null) throw new InvalidOperationException("Twitch is not connected.");
        using var response = await SendHelixAsync(HttpMethod.Get, $"channels/ads?broadcaster_id={Uri.EscapeDataString(Identity.UserId)}", null, cancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var item = document.RootElement.GetProperty("data").EnumerateArray().FirstOrDefault();
        if (item.ValueKind == JsonValueKind.Undefined) return null;
        DateTimeOffset? next = null;
        if (item.TryGetProperty("next_ad_at", out var nextElement))
        {
            if (nextElement.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(nextElement.GetString(), out var parsed)) next = parsed;
            else if (nextElement.ValueKind == JsonValueKind.Number && nextElement.TryGetInt64(out var unix) && unix > 0) next = DateTimeOffset.FromUnixTimeSeconds(unix);
        }
        var duration = item.TryGetProperty("duration", out var durationElement) && durationElement.TryGetInt32(out var seconds) ? seconds : 0;
        var snoozes = item.TryGetProperty("snooze_count", out var snoozeElement) && snoozeElement.TryGetInt32(out var count) ? count : 0;
        return new(next, duration, snoozes);
    }

    public async Task CreateChatSubscriptionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (Identity is null) throw new InvalidOperationException("Twitch is not connected.");
        if (!Identity.Scopes.Contains("user:read:chat", StringComparer.Ordinal))
            throw new InvalidOperationException("Reconnect Twitch to grant chat-reading permission.");
        using var response = await SendHelixAsync(HttpMethod.Post, "eventsub/subscriptions", new
        {
            type = "channel.chat.message",
            version = "1",
            condition = new { broadcaster_user_id = Identity.UserId, user_id = Identity.UserId },
            transport = new { method = "websocket", session_id = sessionId }
        }, cancellationToken);
    }

    public async Task CreateAdSubscriptionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (Identity is null) throw new InvalidOperationException("Twitch is not connected.");
        if (!Identity.Scopes.Contains("channel:read:ads", StringComparer.Ordinal))
            throw new InvalidOperationException("Reconnect Twitch to grant ad-reading permission.");
        using var response = await SendHelixAsync(HttpMethod.Post, "eventsub/subscriptions", new
        {
            type = "channel.ad_break.begin",
            version = "1",
            condition = new { broadcaster_user_id = Identity.UserId },
            transport = new { method = "websocket", session_id = sessionId }
        }, cancellationToken);
    }

    public Task CreateRedemptionSubscriptionAsync(string sessionId, CancellationToken cancellationToken = default) =>
        CreateSubscriptionAsync("channel.channel_points_custom_reward_redemption.add", "1", new { broadcaster_user_id = Identity?.UserId ?? throw new InvalidOperationException("Twitch is not connected.") }, sessionId, "channel:manage:redemptions", cancellationToken);

    public async Task CreateRecapSubscriptionsAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (Identity is null) throw new InvalidOperationException("Twitch is not connected.");
        await CreateSubscriptionAsync("channel.follow", "2", new { broadcaster_user_id = Identity.UserId, moderator_user_id = Identity.UserId }, sessionId, "moderator:read:followers", cancellationToken);
        await CreateSubscriptionAsync("channel.subscribe", "1", new { broadcaster_user_id = Identity.UserId }, sessionId, "channel:read:subscriptions", cancellationToken);
        await CreateSubscriptionAsync("channel.subscription.message", "1", new { broadcaster_user_id = Identity.UserId }, sessionId, "channel:read:subscriptions", cancellationToken);
        await CreateSubscriptionAsync("channel.subscription.gift", "1", new { broadcaster_user_id = Identity.UserId }, sessionId, "channel:read:subscriptions", cancellationToken);
        await CreateSubscriptionAsync("channel.cheer", "1", new { broadcaster_user_id = Identity.UserId }, sessionId, "bits:read", cancellationToken);
        await CreateSubscriptionAsync("channel.raid", "1", new { to_broadcaster_user_id = Identity.UserId }, sessionId, null, cancellationToken);
    }

    private async Task CreateSubscriptionAsync(string type, string version, object condition, string sessionId, string? requiredScope, CancellationToken cancellationToken)
    {
        if (Identity is null) throw new InvalidOperationException("Twitch is not connected.");
        if (requiredScope is not null && !Identity.Scopes.Contains(requiredScope, StringComparer.Ordinal))
            throw new InvalidOperationException($"Reconnect Twitch to grant {requiredScope} permission.");
        using var response = await SendHelixAsync(HttpMethod.Post, "eventsub/subscriptions", new
        {
            type,
            version,
            condition,
            transport = new { method = "websocket", session_id = sessionId }
        }, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendHelixAsync(HttpMethod method, string relative, object? body, CancellationToken cancellationToken)
    {
        if (_tokens is null) throw new InvalidOperationException("Twitch is not connected.");
        var request = new HttpRequestMessage(method, "https://api.twitch.tv/helix/" + relative);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokens.access_token); request.Headers.Add("Client-Id", ClientId);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
        var response = await _http.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized && !string.IsNullOrEmpty(_tokens.refresh_token))
        {
            response.Dispose(); await RefreshAsync(cancellationToken); request = new HttpRequestMessage(method, "https://api.twitch.tv/helix/" + relative);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokens!.access_token); request.Headers.Add("Client-Id", ClientId);
            if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
            response = await _http.SendAsync(request, cancellationToken);
        }
        await EnsureSuccessAsync(response); return response;
    }

    private static bool HasRequiredScopes(TwitchIdentity identity) => RequestedScopes.Split(' ').All(required => identity.Scopes.Contains(required, StringComparer.Ordinal));

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsync("https://id.twitch.tv/oauth2/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = _tokens!.refresh_token
        }), cancellationToken);
        await EnsureSuccessAsync(response);
        _tokens = await JsonSerializer.DeserializeAsync<TwitchTokens>(await response.Content.ReadAsStreamAsync(cancellationToken), JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Twitch returned an invalid refresh response.");
        SaveTokens();
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        if (_tokens is not null)
        {
            try { await _http.PostAsync("https://id.twitch.tv/oauth2/revoke", new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = ClientId, ["token"] = _tokens.access_token }), cancellationToken); }
            catch { }
        }
        SignOutLocal();
    }

    private void SignOutLocal() { _tokens = null; Identity = null; if (File.Exists(_credentialPath)) File.Delete(_credentialPath); }

    private void SaveTokens()
    {
        if (_tokens is null || !OperatingSystem.IsWindows()) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(_tokens, JsonOptions);
        File.WriteAllBytes(_credentialPath, ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
    }

    private void LoadTokens()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(_credentialPath)) return;
        try { _tokens = JsonSerializer.Deserialize<TwitchTokens>(ProtectedData.Unprotect(File.ReadAllBytes(_credentialPath), null, DataProtectionScope.CurrentUser), JsonOptions); }
        catch { SignOutLocal(); }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync();
        var message = JsonSerializer.Deserialize<TwitchError>(body, JsonOptions)?.message;
        throw new HttpRequestException(message ?? $"Twitch returned HTTP {(int)response.StatusCode}.");
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private sealed record DeviceResponse(string device_code, int expires_in, int interval, string user_code, string verification_uri);
    private sealed record TwitchTokens(string access_token, int expires_in, string refresh_token, string[] scope, string token_type);
    private sealed record TwitchError(string? message);
    private sealed record ValidateResponse(string client_id, string login, string user_id, string[]? scopes, int expires_in);
}
