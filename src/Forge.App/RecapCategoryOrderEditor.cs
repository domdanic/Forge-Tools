using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using System.Text.Json;

namespace Forge.App;

internal sealed record RecapCategoryOption(string Id, string Label);

internal sealed class RecapCategoryOrderEditor : StackPanel
{
    private static readonly RecapCategoryOption[] Defaults =
    [
        new("moderators", "Moderators"), new("subscriptions", "Subscriptions & subiversaries"),
        new("gifts", "Gifted subscriptions"), new("cheers", "Cheers"), new("raids", "Raids"), new("follows", "Follows")
    ];
    private readonly StackPanel _cards = new() { Spacing = 6 };
    private string? _dragging;

    public List<string> Order { get; } = [];
    public event EventHandler? Changed;

    public RecapCategoryOrderEditor(JsonElement? saved)
    {
        Margin = new(0, 7, 0, 14);
        Spacing = 7;
        try { if (saved is { ValueKind: JsonValueKind.Array }) Order.AddRange(saved.Value.Deserialize<List<string>>() ?? []); } catch { }
        Order.RemoveAll(id => !Defaults.Any(option => option.Id == id));
        foreach (var option in Defaults) if (!Order.Contains(option.Id)) Order.Add(option.Id);
        Children.Add(new TextBlock { Text = "Drag sections into the order they should appear in the credits.", Foreground = Brushes.LightGray });
        Children.Add(_cards);
        Render();
    }

    private void Render()
    {
        _cards.Children.Clear();
        foreach (var id in Order)
        {
            var option = Defaults.First(item => item.Id == id);
            var card = new Border
            {
                Tag = id, Background = Brush.Parse("#242832"), Padding = new(12, 9), CornerRadius = new(5),
                Child = new TextBlock { Text = "☰  " + option.Label, FontWeight = FontWeight.Medium }
            };
            card.PointerPressed += CardPointerPressed;
            card.PointerMoved += CardPointerMoved;
            card.PointerReleased += CardPointerReleased;
            _cards.Children.Add(card);
        }
    }

    private void CardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { Tag: string id } card || !e.GetCurrentPoint(card).Properties.IsLeftButtonPressed) return;
        _dragging = id;
        e.Pointer.Capture(card);
        e.Handled = true;
    }

    private void CardPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragging is null || sender is not Border card || !e.GetCurrentPoint(card).Properties.IsLeftButtonPressed) return;
        var y = e.GetPosition(_cards).Y;
        var target = _cards.Children.OfType<Border>().Select((item, index) => new { item, index })
            .FirstOrDefault(entry => y < entry.item.Bounds.Bottom)?.index ?? Order.Count - 1;
        var current = Order.IndexOf(_dragging);
        if (current == target || current < 0) return;
        Order.RemoveAt(current);
        Order.Insert(target, _dragging);
        Render();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void CardPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragging = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }
}
