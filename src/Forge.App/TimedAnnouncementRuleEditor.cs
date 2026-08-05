using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Forge.PluginSdk;
using System.Text.Json;

namespace Forge.App;

internal sealed class TimedAnnouncementRuleValue
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Announcement";
    public bool Enabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 60;
    public int MinimumChatMessages { get; set; } = 10;
    public int MinimumUniqueChatters { get; set; }
    public bool ExcludeBroadcaster { get; set; } = true;
    public string UpdateWhen { get; set; } = "streaming";
    public int InitialDelayMinutes { get; set; } = 5;
    public int MaximumSendsPerStream { get; set; }
    public string SelectionMode { get; set; } = "shuffle";
    public List<string> Messages { get; set; } = [];
}

internal sealed class TimedAnnouncementRuleEditor : StackPanel
{
    private readonly IForgeEventBus _events;
    private readonly StackPanel _cards = new();
    private readonly string? _statusPath;
    private readonly TextBlock _runtimeStatus = new() { Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap };
    private readonly Action<string> _status;
    public List<TimedAnnouncementRuleValue> Rules { get; } = [];
    public event EventHandler? Changed;

    public TimedAnnouncementRuleEditor(IForgeEventBus events, JsonElement? saved, string? statusPath, Action<string> status)
    {
        _events = events; _statusPath = statusPath; _status = status; Spacing = 8; Margin = new(0, 7, 0, 14);
        if (saved is { ValueKind: JsonValueKind.Array }) try { Rules.AddRange(saved.Value.Deserialize<List<TimedAnnouncementRuleValue>>() ?? []); } catch { }
        var add = new Button { Content = "Add announcement schedule", HorizontalAlignment = HorizontalAlignment.Left };
        add.Click += (_, _) => { Rules.Add(new() { Name = $"Announcement {Rules.Count + 1}", Messages = [""] }); Render(); Notify("Added announcement schedule"); };
        Children.Add(new TextBlock { Text = "Each schedule waits for its timer, activity, and stream condition. Message pools can shuffle, rotate in order, or select randomly.", Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap });
        var refresh = new Button { Content = "Refresh runtime status", HorizontalAlignment = HorizontalAlignment.Left };
        refresh.Click += (_, _) => RefreshRuntimeStatus();
        Children.Add(add); Children.Add(refresh); Children.Add(_runtimeStatus); Children.Add(_cards); Render(); RefreshRuntimeStatus();
    }

    private void Render()
    {
        _cards.Children.Clear();
        foreach (var rule in Rules.ToList())
        {
            var body = new StackPanel { Spacing = 6, Margin = new(0, 4, 0, 8) };
            var enabled = new CheckBox { Content = "Enabled", IsChecked = rule.Enabled };
            enabled.IsCheckedChanged += (_, _) => { rule.Enabled = enabled.IsChecked == true; Notify(); };
            var name = Field(rule.Name); name.TextChanged += (_, _) => { rule.Name = name.Text?.Trim() ?? ""; Notify(); };
            var interval = Number(rule.IntervalMinutes, value => rule.IntervalMinutes = Math.Clamp(value, 1, 10080));
            var chat = Number(rule.MinimumChatMessages, value => rule.MinimumChatMessages = Math.Clamp(value, 0, 100000));
            var unique = Number(rule.MinimumUniqueChatters, value => rule.MinimumUniqueChatters = Math.Clamp(value, 0, 100000));
            var delay = Number(rule.InitialDelayMinutes, value => rule.InitialDelayMinutes = Math.Clamp(value, 0, 1440));
            var maximum = Number(rule.MaximumSendsPerStream, value => rule.MaximumSendsPerStream = Math.Clamp(value, 0, 10000));
            var exclude = new CheckBox { Content = "Do not count the broadcaster's messages", IsChecked = rule.ExcludeBroadcaster };
            exclude.IsCheckedChanged += (_, _) => { rule.ExcludeBroadcaster = exclude.IsChecked == true; Notify(); };
            var condition = Select([("Only while OBS is streaming", "streaming"), ("Whenever OBS is connected", "obs-connected"), ("Always", "always")], rule.UpdateWhen, value => rule.UpdateWhen = value);
            var selection = Select([("Shuffle all options before repeating", "shuffle"), ("Random, without immediate repeats", "random"), ("Sequential rotation", "sequential")], rule.SelectionMode, value => rule.SelectionMode = value);
            var messages = new TextBox { Text = string.Join(Environment.NewLine, rule.Messages), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120, PlaceholderText = "One possible message per line" };
            messages.TextChanged += (_, _) => { rule.Messages = Lines(messages.Text); Notify(); };
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var test = new Button { Content = "Send test" };
            test.Click += async (_, _) => { try { await _events.PublishAsync(new TimedAnnouncementTestRequested("rule", rule.Id)); _status($"Testing {DisplayName(rule)}"); } catch (Exception ex) { _status("Test failed: " + ex.GetBaseException().Message); } };
            var duplicate = new Button { Content = "Duplicate" };
            duplicate.Click += (_, _) => { var copy = Clone(rule); copy.Id = Guid.NewGuid().ToString("N"); copy.Name = DisplayName(rule) + " copy"; Rules.Insert(Rules.IndexOf(rule) + 1, copy); Render(); Notify("Duplicated schedule"); };
            var up = new Button { Content = "Move up" }; up.Click += (_, _) => Move(rule, -1);
            var down = new Button { Content = "Move down" }; down.Click += (_, _) => Move(rule, 1);
            var remove = new Button { Content = "Remove" }; remove.Click += (_, _) => { Rules.Remove(rule); Render(); Notify($"Removed {DisplayName(rule)}"); };
            foreach (var action in new[] { test, duplicate, up, down, remove }) actions.Children.Add(action);
            body.Children.Add(enabled);
            Add(body, "Schedule name", name); Add(body, "Run this schedule", condition);
            Add(body, "Minimum minutes between sends", interval); Add(body, "Initial delay after streaming begins (minutes)", delay);
            Add(body, "Minimum chat messages since this schedule last sent", chat); Add(body, "Minimum unique chatters since this schedule last sent", unique);
            body.Children.Add(exclude); Add(body, "Maximum sends per stream (0 = unlimited)", maximum);
            Add(body, "Message selection", selection); Add(body, "Possible messages (one per line)", messages); body.Children.Add(actions);
            _cards.Children.Add(new Expander { Header = new TextBlock { Text = DisplayName(rule), FontWeight = FontWeight.Bold, FontSize = 17 }, Content = body, IsExpanded = Rules.Count == 1, Background = Brush.Parse("#191C22"), Padding = new(12), Margin = new(0, 4, 0, 4) });
        }
        if (Rules.Count == 0) _cards.Children.Add(new TextBlock { Text = "No recurring announcement schedules yet.", Foreground = Brushes.LightGray });
    }

