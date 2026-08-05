using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Forge.PluginSdk;
using System.Text.Json;

namespace Forge.App;

internal sealed record ProcessCategoryMapping(string Process, string CategoryId, string CategoryName, string ChatCommand = "", string CommandAccess = "moderators");

internal sealed class ProcessCategoryMappingEditor : StackPanel
{
    private readonly TwitchAuthService _twitch;
    private readonly IForgeEventBus _events;
    private readonly Action<string> _status;
    private readonly string? _optionsSource;
    private readonly ComboBox _process = new() { MinWidth = 260 };
    private readonly TextBox _query = new() { PlaceholderText = "Search Twitch categories", MinWidth = 260 };
    private readonly ComboBox _categories = new() { MinWidth = 260 };
    private readonly TextBox _command = new() { PlaceholderText = "Optional, for example !minecraft", MinWidth = 260 };
    private readonly ComboBox _commandAccess = new() { MinWidth = 260 };
    private readonly StackPanel _cards = new();
    private ProcessCategoryMapping? _editing;

    public List<ProcessCategoryMapping> Mappings { get; } = [];
    public event EventHandler? Changed;

    public ProcessCategoryMappingEditor(TwitchAuthService twitch, IForgeEventBus events, JsonElement? saved, string? optionsSource, Action<string> status)
    {
        _twitch = twitch;
        _events = events;
        _optionsSource = optionsSource;
        _status = status;
        Spacing = 8;
        Margin = new(0, 7, 0, 14);
        if (saved is { ValueKind: JsonValueKind.Array })
        {
            try { Mappings.AddRange(saved.Value.Deserialize<List<ProcessCategoryMapping>>() ?? []); } catch { }
        }

        RefreshProcesses();
        var refresh = new Button { Content = "Refresh processes" };
        refresh.Click += (_, _) => RefreshProcesses();
        var processRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _process, refresh } };

        var search = new Button { Content = "Search Twitch" };
        search.Click += async (_, _) => await SearchAsync();
        _query.KeyDown += async (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) await SearchAsync(); };
        var searchRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _query, search } };

        var save = new Button { Content = "Add saved game", HorizontalAlignment = HorizontalAlignment.Left };
        save.Click += (_, _) => SaveMapping();
        var accessItems = new[]
        {
            new ComboBoxItem { Content = "Broadcaster and moderators", Tag = "moderators" },
            new ComboBoxItem { Content = "Broadcaster only", Tag = "broadcaster" },
            new ComboBoxItem { Content = "Everyone", Tag = "everyone" }
        };
        _commandAccess.ItemsSource = accessItems;
        _commandAccess.SelectedItem = accessItems[0];
        Children.Add(new TextBlock { Text = "Running app" });
        Children.Add(processRow);
        Children.Add(new TextBlock { Text = "Twitch category" });
        Children.Add(searchRow);
        Children.Add(_categories);
        Children.Add(new TextBlock { Text = "Optional Twitch chat command" });
        Children.Add(_command);
        Children.Add(new TextBlock { Text = "Who can use the command" });
        Children.Add(_commandAccess);
        Children.Add(save);
        Children.Add(new TextBlock { Text = "Saved games", FontSize = 17, FontWeight = FontWeight.Bold, Margin = new(0, 12, 0, 2) });
        Children.Add(_cards);
        RenderCards();
    }

    private void RefreshProcesses()
    {
        var selected = (_process.SelectedItem as ComboBoxItem)?.Tag as string;
        var names = new List<string>();
        if (!string.IsNullOrWhiteSpace(_optionsSource) && File.Exists(_optionsSource))
        {
            try { names = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_optionsSource)) ?? []; }
            catch { _status("The plugin's running-app list could not be read"); }
        }
        var items = names.Select(name => new ComboBoxItem { Content = name, Tag = name }).ToList();
        _process.ItemsSource = items;
        _process.SelectedItem = items.FirstOrDefault(item => string.Equals(item.Tag as string, selected, StringComparison.OrdinalIgnoreCase));
        if (items.Count == 0) _status("No visible apps reported by the plugin yet. Make sure its permissions are granted, then refresh.");
    }

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

    private void SaveMapping()
    {
        if (_process.SelectedItem is not ComboBoxItem { Tag: string process } || _categories.SelectedItem is not ComboBoxItem { Tag: TwitchCategory category })
        { _status("Choose a running process and a Twitch category first"); return; }
        var command = NormalizeCommand(_command.Text);
        if (command.Length > 0 && Mappings.Any(item => !ReferenceEquals(item, _editing) && item.ChatCommand.Equals(command, StringComparison.OrdinalIgnoreCase)))
        { _status($"The chat command {command} is already assigned to another saved game"); return; }
        var access = (_commandAccess.SelectedItem as ComboBoxItem)?.Tag as string ?? "moderators";
        if (_editing is not null) Mappings.Remove(_editing);
        Mappings.RemoveAll(item => item.Process.Equals(process, StringComparison.OrdinalIgnoreCase));
        Mappings.Add(new(process, category.Id, category.Name, command, access));
        _editing = null;
        RenderCards(); Changed?.Invoke(this, EventArgs.Empty);
        _status($"Saved {process} → {category.Name}");
    }

    private void RenderCards()
    {
        _cards.Children.Clear();
        foreach (var mapping in Mappings.OrderBy(item => item.Process).ToList())
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var switchNow = new Button { Content = "Switch now" };
            switchNow.Click += async (_, _) =>
            {
                try
                {
                    await _twitch.UpdateCategoryAsync(mapping.CategoryId);
                    await _events.PublishAsync(new TwitchCategoryChanged(mapping.CategoryId, mapping.CategoryName, "forge.core.manual", DateTimeOffset.UtcNow));
                    _status($"Switched Twitch to {mapping.CategoryName}");
                }
                catch (Exception ex) { _status("Category switch failed: " + ex.Message); }
            };
            var edit = new Button { Content = "Edit" };
            edit.Click += (_, _) => BeginEdit(mapping);
            var remove = new Button { Content = "Remove" };
            remove.Click += (_, _) => { Mappings.Remove(mapping); RenderCards(); Changed?.Invoke(this, EventArgs.Empty); _status($"Removed {mapping.Process}"); };
            actions.Children.Add(switchNow); actions.Children.Add(edit); actions.Children.Add(remove);
            var commandText = string.IsNullOrWhiteSpace(mapping.ChatCommand) ? "No chat command" : $"{mapping.ChatCommand} · {AccessLabel(mapping.CommandAccess)}";
            var content = new StackPanel { Children = { new TextBlock { Text = mapping.Process, FontWeight = FontWeight.Bold }, new TextBlock { Text = mapping.CategoryName, Foreground = Brushes.LightGray, Margin = new(0, 2, 0, 2) }, new TextBlock { Text = commandText, Foreground = Brushes.LightGray, Margin = new(0, 0, 0, 8) }, actions } };
            _cards.Children.Add(new Border { Background = Brush.Parse("#191C22"), Padding = new(12), Margin = new(0, 0, 0, 8), CornerRadius = new(5), Child = content });
        }
        if (Mappings.Count == 0) _cards.Children.Add(new TextBlock { Text = "No saved games yet.", Foreground = Brushes.LightGray });
    }

    private void BeginEdit(ProcessCategoryMapping mapping)
    {
        _editing = mapping;
        _process.SelectedItem = (_process.ItemsSource as IEnumerable<ComboBoxItem>)?.FirstOrDefault(item => string.Equals(item.Tag as string, mapping.Process, StringComparison.OrdinalIgnoreCase));
        var item = new ComboBoxItem { Content = mapping.CategoryName, Tag = new TwitchCategory(mapping.CategoryId, mapping.CategoryName) };
        _categories.ItemsSource = new[] { item }; _categories.SelectedItem = item; _query.Text = mapping.CategoryName;
        _command.Text = mapping.ChatCommand;
        _commandAccess.SelectedItem = (_commandAccess.ItemsSource as IEnumerable<ComboBoxItem>)?.FirstOrDefault(item => string.Equals(item.Tag as string, mapping.CommandAccess, StringComparison.OrdinalIgnoreCase)) ?? (_commandAccess.ItemsSource as IEnumerable<ComboBoxItem>)?.First();
        _status($"Editing {mapping.Process}");
    }

    private static string NormalizeCommand(string? value)
    {
        var command = value?.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (command.Length > 0 && !command.StartsWith('!')) command = "!" + command;
        return command;
    }

    private static string AccessLabel(string access) => access switch { "broadcaster" => "Broadcaster only", "everyone" => "Everyone", _ => "Broadcaster and moderators" };

}
