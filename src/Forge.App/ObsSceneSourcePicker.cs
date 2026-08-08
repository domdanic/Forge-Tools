using Avalonia.Controls;
using Avalonia.Layout;
using System.Text.Json;

namespace Forge.App;

internal sealed record ObsSceneSourceSelection(string SceneName, string InputName);

internal sealed class ObsSceneSourcePicker : StackPanel
{
    private readonly ObsWebSocketService _obs;
    private readonly Action<string> _status;
    private readonly ComboBox _scenes = new() { MinWidth = 300 };
    private readonly ComboBox _sources = new() { MinWidth = 300 };
    private ObsSceneSourceSelection _saved;

    public ObsSceneSourceSelection Selection => new(Selected(_scenes), Selected(_sources));
    public event EventHandler? Changed;

    public ObsSceneSourcePicker(ObsWebSocketService obs, JsonElement? saved, Action<string> status)
    {
        _obs = obs;
        _status = status;
        _saved = Read(saved);
        Spacing = 8;
        Margin = new(0, 7, 0, 14);
        _scenes.SelectionChanged += async (_, _) => { await RefreshSourcesAsync(); Changed?.Invoke(this, EventArgs.Empty); };
        _sources.SelectionChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        var refresh = new Button { Content = "Refresh OBS scenes and sources", HorizontalAlignment = HorizontalAlignment.Left };
        refresh.Click += async (_, _) => await RefreshAsync();
        Children.Add(new TextBlock { Text = "Scene" });
        Children.Add(_scenes);
        Children.Add(new TextBlock { Text = "Existing text source in that scene" });
        Children.Add(_sources);
        Children.Add(refresh);
        SetSavedItems();
    }

    private async Task RefreshAsync()
    {
        try
        {
            if (!_obs.IsConnected) { _status("Connect Forge to OBS first"); return; }
            var selected = Selected(_scenes);
            var result = await _obs.RequestAsync("GetSceneList");
            var scenes = result.GetProperty("scenes").EnumerateArray()
                .Select(item => item.GetProperty("sceneName").GetString() ?? "")
                .Where(name => name.Length > 0).OrderBy(name => name).ToList();
            _scenes.ItemsSource = scenes.Select(Item).ToList();
            Select(_scenes, selected.Length > 0 ? selected : _saved.SceneName);
            if (_scenes.SelectedItem is null && scenes.Count > 0) _scenes.SelectedIndex = 0;
            await RefreshSourcesAsync();
            _status($"Found {scenes.Count} OBS scenes");
        }
        catch (Exception ex) { _status("Could not read OBS scenes: " + ex.Message); }
    }

    private async Task RefreshSourcesAsync()
    {
        var sceneName = Selected(_scenes);
        if (!_obs.IsConnected || sceneName.Length == 0) return;
        try
        {
            var selected = Selected(_sources);
            var result = await _obs.RequestAsync("GetSceneItemList", new { sceneName });
            var sources = result.GetProperty("sceneItems").EnumerateArray()
                .Where(item => item.TryGetProperty("inputKind", out var kind) && IsTextInput(kind.GetString()))
                .Select(item => item.GetProperty("sourceName").GetString() ?? "")
                .Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name).ToList();
            _sources.ItemsSource = sources.Select(Item).ToList();
            Select(_sources, selected.Length > 0 ? selected : _saved.InputName);
            if (_sources.SelectedItem is null && sources.Count > 0) _sources.SelectedIndex = 0;
            if (sources.Count == 0) _status($"No OBS text sources are present in {sceneName}");
        }
        catch (Exception ex) { _status("Could not read OBS scene sources: " + ex.Message); }
    }

    private void SetSavedItems()
    {
        _scenes.ItemsSource = _saved.SceneName.Length == 0 ? [] : new[] { Item(_saved.SceneName) };
        _sources.ItemsSource = _saved.InputName.Length == 0 ? [] : new[] { Item(_saved.InputName) };
        _scenes.SelectedIndex = _saved.SceneName.Length == 0 ? -1 : 0;
        _sources.SelectedIndex = _saved.InputName.Length == 0 ? -1 : 0;
    }

    private static ObsSceneSourceSelection Read(JsonElement? saved)
    {
        try { return saved is { ValueKind: JsonValueKind.Object } ? saved.Value.Deserialize<ObsSceneSourceSelection>() ?? new("", "") : new("", ""); }
        catch { return new("", ""); }
    }
    private static bool IsTextInput(string? kind) => kind is not null && (kind.StartsWith("text_", StringComparison.OrdinalIgnoreCase) || kind.Contains("text", StringComparison.OrdinalIgnoreCase));
    private static ComboBoxItem Item(string value) => new() { Content = value, Tag = value };
    private static string Selected(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
    private static void Select(ComboBox combo, string value) => combo.SelectedItem = (combo.ItemsSource as IEnumerable<ComboBoxItem>)?.FirstOrDefault(item => string.Equals(item.Tag as string, value, StringComparison.OrdinalIgnoreCase));
}
