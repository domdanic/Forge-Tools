using System.Text.Json;
using Forge.PluginSdk;

namespace Forge.StreamRecap;

public sealed class StreamRecapPlugin : IForgePlugin
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly List<IDisposable> _subscriptions = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IForgeContext? _context;
    private RecapDocument _recap = new();
    private bool _streaming;

    public Task InitializeAsync(IForgeContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _recap = Load();
        return Task.CompletedTask;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        Subscribe<TwitchFollowed>(e => AddAsync("follow", e.UserName, e.At));
        Subscribe<TwitchSubscribed>(e => e.IsGift ? Task.CompletedTask : AddAsync("subscription", e.UserName, e.At, tier: Tier(e.Tier)));
        Subscribe<TwitchSubscriptionMessage>(e => AddAsync("subiversary", e.UserName, e.At, e.CumulativeMonths, Tier(e.Tier)));
        Subscribe<TwitchSubscriptionGifted>(e => AddAsync("gift-subscriptions", e.IsAnonymous ? "Anonymous" : e.UserName, e.At, e.Total, Tier(e.Tier)));
        Subscribe<TwitchCheered>(e => AddAsync("cheer", e.IsAnonymous ? "Anonymous" : e.UserName, e.At, e.Bits));
        Subscribe<TwitchRaided>(e => AddAsync("raid", e.UserName, e.At, e.Viewers));
        Subscribe<TwitchChatMessage>(OnChatAsync);
        _subscriptions.Add(_context!.Events.Subscribe<ObsEvent>(OnObsEventAsync));
        _context.Settings.Changed += OnSettingsChanged;
        _streaming = await GetStreamingAsync(cancellationToken);
        if (_streaming && _recap.EndedAt is not null) BeginSession(DateTimeOffset.UtcNow);
        await RenderAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var subscription in _subscriptions) subscription.Dispose();
        _subscriptions.Clear();
        if (_context is not null) _context.Settings.Changed -= OnSettingsChanged;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() { await StopAsync(CancellationToken.None); _gate.Dispose(); }

    private void Subscribe<T>(Func<T, Task> handler) => _subscriptions.Add(_context!.Events.Subscribe(handler));

    private async Task OnChatAsync(TwitchChatMessage message)
    {
        if (!_streaming || !message.IsModerator || message.IsBroadcaster || string.IsNullOrWhiteSpace(message.UserName)) return;
        await _gate.WaitAsync();
        try
        {
            if (_recap.Events.Any(item => item.Kind == "moderator" && (item.UserId == message.UserId || item.UserName.Equals(message.UserName, StringComparison.OrdinalIgnoreCase)))) return;
            _recap.Events.Add(new() { Kind = "moderator", UserId = message.UserId, UserName = message.UserName, At = message.At });
            Trim();
            await RenderCoreAsync(CancellationToken.None);
        }
        finally { _gate.Release(); }
    }

    private async Task AddAsync(string kind, string userName, DateTimeOffset at, int? quantity = null, string? tier = null)
    {
        if (!_streaming) return;
        await _gate.WaitAsync();
        try
        {
            _recap.Events.Add(new() { Kind = kind, UserName = userName, At = at, Quantity = quantity, Tier = tier });
            Trim();
            await RenderCoreAsync(CancellationToken.None);
        }
        finally { _gate.Release(); }
    }

    private async Task OnObsEventAsync(ObsEvent message)
    {
        if (!message.EventType.Equals("StreamStateChanged", StringComparison.Ordinal)) return;
        var active = message.Data.TryGetProperty("outputActive", out var outputActive) && outputActive.GetBoolean();
        if (active == _streaming) return;
        _streaming = active;
        await _gate.WaitAsync();
        try
        {
            if (active)
            {
                BeginSession(DateTimeOffset.UtcNow);
                await RenderCoreAsync(CancellationToken.None);
            }
            else
            {
                _recap.EndedAt = DateTimeOffset.UtcNow;
                await RenderCoreAsync(CancellationToken.None);
                if (_context!.Settings.Get("switchSceneAtStreamEnd", false)) await SwitchToCreditsSceneAsync(CancellationToken.None);
            }
        }
        finally { _gate.Release(); }
    }

    private void BeginSession(DateTimeOffset at)
    {
        _recap = new() { StartedAt = at };
        Save();
    }

    private async Task RenderAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try { await RenderCoreAsync(cancellationToken); }
        finally { _gate.Release(); }
    }

    private async Task RenderCoreAsync(CancellationToken cancellationToken)
    {
        Trim();
        Save();
        var text = CreditsText();
        await File.WriteAllTextAsync(Path.Combine(_context!.DataDirectory, "credits.txt"), text, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_context.DataDirectory, "credits-status.json"), JsonSerializer.Serialize(new
        {
            streaming = _streaming,
            totalRecordedEventCount = _recap.Events.Count,
            includedEventCount = IncludedEvents().Count,
            creditsText = text
        }, JsonOptions), cancellationToken);
        if (_context.Settings.Get("manageObsCredits", true) && _context.Connections.Obs.IsConnected)
            await UpdateObsCreditsAsync(text, cancellationToken);
    }

    private string CreditsText()
    {
        var heading = _context!.Settings.Get("creditsHeading", "Thanks for watching!").Trim();
        var events = IncludedEvents();
        var order = _context.Settings.Get("categoryOrder", DefaultCategoryOrder);
        var lines = new List<string> { heading };
        foreach (var category in order.Concat(DefaultCategoryOrder).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var items = events.Where(item => Category(item.Kind) == category).OrderBy(item => item.At).ToList();
            if (items.Count == 0) continue;
            lines.Add("");
            lines.Add(Heading(category));
            lines.AddRange(items.Select(Format));
        }
        return string.Join(Environment.NewLine, lines);
    }

    private List<RecapEvent> IncludedEvents() => _recap.Events.Where(item => _context!.Settings.Get("include" + ToggleSuffix(item.Kind), true)).ToList();
    private string Format(RecapEvent item)
    {
        var fallback = item.Kind switch
        {
            "moderator" => "{user}", "follow" => "{user}", "subscription" => "{user} ({tier})",
            "subiversary" => "{user} — {quantity} months", "gift-subscriptions" => "{user} — {quantity} gifted subs",
            "cheer" => "{user} — {quantity} bits", "raid" => "{user} — {quantity} viewers", _ => item.Summary ?? "{user}"
        };
        var template = _context!.Settings.Get("template" + ToggleSuffix(item.Kind), fallback);
        return template.Replace("{user}", item.UserName, StringComparison.OrdinalIgnoreCase)
            .Replace("{quantity}", item.Quantity?.ToString() ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{months}", item.Quantity?.ToString() ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{tier}", item.Tier ?? "", StringComparison.OrdinalIgnoreCase);
    }
    private string Heading(string category) => _context!.Settings.Get("heading" + category switch
    {
        "moderators" => "Moderators", "subscriptions" => "Subscriptions", "gifts" => "Gifts",
        "cheers" => "Cheers", "raids" => "Raids", _ => "Follows"
    }, category switch { "moderators" => "Mods who dropped in", "subscriptions" => "Subscribers", "gifts" => "Gifted subs", "cheers" => "Cheers", "raids" => "Raids", _ => "New followers" });
    private static string Category(string kind) => kind switch { "moderator" => "moderators", "subscription" or "subiversary" => "subscriptions", "gift-subscriptions" => "gifts", "cheer" => "cheers", "raid" => "raids", _ => "follows" };
    private static string ToggleSuffix(string kind) => kind switch { "moderator" => "Moderators", "subscription" => "Subscriptions", "subiversary" => "Subiversaries", "gift-subscriptions" => "Gifts", "cheer" => "Cheers", "raid" => "Raids", _ => "Follows" };
    private static readonly string[] DefaultCategoryOrder = ["moderators", "subscriptions", "gifts", "cheers", "raids", "follows"];

    private async Task UpdateObsCreditsAsync(string text, CancellationToken cancellationToken)
    {
        var target = _context!.Settings.Get("obsCreditsTarget", new ObsTarget("", ""));
        var sceneName = string.IsNullOrWhiteSpace(target.SceneName) ? "Stream Credits" : target.SceneName;
        var inputName = string.IsNullOrWhiteSpace(target.InputName) ? "Forge Stream Credits" : target.InputName;
        try { await _context!.Connections.Obs.RequestAsync("GetSceneItemList", new { sceneName }, cancellationToken); }
        catch { await _context!.Connections.Obs.RequestAsync("CreateScene", new { sceneName }, cancellationToken); }
        try
        {
            await _context!.Connections.Obs.RequestAsync("SetInputSettings", new { inputName, inputSettings = new { text }, overlay = true }, cancellationToken);
        }
        catch
        {
            var inputKind = OperatingSystem.IsWindows() ? "text_gdiplus_v2" : "text_ft2_source_v2";
            await _context!.Connections.Obs.RequestAsync("CreateInput", new { sceneName, inputName, inputKind, inputSettings = new { text }, sceneItemEnabled = true }, cancellationToken);
        }
    }

    private async Task SwitchToCreditsSceneAsync(CancellationToken cancellationToken)
    {
        if (!_context!.Connections.Obs.IsConnected || !_context.Settings.Get("manageObsCredits", true)) return;
        var target = _context.Settings.Get("obsCreditsTarget", new ObsTarget("", ""));
        await _context.Connections.Obs.RequestAsync("SetCurrentProgramScene", new { sceneName = string.IsNullOrWhiteSpace(target.SceneName) ? "Stream Credits" : target.SceneName }, cancellationToken);
    }

    private async Task<bool> GetStreamingAsync(CancellationToken cancellationToken)
    {
        if (!_context!.Connections.Obs.IsConnected) return false;
        try
        {
            var result = await _context.Connections.Obs.RequestAsync("GetStreamStatus", cancellationToken: cancellationToken);
            return result.TryGetProperty("outputActive", out var active) && active.GetBoolean();
        }
        catch { return false; }
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => _ = RenderAsync(CancellationToken.None);
    private void Trim()
    {
        var maximum = int.TryParse(_context!.Settings.Get("maximumEvents", "100"), out var parsed) ? Math.Clamp(parsed, 1, 1000) : 100;
        if (_recap.Events.Count > maximum) _recap.Events.RemoveRange(0, _recap.Events.Count - maximum);
    }
    private void Save() => File.WriteAllText(Path.Combine(_context!.DataDirectory, "recap.json"), JsonSerializer.Serialize(_recap, JsonOptions));
    private RecapDocument Load() { try { return JsonSerializer.Deserialize<RecapDocument>(File.ReadAllText(Path.Combine(_context!.DataDirectory, "recap.json"))) ?? new(); } catch { return new(); } }
    private static string Tier(string tier) => tier switch { "1000" => "Tier 1", "2000" => "Tier 2", "3000" => "Tier 3", _ => tier };

    private sealed class RecapDocument
    {
        public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? EndedAt { get; set; }
        public List<RecapEvent> Events { get; set; } = [];
    }
    private sealed class RecapEvent
    {
        public string Kind { get; set; } = "";
        public string UserId { get; set; } = "";
        public string UserName { get; set; } = "";
        public string? Summary { get; set; }
        public DateTimeOffset At { get; set; }
        public int? Quantity { get; set; }
        public string? Tier { get; set; }
    }
    private sealed record ObsTarget(string SceneName, string InputName);
}
