using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System.Text.Json;

namespace Forge.App;

internal sealed class TimedAnnouncementRuleValue
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Announcement";
    public bool Enabled { get; set; } = true;
    public int IntervalMinutes { get; set; } = 60;
    public int MinimumChatMessages { get; set; } = 10;
    public List<string> Messages { get; set; } = [];
}

internal sealed class TimedAnnouncementRuleEditor : StackPanel
{
    private readonly StackPanel _cards = new();
    private readonly Action<string> _status;
    public List<TimedAnnouncementRuleValue> Rules { get; } = [];
    public event EventHandler? Changed;

    public TimedAnnouncementRuleEditor(JsonElement? saved, Action<string> status)
    {
        _status = status;
        Spacing = 8;
        Margin = new(0, 7, 0, 14);
        if (saved is { ValueKind: JsonValueKind.Array })
        {
            try { Rules.AddRange(saved.Value.Deserialize<List<TimedAnnouncementRuleValue>>() ?? []); } catch { }
        }
        var add = new Button { Content = "Add announcement schedule", HorizontalAlignment = HorizontalAlignment.Left };
        add.Click += (_, _) =>
        {
            Rules.Add(new TimedAnnouncementRuleValue { Name = $"Announcement {Rules.Count + 1}", Messages = [""] });
            Render(); Changed?.Invoke(this, EventArgs.Empty); _status("Added announcement schedule");
        };
        Children.Add(new TextBlock
        {
            Text = "Each schedule waits for both its timer and chat-activity requirement. Add multiple message options on separate lines; one is chosen randomly each time.",
            Foreground = Brushes.LightGray,
            TextWrapping = TextWrapping.Wrap
        });
        Children.Add(add);
        Children.Add(_cards);
        Render();
    }

    private void Render()
    {
        _cards.Children.Clear();
        foreach (var rule in Rules.ToList())
        {
            var enabled = new CheckBox { Content = "Enabled", IsChecked = rule.Enabled };
            enabled.IsCheckedChanged += (_, _) => { rule.Enabled = enabled.IsChecked == true; Changed?.Invoke(this, EventArgs.Empty); };
            var name = Field(rule.Name);
            name.TextChanged += (_, _) => { rule.Name = name.Text?.Trim() ?? ""; Changed?.Invoke(this, EventArgs.Empty); };
            var interval = Field(rule.IntervalMinutes.ToString());
            interval.TextChanged += (_, _) => { if (int.TryParse(interval.Text, out var value)) rule.IntervalMinutes = Math.Clamp(value, 1, 10080); Changed?.Invoke(this, EventArgs.Empty); };
            var chat = Field(rule.MinimumChatMessages.ToString());
            chat.TextChanged += (_, _) => { if (int.TryParse(chat.Text, out var value)) rule.MinimumChatMessages = Math.Clamp(value, 0, 100000); Changed?.Invoke(this, EventArgs.Empty); };
            var messages = new TextBox
            {
                Text = string.Join(Environment.NewLine, rule.Messages),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 120,
                PlaceholderText = "One possible message per line"
            };
            messages.TextChanged += (_, _) =>
            {
                rule.Messages = (messages.Text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
                Changed?.Invoke(this, EventArgs.Empty);
            };
            var remove = new Button { Content = "Remove schedule", HorizontalAlignment = HorizontalAlignment.Left };
            remove.Click += (_, _) => { Rules.Remove(rule); Render(); Changed?.Invoke(this, EventArgs.Empty); _status($"Removed {DisplayName(rule)}"); };
            var body = new StackPanel { Spacing = 6, Margin = new(0, 4, 0, 8) };
            body.Children.Add(enabled);
            body.Children.Add(Label("Schedule name")); body.Children.Add(name);
            body.Children.Add(Label("Minimum minutes between sends")); body.Children.Add(interval);
            body.Children.Add(Label("Minimum chat messages since this schedule last sent")); body.Children.Add(chat);
            body.Children.Add(Label("Possible messages (one per line)")); body.Children.Add(messages);
            body.Children.Add(remove);
            _cards.Children.Add(new Expander
            {
                Header = new TextBlock { Text = DisplayName(rule), FontWeight = FontWeight.Bold, FontSize = 17 },
                Content = body,
                IsExpanded = Rules.Count == 1,
                Background = Brush.Parse("#191C22"),
                Padding = new(12),
                Margin = new(0, 4, 0, 4)
            });
        }
        if (Rules.Count == 0) _cards.Children.Add(new TextBlock { Text = "No recurring announcement schedules yet.", Foreground = Brushes.LightGray });
    }

    private static TextBox Field(string text) => new() { Text = text, MinWidth = 220 };
    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeight.Medium };
    private static string DisplayName(TimedAnnouncementRuleValue rule) => string.IsNullOrWhiteSpace(rule.Name) ? "Untitled announcement" : rule.Name;
}
