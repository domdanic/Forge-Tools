using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Forge.PluginSdk;
using System.Text.Json;

namespace Forge.App;

internal sealed record CaptureTarget(string DisplayName, string Process, string Window);
internal sealed record CaptureSwitchMapping(string CategoryId, string CategoryName, string TargetProcess, string TargetWindow, string? VideoInput, string? AudioInput);

internal sealed class CaptureSwitchMappingEditor : StackPanel
{
    private readonly TwitchAuthService _twitch;
    private readonly ObsWebSocketService _obs;
    private readonly IForgeEventBus _events;
    private readonly string? _optionsSource;
    private readonly Action<string> _status;
    private readonly TextBox _query = new() { PlaceholderText = "Search Twitch categories", MinWidth = 250 };
    private readonly ComboBox _categories = new() { MinWidth = 250 };
    private readonly ComboBox _targets = new() { MinWidth = 330 };
    private readonly ComboBox _videoInputs = new() { MinWidth = 250 };
    private readonly ComboBox _audioInputs = new() { MinWidth = 250 };
    private readonly StackPanel _cards = new();
    private CaptureSwitchMapping? _editing;

    public List<CaptureSwitchMapping> Mappings { get; } = [];
    public event EventHandler? Changed;

    public CaptureSwitchMappingEditor(TwitchAuthService twitch, ObsWebSocketService obs, IForgeEventBus events, JsonElement? saved, string? optionsSource, Action<string> status)
    {
        _twitch = twitch;
        _obs = obs;
        _events = events;
        _optionsSource = optionsSource;
        _status = status;
        Spacing = 8;
        Margin = new(0, 7, 0, 14);
        if (saved is { ValueKind: JsonValueKind.Array })
        {
            try { Mappings.AddRange(saved.Value.Deserialize<List<CaptureSwitchMapping>>() ?? []); } catch { }
        }

        var search = new Button { Content = "Search Twitch" };
        search.Click += async (_, _) => await SearchAsync();
        _query.KeyDown += async (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) await SearchAsync(); };
        var searchRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _query, search } };

        var refreshTargets = new Button { Content = "Refresh apps" };
        refreshTargets.Click += (_, _) => RefreshTargets();
        var targetRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _targets, refreshTargets } };

        var refreshObs = new Button { Content = "Refresh OBS sources" };
        refreshObs.Click += async (_, _) => await RefreshObsInputsAsync();
        var sourceRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { refreshObs } };

        var save = new Button { Content = "Save capture rule", HorizontalAlignment = HorizontalAlignment.Left };
        save.Click += (_, _) => SaveMapping();

        Children.Add(Label("When the Twitch category becomes"));
        Children.Add(searchRow);
        Children.Add(_categories);
        Children.Add(Label("Capture this running app"));
        Children.Add(targetRow);
        Children.Add(Label("Game Capture source (optional)"));
        Children.Add(_videoInputs);
        Children.Add(Label("Application Audio Capture source (optional)"));
        Children.Add(_audioInputs);
        Children.Add(sourceRow);
        Children.Add(save);
        Children.Add(new TextBlock { Text = "Saved capture rules", FontSize = 17, FontWeight = FontWeight.Bold, Margin = new(0, 12, 0, 2) });
        Children.Add(_cards);
        RefreshTargets();
        RenderCards();
    }

    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeight.Medium };

    private async Task SearchAsync()
    {
        try
        {
            var query = _query.Text?.Trim();
            if (string.IsNullOrWhiteSpace(query)) return;
            var results = await _twitch.SearchCategoriesAsync(query);
            var items = results.Select(category => new ComboBoxItem { Content = category.Name, Tag = category }).ToList();
            _categories.ItemsSource = items;
            _categories.SelectedIndex = items.Count > 0 ? 0 : -1;
            _status(items.Count == 0 ? "No matching Twitch categories found" : $"Found {items.Count} Twitch categories");
        }
        catch (Exception ex) { _status("Category search failed: " + ex.Message); }
    }

    private void RefreshTargets()
    {
        var selected = (_targets.SelectedItem as ComboBoxItem)?.Tag as CaptureTarget;
        var targets = new List<CaptureTarget>();
        if (!string.IsNullOrWhiteSpace(_optionsSource) && File.Exists(_optionsSource))
        {
            try { targets = JsonSerializer.Deserialize<List<CaptureTarget>>(File.ReadAllText(_optionsSource), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? []; }
            catch { _status("The running-app list could not be read"); }
        }
        var items = targets.Select(target => new ComboBoxItem { Content = target.DisplayName, Tag = target }).ToList();
        _targets.ItemsSource = items;
        _targets.SelectedItem = items.FirstOrDefault(item => item.Tag is CaptureTarget target && selected is not null && target.Window == selected.Window);
        if (items.Count == 0) _status("No capturable app windows found yet. Make sure the plugin is enabled, then refresh.");
    }

    private async Task RefreshObsInputsAsync()
    {
        try
        {
            if (!_obs.IsConnected) { _status("Connect Forge to OBS first"); return; }
            var result = await _obs.RequestAsync("GetInputList");
            var inputs = result.GetProperty("inputs").EnumerateArray().Select(item => new ObsInput(
                item.GetProperty("inputName").GetString() ?? "",
                item.TryGetProperty("unversionedInputKind", out var unversioned) ? unversioned.GetString() ?? "" : item.GetProperty("inputKind").GetString() ?? "")).ToList();
            SetInputItems(_videoInputs, inputs.Where(item => item.Kind.Equals("game_capture", StringComparison.OrdinalIgnoreCase)), "No video source");
            SetInputItems(_audioInputs, inputs.Where(item => item.Kind.Equals("wasapi_process_output_capture", StringComparison.OrdinalIgnoreCase)), "No audio source");
            _status("OBS capture sources refreshed");
        }
        catch (Exception ex) { _status("Could not read OBS sources: " + ex.Message); }
    }

    private static void SetInputItems(ComboBox combo, IEnumerable<ObsInput> inputs, string emptyLabel)
    {
        var selected = (combo.SelectedItem as ComboBoxItem)?.Tag as string;
        var items = new List<ComboBoxItem> { new() { Content = emptyLabel, Tag = "" } };
        items.AddRange(inputs.OrderBy(item => item.Name).Select(item => new ComboBoxItem { Content = item.Name, Tag = item.Name }));
        combo.ItemsSource = items;
        combo.SelectedItem = items.FirstOrDefault(item => string.Equals(item.Tag as string, selected, StringComparison.OrdinalIgnoreCase)) ?? items[0];
    }

    private void SaveMapping()
    {
        if (_categories.SelectedItem is not ComboBoxItem { Tag: TwitchCategory category }) { _status("Choose a Twitch category first"); return; }
        if (_targets.SelectedItem is not ComboBoxItem { Tag: CaptureTarget target }) { _status("Choose a running app first"); return; }
        var video = (_videoInputs.SelectedItem as ComboBoxItem)?.Tag as string;
        var audio = (_audioInputs.SelectedItem as ComboBoxItem)?.Tag as string;
        if (string.IsNullOrWhiteSpace(video) && string.IsNullOrWhiteSpace(audio)) { _status("Choose at least one OBS capture source"); return; }
        if (_editing is not null) Mappings.Remove(_editing);
        Mappings.RemoveAll(item => item.CategoryId == category.Id);
        Mappings.Add(new(category.Id, category.Name, target.Process, target.Window, EmptyToNull(video), EmptyToNull(audio)));
        _editing = null;
        RenderCards();
        Changed?.Invoke(this, EventArgs.Empty);
        _status($"Saved capture rule for {category.Name}");
    }

    private void RenderCards()
    {
        _cards.Children.Clear();
        foreach (var mapping in Mappings.OrderBy(item => item.CategoryName).ToList())
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var test = new Button { Content = "Test rule" };
            test.Click += async (_, _) =>
            {
                try { await _events.PublishAsync(new TwitchCategoryChanged(mapping.CategoryId, mapping.CategoryName, "forge.core.test", DateTimeOffset.UtcNow)); _status($"Tested {mapping.CategoryName}"); }
                catch (Exception ex) { _status("Capture test failed: " + ex.GetBaseException().Message); }
            };
            var edit = new Button { Content = "Edit" };
            edit.Click += (_, _) => BeginEdit(mapping);
            var remove = new Button { Content = "Remove" };
            remove.Click += (_, _) => { Mappings.Remove(mapping); RenderCards(); Changed?.Invoke(this, EventArgs.Empty); _status($"Removed {mapping.CategoryName}"); };
            actions.Children.Add(test); actions.Children.Add(edit); actions.Children.Add(remove);
            var sources = string.Join(" + ", new[] { mapping.VideoInput, mapping.AudioInput }.Where(value => !string.IsNullOrWhiteSpace(value)));
            _cards.Children.Add(new Border
            {
                Background = Brush.Parse("#191C22"), Padding = new(12), Margin = new(0, 0, 0, 8), CornerRadius = new(5),
                Child = new StackPanel { Children = { new TextBlock { Text = mapping.CategoryName, FontWeight = FontWeight.Bold }, new TextBlock { Text = $"{mapping.TargetProcess} → {sources}", Foreground = Brushes.LightGray, Margin = new(0, 2, 0, 8), TextWrapping = TextWrapping.Wrap }, actions } }
            });
        }
        if (Mappings.Count == 0) _cards.Children.Add(new TextBlock { Text = "No capture rules yet.", Foreground = Brushes.LightGray });
    }

    private void BeginEdit(CaptureSwitchMapping mapping)
    {
        _editing = mapping;
        var category = new ComboBoxItem { Content = mapping.CategoryName, Tag = new TwitchCategory(mapping.CategoryId, mapping.CategoryName) };
        _categories.ItemsSource = new[] { category }; _categories.SelectedItem = category; _query.Text = mapping.CategoryName;
        SelectTarget(mapping.TargetWindow);
        SetSavedInput(_videoInputs, mapping.VideoInput, "No video source");
        SetSavedInput(_audioInputs, mapping.AudioInput, "No audio source");
        _status($"Editing {mapping.CategoryName}");
    }

    private void SelectTarget(string window) => _targets.SelectedItem = (_targets.ItemsSource as IEnumerable<ComboBoxItem>)?.FirstOrDefault(item => item.Tag is CaptureTarget target && target.Window == window);
    private static void SetSavedInput(ComboBox combo, string? name, string emptyLabel)
    {
        var item = new ComboBoxItem { Content = name ?? emptyLabel, Tag = name ?? "" };
        combo.ItemsSource = new[] { item }; combo.SelectedItem = item;
    }
    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private sealed record ObsInput(string Name, string Kind);
}
