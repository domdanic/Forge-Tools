using Forge.PluginSdk;
using System.Security.Cryptography;
using System.Text.Json;

namespace Forge.TimedAnnouncements;

public sealed class TimedAnnouncementsPlugin : IForgePlugin
{
    private IForgeContext? _context;
    private CancellationTokenSource? _lifetime;
    private Task? _worker;
    private IDisposable? _chatSubscription;
    private IDisposable? _adSubscription;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private RuntimeState _state = new();
    private string StatePath => Path.Combine(_context!.DataDirectory, "state.json");

    public Task InitializeAsync(IForgeContext context, CancellationToken cancellationToken)
    {
        _context = context;
        try { _state = JsonSerializer.Deserialize<RuntimeState>(File.ReadAllText(StatePath)) ?? new(); } catch { _state = new(); }
        MigrateLegacyRules();
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _chatSubscription = _context!.Events.Subscribe<TwitchChatMessage>(message =>
        {
            foreach (var rule in LoadRules()) _state.ChatMessages[rule.Id] = _state.ChatMessages.GetValueOrDefault(rule.Id) + 1;
            Save(); return Task.CompletedTask;
        });
        _adSubscription = _context.Events.Subscribe<TwitchAdBreakStarted>(OnAdStartedAsync);
        _worker = RunAsync(_lifetime.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _chatSubscription?.Dispose(); _adSubscription?.Dispose();
        _chatSubscription = null; _adSubscription = null;
        if (_lifetime is null) return;
        _lifetime.Cancel();
        if (_worker is not null) try { await _worker.WaitAsync(cancellationToken); } catch (OperationCanceledException) { }
        Save(); _lifetime.Dispose(); _lifetime = null;
    }

    private async Task OnAdStartedAsync(TwitchAdBreakStarted ad)
    {
        _state.AdEndsAt = ad.StartedAt.AddSeconds(Math.Max(0, ad.DurationSeconds));
        _state.AdDurationSeconds = ad.DurationSeconds;
        Save();
        await Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                foreach (var rule in LoadRules()) await CheckRecurringAsync(rule, cancellationToken);
                await CheckAdEndAsync(cancellationToken);
                if (DateTimeOffset.UtcNow - _state.LastScheduleCheck >= TimeSpan.FromSeconds(45)) await CheckAdScheduleAsync(cancellationToken);
            }
            catch (Exception ex) { WriteStatus(ex.Message); }
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }

    private async Task CheckRecurringAsync(AnnouncementRule rule, CancellationToken cancellationToken)
    {
        if (!rule.Enabled || rule.Messages.Count == 0) return;
        var minutes = Math.Clamp(rule.IntervalMinutes, 1, 10080);
        var requiredChat = Math.Clamp(rule.MinimumChatMessages, 0, 100000);
        _state.LastSent.TryGetValue(rule.Id, out var last);
        if (last == default) { _state.LastSent[rule.Id] = DateTimeOffset.UtcNow; Save(); return; }
        if (DateTimeOffset.UtcNow - last < TimeSpan.FromMinutes(minutes) || _state.ChatMessages.GetValueOrDefault(rule.Id) < requiredChat) return;
        var text = rule.Messages[RandomNumberGenerator.GetInt32(rule.Messages.Count)].Trim();
        if (text.Length == 0) return;
        await SendAsync(text, cancellationToken);
        _state.LastSent[rule.Id] = DateTimeOffset.UtcNow;
        _state.ChatMessages[rule.Id] = 0;
        Save();
    }

    private List<AnnouncementRule> LoadRules() => _context!.Settings.Get("announcementRules", new List<AnnouncementRule>())
        .Where(rule => !string.IsNullOrWhiteSpace(rule.Id))
        .Select(rule => rule with { Messages = rule.Messages.Where(message => !string.IsNullOrWhiteSpace(message)).Select(message => message.Trim()).Distinct().ToList() })
        .ToList();

