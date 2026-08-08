using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using System.Text.Json;

namespace Forge.App;

internal sealed class RecapPreviewControl : StackPanel
{
    private readonly string? _statusPath;
    private readonly Action<string> _status;
    private readonly TextBlock _summary = new() { Foreground = Brushes.LightGray };
    private readonly TextBox _preview = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 180 };

    public RecapPreviewControl(string? statusPath, Action<string> status)
    {
        _statusPath = statusPath;
        _status = status;
        Spacing = 8;
        Margin = new(0, 7, 0, 14);
        var refresh = new Button { Content = "Refresh credits preview", HorizontalAlignment = HorizontalAlignment.Left };
        refresh.Click += (_, _) => Refresh();
        Children.Add(_summary); Children.Add(_preview); Children.Add(refresh);
        Refresh();
    }

    private void Refresh()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_statusPath) || !File.Exists(_statusPath))
            {
                _summary.Text = "No recap has been generated yet."; _preview.Text = "Start the plugin and refresh this preview."; return;
            }
            using var document = JsonDocument.Parse(File.ReadAllText(_statusPath));
            var root = document.RootElement;
            var count = root.TryGetProperty("includedEventCount", out var countElement) ? countElement.GetInt32() : 0;
            var streaming = root.TryGetProperty("streaming", out var streamElement) && streamElement.GetBoolean();
            _summary.Text = $"{count} included credit item{(count == 1 ? "" : "s")} · {(streaming ? "streaming now" : "latest completed/current recap")}";
            _preview.Text = root.TryGetProperty("creditsText", out var text) ? text.GetString() ?? "" : "";
            _status("Credits preview refreshed");
        }
        catch (Exception ex) { _status("Could not read credits preview: " + ex.Message); }
    }
}
