using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Forge.PluginSdk;

namespace Forge.LinkProtection;

public sealed class LinkProtectionPlugin : IForgePlugin
{
    private static readonly Regex LinkPattern = new(
        @"(?<![\w@])(?:https?://)?(?:www\.)?(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}(?::\d{1,5})?(?:/[^\s<>""']*)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IForgeContext? _context;
    private IDisposable? _subscription;
    private ProtectionStatus _status = new();

    public Task InitializeAsync(IForgeContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _status = LoadStatus();
        WriteStatus();
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _context!.Events.Subscribe<TwitchChatMessage>(OnMessageAsync);
        _context.Settings.Changed += OnSettingsChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose(); _subscription = null;
        if (_context is not null) _context.Settings.Changed -= OnSettingsChanged;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() { await StopAsync(CancellationToken.None); _gate.Dispose(); }

    private async Task OnMessageAsync(TwitchChatMessage message)
    {
        if (!_context!.Settings.Get("enabled", true) || message.IsBroadcaster) return;
        var links = ExtractHosts(message.Text);
        if (links.Count == 0) return;
        await _gate.WaitAsync();
        try
        {
            _status.MessagesWithLinks++;
            if (!string.IsNullOrWhiteSpace(message.SourceBroadcasterUserId))
            {
                _status.ForeignSharedChatMessagesIgnored++;
                Record(message, "ignored-shared-chat", links);
                return;
            }
            if (message.IsModerator && _context.Settings.Get("exemptModerators", true))
            {
                _status.AllowedMessages++;
                Record(message, "allowed-moderator", links);
                return;
            }
            var allowed = AllowedDomains();
            var blocked = links.Where(host => !IsAllowed(host, allowed)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (blocked.Count == 0)
            {
                _status.AllowedMessages++;
                Record(message, "allowed-domains", links);
                return;
            }
            if (_context.Settings.Get("dryRun", false))
            {
                _status.DryRunMatches++;
                Record(message, "dry-run", blocked);
                return;
            }
            await _context.Connections.Twitch.DeleteChatMessageAsync(message.MessageId);
            _status.DeletedMessages++;
            Record(message, "deleted", blocked);
        }
        catch (Exception ex)
        {
            _status.Failures++;
            _status.LastAction = new(DateTimeOffset.UtcNow, "delete-failed", message.UserName, [], ex.GetBaseException().Message);
        }
        finally { WriteStatus(); _gate.Release(); }
    }

    private List<string> ExtractHosts(string text)
    {
        var result = new List<string>();
        foreach (Match match in LinkPattern.Matches(text))
        {
            var value = match.Value.TrimEnd('.', ',', '!', '?', ';', ':', ')', ']', '}');
            var host = NormalizeDomain(value);
            if (host is not null) result.Add(host);
        }
        return result;
    }

    private HashSet<string> AllowedDomains()
    {
        var raw = _context!.Settings.Get("allowedDomains", "");
        var result = raw.Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#'))
            .Select(NormalizeDomain).Where(domain => domain is not null).Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_context.Settings.Get("allowTwitchLinks", true)) result.Add("twitch.tv");
        return result;
    }

    private static string? NormalizeDomain(string value)
    {
        value = value.Trim();
        if (value.Length == 0) return null;
        if (!value.Contains("://", StringComparison.Ordinal)) value = "https://" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host)) return null;
        try
        {
            var host = new IdnMapping().GetAscii(uri.Host.TrimEnd('.')).ToLowerInvariant();
            return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
        }
        catch (ArgumentException) { return null; }
    }

    private static bool IsAllowed(string host, HashSet<string> allowed) => allowed.Any(domain =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));

    private void Record(TwitchChatMessage message, string action, IReadOnlyList<string> domains) =>
        _status.LastAction = new(DateTimeOffset.UtcNow, action, message.UserName, domains, null);
    private void OnSettingsChanged(object? sender, EventArgs e) => WriteStatus();
    private void WriteStatus() => File.WriteAllText(Path.Combine(_context!.DataDirectory, "link-protection-status.json"), JsonSerializer.Serialize(_status, JsonOptions));
    private ProtectionStatus LoadStatus() { try { return JsonSerializer.Deserialize<ProtectionStatus>(File.ReadAllText(Path.Combine(_context!.DataDirectory, "link-protection-status.json"))) ?? new(); } catch { return new(); } }

    private sealed class ProtectionStatus
    {
        public int MessagesWithLinks { get; set; }
        public int AllowedMessages { get; set; }
        public int ForeignSharedChatMessagesIgnored { get; set; }
        public int DeletedMessages { get; set; }
        public int DryRunMatches { get; set; }
        public int Failures { get; set; }
        public AuditAction? LastAction { get; set; }
    }
    private sealed record AuditAction(DateTimeOffset At, string Action, string UserName, IReadOnlyList<string> Domains, string? Error);
}