    private void MigrateLegacyRules()
    {
        if (LoadRules().Count > 0) return;
        var migrated = new List<AnnouncementRule>();
        for (var index = 1; index <= 3; index++)
        {
            var text = _context!.Settings.Get($"message{index}Text", "").Trim();
            var enabled = _context.Settings.Get($"message{index}Enabled", false);
            if (!enabled && text.Length == 0) continue;
            migrated.Add(new AnnouncementRule(
                $"legacy-{index}",
                $"Announcement {index}",
                enabled,
                ClampInt(_context.Settings.Get($"message{index}Minutes", "60"), 60, 1, 10080),
                ClampInt(_context.Settings.Get($"message{index}ChatCount", "10"), 10, 0, 100000),
                text.Length == 0 ? [] : [text]));
        }
        if (migrated.Count > 0) _context!.Settings.Set("announcementRules", migrated);
    }

    private async Task CheckAdEndAsync(CancellationToken cancellationToken)
    {
        if (_state.AdEndsAt is null || DateTimeOffset.UtcNow < _state.AdEndsAt) return;
        var end = _state.AdEndsAt; _state.AdEndsAt = null; Save();
        if (!_context!.Settings.Get("adEndEnabled", true)) return;
        var text = _context.Settings.Get("adEndText", "Ads are over—welcome back!")
            .Replace("{duration}", FormatDuration(_state.AdDurationSeconds), StringComparison.OrdinalIgnoreCase).Trim();
        if (text.Length > 0) await SendAsync(text, cancellationToken);
    }

    private async Task CheckAdScheduleAsync(CancellationToken cancellationToken)
    {
        _state.LastScheduleCheck = DateTimeOffset.UtcNow; Save();
        if (!_context!.Settings.Get("adWarningEnabled", true) || !_context.Connections.Twitch.IsConnected) return;
        var schedule = await _context.Connections.Twitch.GetAdScheduleAsync(cancellationToken);
        if (schedule?.NextAdAt is null) return;
        var lead = ClampInt(_context.Settings.Get("adWarningMinutes", "2"), 2, 1, 15);
        var remaining = schedule.NextAdAt.Value - DateTimeOffset.UtcNow;
        var scheduleKey = schedule.NextAdAt.Value.ToUnixTimeSeconds().ToString();
        if (remaining > TimeSpan.Zero && remaining <= TimeSpan.FromMinutes(lead) && _state.WarnedSchedule != scheduleKey)
        {
            var text = _context.Settings.Get("adWarningText", "Heads up: ads are scheduled in about {minutes} minutes.")
                .Replace("{minutes}", lead.ToString(), StringComparison.OrdinalIgnoreCase).Trim();
            if (text.Length > 0) await SendAsync(text, cancellationToken);
            _state.WarnedSchedule = scheduleKey; Save();
        }
    }

    private async Task SendAsync(string message, CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken);
        try { await _context!.Connections.Twitch.SendChatMessageAsync(message.Length <= 500 ? message : message[..500], cancellationToken); WriteStatus("Sent: " + message); }
        finally { _sendGate.Release(); }
    }

    private void Save()
    {
        var temporary = StatePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, StatePath, true);
    }
    private void WriteStatus(string message) => File.WriteAllText(Path.Combine(_context!.DataDirectory, "status.json"), JsonSerializer.Serialize(new { message, at = DateTimeOffset.UtcNow }, new JsonSerializerOptions { WriteIndented = true }));
    private static int ClampInt(string value, int fallback, int minimum, int maximum) => Math.Clamp(int.TryParse(value, out var parsed) ? parsed : fallback, minimum, maximum);
    private static string FormatDuration(int seconds) => seconds >= 60 ? $"{Math.Max(1, seconds / 60)} minute(s)" : $"{seconds} seconds";
    public async ValueTask DisposeAsync() { if (_lifetime is not null) await StopAsync(CancellationToken.None); _sendGate.Dispose(); }

    private sealed class RuntimeState
    {
        public Dictionary<string, int> ChatMessages { get; set; } = [];
        public Dictionary<string, DateTimeOffset> LastSent { get; set; } = [];
        public DateTimeOffset? AdEndsAt { get; set; }
        public int AdDurationSeconds { get; set; }
        public DateTimeOffset LastScheduleCheck { get; set; }
        public string? WarnedSchedule { get; set; }
    }
    private sealed record AnnouncementRule(string Id, string Name, bool Enabled, int IntervalMinutes, int MinimumChatMessages, List<string> Messages);
}
