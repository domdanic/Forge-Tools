using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Forge.PluginSdk;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Forge.App;

internal sealed record ProcessCategoryMapping(string Process, string CategoryId, string CategoryName);

internal sealed class ProcessCategoryMappingEditor : StackPanel
{
    private readonly TwitchAuthService _twitch;
    private readonly Action<string> _status;
    private readonly ComboBox _process = new() { MinWidth = 260 };
    private readonly TextBox _query = new() { PlaceholderText = "Search Twitch categories", MinWidth = 260 };
    private readonly ComboBox _categories = new() { MinWidth = 260 };
    private readonly StackPanel _cards = new();
    private ProcessCategoryMapping? _editing;

    public List<ProcessCategoryMapping> Mappings { get; } = [];
    public event EventHandler? Changed;

    public ProcessCategoryMappingEditor(TwitchAuthService twitch, JsonElement? saved, Action<string> status)
    {
        _twitch = twitch;
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
        Children.Add(new TextBlock { Text = "Running app" });
        Children.Add(processRow);
        Children.Add(new TextBlock { Text = "Twitch category" });
        Children.Add(searchRow);
        Children.Add(_categories);
        Children.Add(save);
        Children.Add(new TextBlock { Text = "Saved games", FontSize = 17, FontWeight = FontWeight.Bold, Margin = new(0, 12, 0, 2) });
        Children.Add(_cards);
        RenderCards();
    }

    private void RefreshProcesses()
    {
        var selected = (_process.SelectedItem as ComboBoxItem)?.Tag as string;
        var names = OperatingSystem.IsWindows() ? WindowsApps.ListProcessNames() : ListProcesses();
        var items = names.Select(name => new ComboBoxItem { Content = name, Tag = name }).ToList();
        _process.ItemsSource = items;
        _process.SelectedItem = items.FirstOrDefault(item => string.Equals(item.Tag as string, selected, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> ListProcesses() => Process.GetProcesses().Select(process =>
    {
        try { return process.ProcessName; }
        catch { return null; }
        finally { process.Dispose(); }
    }).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name).ToList()!;

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
        if (_editing is not null) Mappings.Remove(_editing);
        Mappings.RemoveAll(item => item.Process.Equals(process, StringComparison.OrdinalIgnoreCase));
        Mappings.Add(new(process, category.Id, category.Name));
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
            switchNow.Click += async (_, _) => { try { await _twitch.UpdateCategoryAsync(mapping.CategoryId); _status($"Switched Twitch to {mapping.CategoryName}"); } catch (Exception ex) { _status("Category switch failed: " + ex.Message); } };
            var edit = new Button { Content = "Edit" };
            edit.Click += (_, _) => BeginEdit(mapping);
            var remove = new Button { Content = "Remove" };
            remove.Click += (_, _) => { Mappings.Remove(mapping); RenderCards(); Changed?.Invoke(this, EventArgs.Empty); _status($"Removed {mapping.Process}"); };
            actions.Children.Add(switchNow); actions.Children.Add(edit); actions.Children.Add(remove);
            var content = new StackPanel { Children = { new TextBlock { Text = mapping.Process, FontWeight = FontWeight.Bold }, new TextBlock { Text = mapping.CategoryName, Foreground = Brushes.LightGray, Margin = new(0, 2, 0, 8) }, actions } };
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
        _status($"Editing {mapping.Process}");
    }

    private static class WindowsApps
    {
        private const int DwmwaCloaked = 14;

        public static List<string> ListProcessNames()
        {
            var processIds = new HashSet<uint>();
            EnumWindows((window, _) =>
            {
                if (!IsWindowVisible(window) || GetWindowTextLength(window) == 0 || GetWindow(window, 4) != IntPtr.Zero) return true;
                if (DwmGetWindowAttribute(window, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                GetWindowThreadProcessId(window, out var processId);
                if (processId != Environment.ProcessId) processIds.Add(processId);
                return true;
            }, IntPtr.Zero);

            return processIds.Select(processId =>
            {
                try { using var process = Process.GetProcessById((int)processId); return process.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? process.ProcessName : process.ProcessName + ".exe"; }
                catch { return null; }
            }).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name).ToList()!;
        }

        private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    }
}
