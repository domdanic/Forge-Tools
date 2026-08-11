using Forge.PluginSdk;

namespace Forge.TwitchRedeems;

public sealed class TwitchRedeemsPlugin : IForgePlugin
{
    private IForgeContext? _context;
    private readonly List<IDisposable> _registrations = [];
    private IDisposable? _redemptions;
    private IDisposable? _changes;
    private IDisposable? _fulfillAction;
    private IDisposable? _cancelAction;

    public Task InitializeAsync(IForgeContext context, CancellationToken cancellationToken) { _context = context; return Task.CompletedTask; }
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _redemptions = _context!.Events.Subscribe<TwitchRewardRedeemed>(OnRedeemedAsync);
        _changes = _context.Events.Subscribe<TwitchRewardsChanged>(_ => RefreshAsync(CancellationToken.None));
        _fulfillAction = _context.Automation.RegisterAction(new("tools.forge.twitch-redeems.fulfill", "Fulfill Twitch redemption", "Marks a Forge-created queued redemption fulfilled.", []), (invocation, token) => UpdateStatusAsync(invocation, "FULFILLED", token));
        _cancelAction = _context.Automation.RegisterAction(new("tools.forge.twitch-redeems.cancel", "Cancel and refund Twitch redemption", "Cancels a Forge-created queued redemption and returns its points.", []), (invocation, token) => UpdateStatusAsync(invocation, "CANCELED", token));
        await RefreshAsync(cancellationToken);
    }
    public Task StopAsync(CancellationToken cancellationToken) { _redemptions?.Dispose(); _changes?.Dispose(); _fulfillAction?.Dispose(); _cancelAction?.Dispose(); _redemptions = null; _changes = null; _fulfillAction = null; _cancelAction = null; ClearRegistrations(); return Task.CompletedTask; }
    public ValueTask DisposeAsync() { ClearRegistrations(); return ValueTask.CompletedTask; }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        ClearRegistrations();
        if (_context?.Connections.Twitch.IsConnected != true) return;
        var rewards = await _context.Connections.Twitch.GetCustomRewardsAsync(cancellationToken);
        foreach (var reward in rewards) _registrations.Add(_context.Automation.RegisterTrigger(new(TriggerId(reward.Id), $"Twitch reward: {reward.Title}", reward.Prompt)));
    }

    private async Task OnRedeemedAsync(TwitchRewardRedeemed redemption)
    {
        if (_context is null) return;
        if (_registrations.Count == 0) await RefreshAsync(CancellationToken.None);
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["user"] = redemption.UserName, ["user.login"] = redemption.UserLogin, ["user.id"] = redemption.UserId,
            ["input"] = redemption.UserInput, ["reward"] = redemption.RewardTitle, ["reward.id"] = redemption.RewardId,
            ["redemption.id"] = redemption.RedemptionId
        };
        await _context.Automation.FireAsync(TriggerId(redemption.RewardId), variables);
    }

    private Task UpdateStatusAsync(AutomationActionInvocation invocation, string status, CancellationToken cancellationToken)
    {
        if (_context is null || !invocation.Variables.TryGetValue("reward.id", out var rewardId) || !invocation.Variables.TryGetValue("redemption.id", out var redemptionId))
            throw new InvalidOperationException("This action must run from a Twitch redemption trigger.");
        return _context.Connections.Twitch.UpdateRedemptionStatusAsync(rewardId, redemptionId, status, cancellationToken);
    }

    private void ClearRegistrations() { foreach (var item in _registrations) item.Dispose(); _registrations.Clear(); }
    private static string TriggerId(string rewardId) => $"tools.forge.twitch-redeems.reward.{rewardId}";
}