    private TextBox Number(int value, Action<int> assign) { var field = Field(value.ToString()); field.TextChanged += (_, _) => { if (int.TryParse(field.Text, out var parsed)) assign(parsed); Notify(); }; return field; }
    private ComboBox Select((string Label, string Value)[] options, string selected, Action<string> assign)
    {
        var combo = new ComboBox(); var items = options.Select(option => new ComboBoxItem { Content = option.Label, Tag = option.Value }).ToList(); combo.ItemsSource = items; combo.SelectedItem = items.FirstOrDefault(item => Equals(item.Tag, selected)) ?? items[0];
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is ComboBoxItem { Tag: string value }) assign(value); Notify(); }; return combo;
    }
    private void Move(TimedAnnouncementRuleValue rule, int offset) { var old = Rules.IndexOf(rule); var next = Math.Clamp(old + offset, 0, Rules.Count - 1); if (old == next) return; Rules.RemoveAt(old); Rules.Insert(next, rule); Render(); Notify("Reordered schedules"); }
    private void RefreshRuntimeStatus()
    {
        if (string.IsNullOrWhiteSpace(_statusPath) || !File.Exists(_statusPath)) { _runtimeStatus.Text = "Runtime status will appear after the enabled plugin starts."; return; }
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_statusPath)); var root = document.RootElement;
            var paused = root.TryGetProperty("paused", out var pausedElement) && pausedElement.GetBoolean();
            var connected = root.TryGetProperty("obsConnected", out var connectedElement) && connectedElement.GetBoolean();
            var streaming = root.TryGetProperty("streaming", out var streamingElement) && streamingElement.GetBoolean();
            var activity = new List<string>();
            foreach (var rule in Rules)
            {
                var messages = ReadCount(root, "chatMessages", rule.Id); var unique = ReadCount(root, "uniqueChatters", rule.Id); var sent = ReadCount(root, "sentThisStream", rule.Id);
                activity.Add($"{DisplayName(rule)}: {messages}/{rule.MinimumChatMessages} messages, {unique}/{rule.MinimumUniqueChatters} unique, {sent} sent this stream");
            }
            _runtimeStatus.Text = $"{(paused ? "Paused" : "Running")} · OBS {(connected ? "connected" : "disconnected")} · {(streaming ? "streaming" : "not streaming")}" + (activity.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, activity));
        }
        catch { _runtimeStatus.Text = "Runtime status could not be read yet."; }
    }
    private static int ReadCount(JsonElement root, string property, string id) => root.TryGetProperty(property, out var values) && values.ValueKind == JsonValueKind.Object && values.TryGetProperty(id, out var value) && value.TryGetInt32(out var count) ? count : 0;
    private static TimedAnnouncementRuleValue Clone(TimedAnnouncementRuleValue rule) => new() { Name = rule.Name, Enabled = rule.Enabled, IntervalMinutes = rule.IntervalMinutes, MinimumChatMessages = rule.MinimumChatMessages, MinimumUniqueChatters = rule.MinimumUniqueChatters, ExcludeBroadcaster = rule.ExcludeBroadcaster, UpdateWhen = rule.UpdateWhen, InitialDelayMinutes = rule.InitialDelayMinutes, MaximumSendsPerStream = rule.MaximumSendsPerStream, SelectionMode = rule.SelectionMode, Messages = [.. rule.Messages] };
    private void Notify(string? message = null) { Changed?.Invoke(this, EventArgs.Empty); if (message is not null) _status(message); }
    private static void Add(Panel panel, string label, Control control) { panel.Children.Add(Label(label)); panel.Children.Add(control); }
    private static List<string> Lines(string? text) => (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
    private static TextBox Field(string text) => new() { Text = text, MinWidth = 220 };
    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeight.Medium };
    private static string DisplayName(TimedAnnouncementRuleValue rule) => string.IsNullOrWhiteSpace(rule.Name) ? "Untitled announcement" : rule.Name;
}
