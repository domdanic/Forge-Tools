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
        Subscribe<TwitchFollowed>(e => AddAsync("follow", e.UserName, $"{e.UserName} followed", e.At));
        Subscribe<TwitchSubscribed>(e => e.IsGift ? Task.CompletedTask : AddAsync("subscription", e.UserName, $"{e.UserName} subscribed ({Tier(e.Tier)})", e.At));
        Subscribe<TwitchSubscriptionMessage>(e => AddAsync("subiversary", e.UserName, $"{e.UserName} subscribed for {e.CumulativeMonths} months", e.At, e.CumulativeMonths));
        Subscribe<TwitchSubscriptionGifted>(e => AddAsync("gift-subscriptions", e.IsAnonymous ? "Anonymous" : e.UserName, $"{(e.IsAnonymous ? "Anonymous" : e.UserName)} gifted {e.Total} subs", e.At, e.Total));
        Subscribe<TwitchCheered>(e => AddAsync("cheer", e.IsAnonymous ? "Anonymous" : e.UserName, $"{(e.IsAnonymous ? "Anonymous" : e.UserName)} cheered {e.Bits} bits", e.At, e.Bits));
        Subscribe<TwitchRaided>(e => AddAsync("raid", e.UserName, $"{e.UserName} raided with {e.Viewers} viewers", e.At, e.Viewers));
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

    private async Task AddAsync(string kind, string userName, string summary, DateTimeOffset at, int? quantity = null)
    {
        if (!_streaming || (kind == "follow" && !_context!.Settings.Get("includeFollows", true))) return;
        await _gate.WaitAsync();
        try
        {
            _recap.Events.Add(new(kind, userName, summary, at, quantity));
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
            if (active) BeginSession(DateTimeOffset.UtcNow);
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
        if (_context.Settings.Get("manageObsCredits", true) && _context.Connections.Obs.IsConnected)
            await UpdateObsCreditsAsync(text, cancellationToken);
    }

    private string CreditsText()
    {
        var heading = _context!.Settings.Get("creditsHeading", "Thanks for watching!").Trim();
        var lines = _recap.Events.OrderBy(e => e.At).Select(e => e.Summary);
        return string.Join(Environment.NewLine, new[] { heading, "" }.Concat(lines));
    }

    private async Task UpdateObsCreditsAsync(string text, CancellationToken cancellationToken)
    {
        var sceneName = Setting("sceneName", "Stream Credits");
        var inputName = Setting("inputName", "Forge Stream Credits");
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
        await _context.Connections.Obs.RequestAsync("SetCurrentProgramScene", new { sceneName = Setting("sceneName", "Stream Credits") }, cancellationToken);
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
    private string Setting(string key, string fallback) => string.IsNullOrWhiteSpace(_context!.Settings.Get(key, fallback)) ? fallback : _context.Settings.Get(key, fallback).Trim();
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
    private sealed record RecapEvent(string Kind, string UserName, string Summary, DateTimeOffset At, int? Quantity);
}
