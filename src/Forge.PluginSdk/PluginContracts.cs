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
    Task<TwitchAdSchedule?> GetAdScheduleAsync(CancellationToken cancellationToken = default);
}

public sealed record TwitchCategory(string Id, string Name);
public sealed record TwitchChannel(string BroadcasterId, string CategoryId, string CategoryName, string Title);
public sealed record TwitchAdSchedule(DateTimeOffset? NextAdAt, int DurationSeconds, int SnoozeCount);

public interface IForgeEventBus
{
    IDisposable Subscribe<T>(Func<T, Task> handler);
    Task PublishAsync<T>(T message, CancellationToken cancellationToken = default);
}

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
    DateTimeOffset At);
public sealed record TwitchAdBreakStarted(int DurationSeconds, DateTimeOffset StartedAt, bool IsAutomatic);

public sealed record PluginUi
{
    public List<UiSection> Sections { get; init; } = [];
}

public sealed record UiSection
{
    public required string Title { get; init; }
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
