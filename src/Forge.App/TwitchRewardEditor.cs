using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Forge.PluginSdk;

namespace Forge.App;

public sealed class TwitchRewardEditor : StackPanel
{
    private readonly ITwitchConnection _twitch;
    private readonly IForgeEventBus _events;
    private readonly Action<string> _status;
    private readonly StackPanel _list = new() { Spacing = 8 };

    public TwitchRewardEditor(ITwitchConnection twitch, IForgeEventBus events, Action<string> status)
    {
        _twitch = twitch; _events = events; _status = status; Spacing = 10;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var add = new Button { Content = "Create Forge reward" }; var refresh = new Button { Content = "Refresh rewards" };
        add.Click += (_, _) => AddEditor(null); refresh.Click += async (_, _) => await RefreshAsync();
        actions.Children.Add(add); actions.Children.Add(refresh); Children.Add(actions); Children.Add(_list);
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        _list.Children.Clear();
        try
        {
            var rewards = await _twitch.GetCustomRewardsAsync();
            if (rewards.Count == 0) _list.Children.Add(new TextBlock { Text = "No custom rewards found.", Foreground = Brushes.LightGray });
            foreach (var reward in rewards) AddEditor(reward);
            _status($"Loaded {rewards.Count} Twitch reward{(rewards.Count == 1 ? "" : "s")}");
        }
        catch (Exception ex) { _list.Children.Add(new TextBlock { Text = ex.GetBaseException().Message, Foreground = Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap }); }
    }

    private void AddEditor(TwitchCustomReward? reward)
    {
        var body = new StackPanel { Spacing = 6 };
        var title = Field(body, "Title", reward?.Title ?? "");
        var prompt = Field(body, "Prompt", reward?.Prompt ?? "");
        body.Children.Add(new TextBlock { Text = "Cost" }); var cost = new NumericUpDown { Minimum = 1, Maximum = 1_000_000_000, Value = reward?.Cost ?? 100 }; body.Children.Add(cost);
        var enabled = new CheckBox { Content = "Enabled", IsChecked = reward?.IsEnabled ?? true }; body.Children.Add(enabled);
        var input = new CheckBox { Content = "Require viewer text", IsChecked = reward?.IsUserInputRequired ?? false }; body.Children.Add(input);
        var skip = new CheckBox { Content = "Skip the redemption queue", IsChecked = reward?.SkipRequestQueue ?? true }; body.Children.Add(skip);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var manageable = reward?.IsManageable ?? true;
        var save = new Button { Content = reward is null ? "Create reward" : manageable ? "Save changes" : "Created outside Forge", IsEnabled = manageable }; var remove = new Button { Content = "Delete", IsVisible = reward is not null, IsEnabled = manageable };
        save.Click += async (_, _) =>
        {
            try
            {
                var request = new TwitchCustomRewardRequest(title.Text?.Trim() ?? "", prompt.Text?.Trim() ?? "", (int)(cost.Value ?? 100), enabled.IsChecked == true, input.IsChecked == true, skip.IsChecked == true);
                if (request.Title.Length is < 1 or > 45) throw new InvalidOperationException("Reward titles must be between 1 and 45 characters.");
                if (reward is null) await _twitch.CreateCustomRewardAsync(request); else await _twitch.UpdateCustomRewardAsync(reward.Id, request);
                await _events.PublishAsync(new TwitchRewardsChanged(DateTimeOffset.UtcNow)); await RefreshAsync(); _status(reward is null ? "Reward created" : "Reward updated");
            }
            catch (Exception ex) { _status("Reward save failed: " + ex.GetBaseException().Message); }
        };
        remove.Click += async (_, _) => { try { await _twitch.DeleteCustomRewardAsync(reward!.Id); await _events.PublishAsync(new TwitchRewardsChanged(DateTimeOffset.UtcNow)); await RefreshAsync(); _status("Reward deleted"); } catch (Exception ex) { _status("Reward deletion failed: " + ex.GetBaseException().Message); } };
        actions.Children.Add(save); actions.Children.Add(remove); body.Children.Add(actions);
        if (reward is not null) body.Children.Insert(0, new TextBlock { Text = reward.Title, FontWeight = FontWeight.Bold, FontSize = 17 });
        _list.Children.Add(new Border { Background = Brush.Parse("#191C22"), Padding = new(12), CornerRadius = new(5), Child = body });
    }

    private static TextBox Field(Panel panel, string label, string value) { panel.Children.Add(new TextBlock { Text = label }); var field = new TextBox { Text = value }; panel.Children.Add(field); return field; }
}
