using System.Text.Json.Serialization;
using System.Text.Json;

namespace Forge.PluginSdk;

public sealed record PluginManifest
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string Description { get; init; } = "";
    public string Author { get; init; } = "";
    public string ForgeApi { get; init; } = "1";
    public string? MinimumCoreVersion { get; init; }
    public string? Ui { get; init; }
    public string? Homepage { get; init; }
    public string? UpdateManifestUrl { get; init; }
    public string? EntryAssembly { get; init; }
    public string? EntryType { get; init; }
    public string[] Permissions { get; init; } = [];
}

public interface IForgePlugin : IAsyncDisposable
{
    Task InitializeAsync(IForgeContext context, CancellationToken cancellationToken);
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

public interface IForgeContext
{
    string PluginId { get; }
    string DataDirectory { get; }
    IReadOnlySet<string> GrantedPermissions { get; }
    IForgeEventBus Events { get; }
    IForgeConnections Connections { get; }
    IPluginSettings Settings { get; }
    IForgeAutomation Automation { get; }
    IPluginSecrets Secrets { get; }
}

public interface IPluginSecrets
{
    bool CanPersist { get; }
    string? Load(string key);
    void Save(string key, string value);
    void Delete(string key);
}

public interface IPluginSettings
{
    T Get<T>(string key, T fallback);
    void Set<T>(string key, T value);
    event EventHandler? Changed;
}

public interface IForgeConnections
{
    IObsConnection Obs { get; }
    ITwitchConnection Twitch { get; }
}

public interface IObsConnection
{
    bool IsConnected { get; }
    Task<JsonElement> RequestAsync(string requestType, object? requestData = null, CancellationToken cancellationToken = default);
}

public interface ITwitchConnection
{
    bool IsConnected { get; }
    string? Login { get; }
    Task<TwitchCategory?> FindCategoryAsync(string exactName, CancellationToken cancellationToken = default);
    Task<TwitchChannel?> GetChannelAsync(CancellationToken cancellationToken = default);
    Task UpdateCategoryAsync(string categoryId, CancellationToken cancellationToken = default);
    Task SendChatMessageAsync(string message, CancellationToken cancellationToken = default);
    Task DeleteChatMessageAsync(string messageId, CancellationToken cancellationToken = default);
    Task<TwitchAdSchedule?> GetAdScheduleAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TwitchChatter>> GetChattersAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TwitchCustomReward>> GetCustomRewardsAsync(CancellationToken cancellationToken = default);
    Task<TwitchCustomReward> CreateCustomRewardAsync(TwitchCustomRewardRequest reward, CancellationToken cancellationToken = default);
    Task UpdateCustomRewardAsync(string rewardId, TwitchCustomRewardRequest reward, CancellationToken cancellationToken = default);
    Task DeleteCustomRewardAsync(string rewardId, CancellationToken cancellationToken = default);
    Task UpdateRedemptionStatusAsync(string rewardId, string redemptionId, string status, CancellationToken cancellationToken = default);
}

public sealed record TwitchCategory(string Id, string Name);
public sealed record TwitchChannel(string BroadcasterId, string CategoryId, string CategoryName, string Title);
public sealed record TwitchAdSchedule(DateTimeOffset? NextAdAt, int DurationSeconds, int SnoozeCount);
public sealed record TwitchChatter(string UserId, string UserLogin, string UserName);
public sealed record TwitchCustomReward(string Id, string Title, string Prompt, int Cost, bool IsEnabled, bool IsPaused, bool IsUserInputRequired, bool SkipRequestQueue, bool IsManageable);
public sealed record TwitchCustomRewardRequest(string Title, string Prompt, int Cost, bool IsEnabled = true, bool IsUserInputRequired = false, bool SkipRequestQueue = true);

public interface IForgeEventBus
{
    IDisposable Subscribe<T>(Func<T, Task> handler);
    Task PublishAsync<T>(T message, CancellationToken cancellationToken = default);
}

public interface IForgeAutomation
{
    IDisposable RegisterTrigger(AutomationTriggerDefinition definition);
    IDisposable RegisterAction(AutomationActionDefinition definition, Func<AutomationActionInvocation, CancellationToken, Task> handler);
    Task FireAsync(string triggerId, IReadOnlyDictionary<string, string>? variables = null, CancellationToken cancellationToken = default);
}

public sealed record AutomationTriggerDefinition(string Id, string Name, string Description = "");
public sealed record AutomationActionDefinition(string Id, string Name, string Description, IReadOnlyList<AutomationParameter> Parameters);
public sealed record AutomationParameter(string Key, string Label, string Type = "text", string? Description = null, string? Default = null, bool Required = false, IReadOnlyList<UiOption>? Options = null);
public sealed record AutomationActionInvocation(JsonElement Configuration, IReadOnlyDictionary<string, string> Variables);
public sealed record AutomationActionStep(string ActionId, JsonElement Configuration, int DelayMilliseconds = 0);
public sealed record AutomationBinding(string Id, string Name, string TriggerId, bool Enabled, IReadOnlyList<AutomationActionStep> Actions);

public sealed record ForgeStarted(DateTimeOffset At);
public sealed record ForgeStopping(DateTimeOffset At);
public sealed record ProfileChanged(string Previous, string Current);
public sealed record ObsConnectionChanged(bool Connected, string? Message = null);
public sealed record ObsEvent(string EventType, JsonElement Data);
public sealed record TwitchConnectionChanged(bool Connected, string? Login = null);
public sealed record TwitchCategoryChanged(
    string CategoryId,
    string CategoryName,
    string Source,
    DateTimeOffset At);
public sealed record TwitchChatMessage(
    string MessageId,
    string UserId,
    string UserLogin,
    string UserName,
    string Text,
    bool IsBroadcaster,
    bool IsModerator,
    DateTimeOffset At,
    string? SourceBroadcasterUserId = null,
    string? SourceMessageId = null,
    bool IsSourceOnly = false);
public sealed record TwitchAdBreakStarted(int DurationSeconds, DateTimeOffset StartedAt, bool IsAutomatic);
public sealed record TwitchFollowed(string UserId, string UserLogin, string UserName, DateTimeOffset At);
public sealed record TwitchSubscribed(string UserId, string UserLogin, string UserName, string Tier, bool IsGift, DateTimeOffset At);
public sealed record TwitchSubscriptionMessage(string UserId, string UserLogin, string UserName, string Tier, int CumulativeMonths, int? StreakMonths, int DurationMonths, string Message, DateTimeOffset At);
public sealed record TwitchSubscriptionGifted(string UserId, string UserLogin, string UserName, string Tier, int Total, int? CumulativeTotal, bool IsAnonymous, DateTimeOffset At);
public sealed record TwitchCheered(string UserId, string UserLogin, string UserName, int Bits, string Message, bool IsAnonymous, DateTimeOffset At);
public sealed record TwitchRaided(string UserId, string UserLogin, string UserName, int Viewers, DateTimeOffset At);
public sealed record TwitchRewardRedeemed(string RedemptionId, string RewardId, string RewardTitle, string UserId, string UserLogin, string UserName, string UserInput, string Status, DateTimeOffset At);
public sealed record TwitchRewardsChanged(DateTimeOffset At);
public sealed record TimedAnnouncementTestRequested(string Kind, string? RuleId = null);

public sealed record PluginUi
{
    public List<UiSection> Sections { get; init; } = [];
}

public sealed record UiSection
{
    public required string Title { get; init; }
    public bool Collapsible { get; init; }
    public bool InitiallyExpanded { get; init; } = true;
    public string? Description { get; init; }
    public List<UiControl> Controls { get; init; } = [];
}

public sealed record UiControl
{
    public required string Type { get; init; }
    public required string Key { get; init; }
    public required string Label { get; init; }
    public string? Description { get; init; }
    public object? Default { get; init; }
    public string? OptionsSource { get; init; }
    public List<UiOption> Options { get; init; } = [];
}

public sealed record UiOption
{
    public required string Label { get; init; }
    public required string Value { get; init; }
}

public sealed record CatalogDocument
{
    public string? SourceUrl { get; init; }
    public List<CatalogPlugin> Plugins { get; init; } = [];
}

public sealed record CatalogPlugin
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string Description { get; init; } = "";
    public string Author { get; init; } = "";
    public required string PackageUrl { get; init; }
    public required string Sha256 { get; init; }
    public string ForgeApi { get; init; } = "1";
    public string? MinimumCoreVersion { get; init; }
    public bool Verified { get; init; }
    public string[] Permissions { get; init; } = [];
    public bool Available { get; init; } = true;
    public string? SignerKeyId { get; init; }
    public string? Signature { get; init; }
}

[JsonSerializable(typeof(PluginManifest))]
[JsonSerializable(typeof(PluginUi))]
[JsonSerializable(typeof(CatalogDocument))]
public partial class ForgeJsonContext : JsonSerializerContext;
