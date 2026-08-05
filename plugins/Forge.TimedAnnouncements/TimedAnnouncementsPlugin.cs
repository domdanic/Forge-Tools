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
    private IDisposable? _testSubscription;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly object _stateSync = new();
    private RuntimeState _state = new();
    private bool _wasStreaming;
    private string StatePath => Path.Combine(_context!.DataDirectory, "state.json");

    public Task InitializeAsync(IForgeContext context, CancellationToken cancellationToken)
    {
        _context = context;
        try { _state = JsonSerializer.Deserialize<RuntimeState>(File.ReadAllText(StatePath)) ?? new(); } catch { _state = new(); }
        MigrateLegacyRules(); MigrateLegacyAdSettings(); return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _chatSubscription = _context!.Events.Subscribe<TwitchChatMessage>(OnChatAsync);
        _adSubscription = _context.Events.Subscribe<TwitchAdBreakStarted>(OnAdStartedAsync);
        _testSubscription = _context.Events.Subscribe<TimedAnnouncementTestRequested>(OnTestAsync);
        _worker = RunAsync(_lifetime.Token); return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _chatSubscription?.Dispose(); _adSubscription?.Dispose(); _testSubscription?.Dispose();
        _chatSubscription = null; _adSubscription = null; _testSubscription = null;
        if (_lifetime is null) return; _lifetime.Cancel();
        if (_worker is not null) try { await _worker.WaitAsync(cancellationToken); } catch (OperationCanceledException) { }
        Save(); _lifetime.Dispose(); _lifetime = null;
    }

    private Task OnChatAsync(TwitchChatMessage message)
    {
        var ignored = SplitValues(_context!.Settings.Get("ignoredUsers", ""));
        if (ignored.Contains(message.UserLogin, StringComparer.OrdinalIgnoreCase)) return Task.CompletedTask;
        lock (_stateSync)
        {
            foreach (var rule in LoadRules())
            {
                if (rule.ExcludeBroadcaster && message.IsBroadcaster) continue;
                _state.ChatMessages[rule.Id] = _state.ChatMessages.GetValueOrDefault(rule.Id) + 1;
                if (!_state.UniqueChatters.TryGetValue(rule.Id, out var users)) _state.UniqueChatters[rule.Id] = users = [];
                users.Add(message.UserId);
            }
            Save();
        }
        return Task.CompletedTask;
    }

    private async Task OnAdStartedAsync(TwitchAdBreakStarted ad)
    {
        lock (_stateSync) { _state.AdEndsAt = ad.StartedAt.AddSeconds(Math.Max(0, ad.DurationSeconds)); _state.AdDurationSeconds = ad.DurationSeconds; _state.ResumeAnnouncementsAt = _state.AdEndsAt.Value.AddMinutes(IntSetting("postAdDelayMinutes", 1, 0, 60)); Save(); }
        if (_context!.Settings.Get("paused", false) || !_context.Settings.Get("adStartEnabled", true)) return;
        var text = SelectFromPool("ad-start", LinesSetting("adStartMessages", "Ads are starting now and will run for {duration}."), "random");
        if (text is not null) await SendAsync(text, CancellationToken.None);
    }

    private async Task OnTestAsync(TimedAnnouncementTestRequested request)
    {
        string? message = request.Kind.ToLowerInvariant() switch
        {
            "rule" => LoadRules().FirstOrDefault(rule => rule.Id == request.RuleId) is { } rule ? SelectFromPool(rule.Id, rule.Messages, rule.SelectionMode) : null,
            "ad-warning" => SelectFromPool("test-ad-warning", LinesSetting("adWarningMessages", "Heads up: ads are scheduled in about {minutes} minutes."), "random"),
            "ad-start" => SelectFromPool("test-ad-start", LinesSetting("adStartMessages", "Ads are starting now and will run for {duration}."), "random"),
            "ad-end" => SelectFromPool("test-ad-end", LinesSetting("adEndMessages", "Ads are over—welcome back!"), "random"),
            _ => null
        };
        if (message is null) throw new InvalidOperationException("This announcement has no message options.");
        await SendAsync(message, CancellationToken.None, test: true);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var obs = await GetObsSnapshotAsync(cancellationToken);
                UpdateStreamSession(obs.Streaming);
                if (!_context!.Settings.Get("paused", false))
                {
                    await CheckAdEndAsync(cancellationToken);
                    if (DateTimeOffset.UtcNow - _state.LastScheduleCheck >= TimeSpan.FromSeconds(45)) await CheckAdScheduleAsync(cancellationToken);
                    if ((_state.ResumeAnnouncementsAt ?? DateTimeOffset.MinValue) <= DateTimeOffset.UtcNow)
                        foreach (var rule in LoadRules()) await CheckRecurringAsync(rule, obs, cancellationToken);
                }
                WriteRuntimeStatus(obs);
            }
            catch (Exception ex) { WriteStatus(ex.GetBaseException().Message); }
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }
    }

    private void UpdateStreamSession(bool streaming)
    {
        lock (_stateSync)
        {
            if (streaming && !_wasStreaming)
            {
                _state.StreamStartedAt = DateTimeOffset.UtcNow; _state.SentThisStream.Clear();
                foreach (var rule in LoadRules()) if (!_state.LastSent.ContainsKey(rule.Id)) _state.LastSent[rule.Id] = DateTimeOffset.UtcNow;
                Save();
            }
            else if (!streaming && _wasStreaming) { _state.StreamStartedAt = null; _state.SentThisStream.Clear(); Save(); }
            _wasStreaming = streaming;
        }
    }

    private async Task CheckRecurringAsync(AnnouncementRule rule, ObsSnapshot obs, CancellationToken cancellationToken)
    {
        if (!rule.Enabled || rule.Messages.Count == 0 || !ConditionAllows(rule.UpdateWhen, obs)) return;
        if (rule.UpdateWhen.Equals("streaming", StringComparison.OrdinalIgnoreCase) && _state.StreamStartedAt is { } started && DateTimeOffset.UtcNow - started < TimeSpan.FromMinutes(Math.Clamp(rule.InitialDelayMinutes, 0, 1440))) return;
        if (obs.Streaming && rule.MaximumSendsPerStream > 0 && _state.SentThisStream.GetValueOrDefault(rule.Id) >= rule.MaximumSendsPerStream) return;
        _state.LastSent.TryGetValue(rule.Id, out var last);
        if (last == default) { _state.LastSent[rule.Id] = DateTimeOffset.UtcNow; Save(); return; }
        if (DateTimeOffset.UtcNow - last < TimeSpan.FromMinutes(Math.Clamp(rule.IntervalMinutes, 1, 10080))) return;
        if (_state.ChatMessages.GetValueOrDefault(rule.Id) < Math.Clamp(rule.MinimumChatMessages, 0, 100000)) return;
        if ((_state.UniqueChatters.TryGetValue(rule.Id, out var users) ? users.Count : 0) < Math.Clamp(rule.MinimumUniqueChatters, 0, 100000)) return;
        var text = SelectFromPool(rule.Id, rule.Messages, rule.SelectionMode); if (text is null) return;
        await SendAsync(text, cancellationToken);
        lock (_stateSync) { _state.LastSent[rule.Id] = DateTimeOffset.UtcNow; _state.ChatMessages[rule.Id] = 0; _state.UniqueChatters[rule.Id] = []; if (obs.Streaming) _state.SentThisStream[rule.Id] = _state.SentThisStream.GetValueOrDefault(rule.Id) + 1; Save(); }
    }

    private async Task CheckAdEndAsync(CancellationToken cancellationToken)
    {
        if (_state.AdEndsAt is null || DateTimeOffset.UtcNow < _state.AdEndsAt) return;
        lock (_stateSync) { _state.AdEndsAt = null; Save(); }
        if (!_context!.Settings.Get("adEndEnabled", true)) return;
        var text = SelectFromPool("ad-end", LinesSetting("adEndMessages", "Ads are over—welcome back!"), "random");
        if (text is not null) await SendAsync(text, cancellationToken);
    }

    private async Task CheckAdScheduleAsync(CancellationToken cancellationToken)
    {
        lock (_stateSync) { _state.LastScheduleCheck = DateTimeOffset.UtcNow; Save(); }
        if (!_context!.Settings.Get("adWarningEnabled", true) || !_context.Connections.Twitch.IsConnected) return;
        var schedule = await _context.Connections.Twitch.GetAdScheduleAsync(cancellationToken); if (schedule?.NextAdAt is null) return;
        var lead = IntSetting("adWarningMinutes", 2, 1, 15); var remaining = schedule.NextAdAt.Value - DateTimeOffset.UtcNow; var key = schedule.NextAdAt.Value.ToUnixTimeSeconds().ToString();
        if (remaining <= TimeSpan.Zero || remaining > TimeSpan.FromMinutes(lead) || _state.WarnedSchedule == key) return;
        var text = SelectFromPool("ad-warning", LinesSetting("adWarningMessages", "Heads up: ads are scheduled in about {minutes} minutes."), "random");
        if (text is not null) await SendAsync(text.Replace("{minutes}", lead.ToString(), StringComparison.OrdinalIgnoreCase), cancellationToken);
        lock (_stateSync) { _state.WarnedSchedule = key; Save(); }
    }

    private string? SelectFromPool(string key, List<string> messages, string mode)
    {
        messages = messages.Where(message => !string.IsNullOrWhiteSpace(message)).Select(message => message.Trim()).Distinct().ToList(); if (messages.Count == 0) return null;
        lock (_stateSync)
        {
            int index;
            if (mode.Equals("sequential", StringComparison.OrdinalIgnoreCase)) { index = _state.NextMessageIndex.GetValueOrDefault(key) % messages.Count; _state.NextMessageIndex[key] = (index + 1) % messages.Count; }
            else if (mode.Equals("shuffle", StringComparison.OrdinalIgnoreCase))
            {
                if (!_state.ShuffleRemaining.TryGetValue(key, out var remaining) || remaining.Count == 0 || remaining.Any(value => value >= messages.Count)) { remaining = Enumerable.Range(0, messages.Count).ToList(); Shuffle(remaining); _state.ShuffleRemaining[key] = remaining; }
                index = remaining[0]; remaining.RemoveAt(0);
            }
            else
            {
                index = RandomNumberGenerator.GetInt32(messages.Count);
                if (messages.Count > 1 && index == _state.LastMessageIndex.GetValueOrDefault(key, -1)) index = (index + RandomNumberGenerator.GetInt32(1, messages.Count)) % messages.Count;
            }
            _state.LastMessageIndex[key] = index; Save(); return messages[index];
        }
    }

    private async Task SendAsync(string message, CancellationToken cancellationToken, bool test = false)
    {
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            var spacing = TimeSpan.FromSeconds(IntSetting("minimumGlobalSpacingSeconds", 15, 0, 300)); var wait = spacing - (DateTimeOffset.UtcNow - _state.LastAutomatedMessageAt);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, cancellationToken);
            message = await ResolvePlaceholdersAsync(message, cancellationToken);
            await _context!.Connections.Twitch.SendChatMessageAsync(message.Length <= 500 ? message : message[..500], cancellationToken);
            lock (_stateSync) { _state.LastAutomatedMessageAt = DateTimeOffset.UtcNow; Save(); } WriteStatus((test ? "Test sent: " : "Sent: ") + message);
        }
        finally { _sendGate.Release(); }
    }

    private async Task<string> ResolvePlaceholdersAsync(string message, CancellationToken cancellationToken)
    {
        message = message.Replace("{duration}", FormatDuration(_state.AdDurationSeconds), StringComparison.OrdinalIgnoreCase)
            .Replace("{channel}", _context!.Connections.Twitch.Login ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", DateTimeOffset.Now.ToString("t"), StringComparison.OrdinalIgnoreCase);
        if (message.Contains("{category}", StringComparison.OrdinalIgnoreCase) || message.Contains("{title}", StringComparison.OrdinalIgnoreCase))
        {
            var channel = await _context.Connections.Twitch.GetChannelAsync(cancellationToken);
            message = message.Replace("{category}", channel?.CategoryName ?? "", StringComparison.OrdinalIgnoreCase).Replace("{title}", channel?.Title ?? "", StringComparison.OrdinalIgnoreCase);
        }
        return message;
    }

    private async Task<ObsSnapshot> GetObsSnapshotAsync(CancellationToken cancellationToken)
    {
        if (!_context!.Connections.Obs.IsConnected) return new(false, false);
        try { var result = await _context.Connections.Obs.RequestAsync("GetStreamStatus", cancellationToken: cancellationToken); return new(true, result.TryGetProperty("outputActive", out var active) && active.GetBoolean()); }
        catch { return new(true, false); }
    }
    private static bool ConditionAllows(string mode, ObsSnapshot obs) => mode.ToLowerInvariant() switch { "always" => true, "obs-connected" => obs.Connected, _ => obs.Streaming };

    private List<AnnouncementRule> LoadRules() => _context!.Settings.Get("announcementRules", new List<AnnouncementRule>()).Where(rule => !string.IsNullOrWhiteSpace(rule.Id)).Select(rule => { rule.Messages = rule.Messages?.Where(message => !string.IsNullOrWhiteSpace(message)).ToList() ?? []; return rule; }).ToList();
    private void MigrateLegacyRules()
    {
        if (LoadRules().Count > 0) return; var migrated = new List<AnnouncementRule>();
        for (var index = 1; index <= 3; index++) { var text = _context!.Settings.Get($"message{index}Text", "").Trim(); var enabled = _context.Settings.Get($"message{index}Enabled", false); if (!enabled && text.Length == 0) continue; migrated.Add(new() { Id = $"legacy-{index}", Name = $"Announcement {index}", Enabled = enabled, IntervalMinutes = IntSetting($"message{index}Minutes", 60, 1, 10080), MinimumChatMessages = IntSetting($"message{index}ChatCount", 10, 0, 100000), Messages = text.Length == 0 ? [] : [text] }); }
        if (migrated.Count > 0) _context!.Settings.Set("announcementRules", migrated);
    }

    private void MigrateLegacyAdSettings()
    {
        if (_context!.Settings.Get("adWarningMessages", "").Length == 0)
        {
            var legacy = _context.Settings.Get("adWarningText", "").Trim(); if (legacy.Length > 0) _context.Settings.Set("adWarningMessages", legacy);
        }
        if (_context.Settings.Get("adEndMessages", "").Length == 0)
        {
            var legacy = _context.Settings.Get("adEndText", "").Trim(); if (legacy.Length > 0) _context.Settings.Set("adEndMessages", legacy);
        }
    }

    private int IntSetting(string key, int fallback, int minimum, int maximum) => Math.Clamp(int.TryParse(_context!.Settings.Get(key, fallback.ToString()), out var parsed) ? parsed : fallback, minimum, maximum);
    private List<string> LinesSetting(string key, string fallback) => SplitLines(_context!.Settings.Get(key, fallback));
    private static List<string> SplitLines(string text) => text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
    private static List<string> SplitValues(string text) => text.Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    private static void Shuffle(List<int> values) { for (var index = values.Count - 1; index > 0; index--) { var swap = RandomNumberGenerator.GetInt32(index + 1); (values[index], values[swap]) = (values[swap], values[index]); } }
    private void Save() { lock (_stateSync) { var temporary = StatePath + ".tmp"; File.WriteAllText(temporary, JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true })); File.Move(temporary, StatePath, true); } }
    private void WriteStatus(string message) => File.WriteAllText(Path.Combine(_context!.DataDirectory, "status.json"), JsonSerializer.Serialize(new { message, at = DateTimeOffset.UtcNow }, new JsonSerializerOptions { WriteIndented = true }));
    private void WriteRuntimeStatus(ObsSnapshot obs) => File.WriteAllText(Path.Combine(_context!.DataDirectory, "schedule-status.json"), JsonSerializer.Serialize(new { paused = _context!.Settings.Get("paused", false), obsConnected = obs.Connected, streaming = obs.Streaming, adEndsAt = _state.AdEndsAt, resumeAnnouncementsAt = _state.ResumeAnnouncementsAt, lastAutomatedMessageAt = _state.LastAutomatedMessageAt, chatMessages = _state.ChatMessages, uniqueChatters = _state.UniqueChatters.ToDictionary(pair => pair.Key, pair => pair.Value.Count), sentThisStream = _state.SentThisStream }, new JsonSerializerOptions { WriteIndented = true }));
    private static string FormatDuration(int seconds) => seconds >= 60 ? $"{Math.Max(1, seconds / 60)} minute(s)" : $"{seconds} seconds";
    public async ValueTask DisposeAsync() { if (_lifetime is not null) await StopAsync(CancellationToken.None); _sendGate.Dispose(); }

    private sealed record ObsSnapshot(bool Connected, bool Streaming);
    private sealed class AnnouncementRule
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N"); public string Name { get; set; } = "Announcement"; public bool Enabled { get; set; } = true;
        public int IntervalMinutes { get; set; } = 60; public int MinimumChatMessages { get; set; } = 10; public int MinimumUniqueChatters { get; set; }
        public bool ExcludeBroadcaster { get; set; } = true; public string UpdateWhen { get; set; } = "streaming"; public int InitialDelayMinutes { get; set; } = 5;
        public int MaximumSendsPerStream { get; set; } public string SelectionMode { get; set; } = "shuffle"; public List<string> Messages { get; set; } = [];
    }
    private sealed class RuntimeState
    {
        public Dictionary<string, int> ChatMessages { get; set; } = []; public Dictionary<string, HashSet<string>> UniqueChatters { get; set; } = [];
        public Dictionary<string, DateTimeOffset> LastSent { get; set; } = []; public Dictionary<string, int> SentThisStream { get; set; } = [];
        public Dictionary<string, int> LastMessageIndex { get; set; } = []; public Dictionary<string, int> NextMessageIndex { get; set; } = [];
        public Dictionary<string, List<int>> ShuffleRemaining { get; set; } = []; public DateTimeOffset? StreamStartedAt { get; set; }
        public DateTimeOffset? AdEndsAt { get; set; } public DateTimeOffset? ResumeAnnouncementsAt { get; set; } public int AdDurationSeconds { get; set; }
        public DateTimeOffset LastScheduleCheck { get; set; } public string? WarnedSchedule { get; set; } public DateTimeOffset LastAutomatedMessageAt { get; set; }
    }
}
