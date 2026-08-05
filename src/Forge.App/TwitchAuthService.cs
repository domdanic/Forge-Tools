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
    private const string RequestedScopes = "user:read:chat channel:manage:broadcast";
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
