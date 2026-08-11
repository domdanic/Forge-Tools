using Forge.PluginSdk;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace Forge.App;

public sealed class PermissionStore
{
    private readonly string _path;
    private Dictionary<string, HashSet<string>> _grants;
    public PermissionStore(string settingsDirectory) { _path = Path.Combine(settingsDirectory, "permissions.json"); _grants = Load(); }
    public IReadOnlySet<string> Get(string pluginId) => _grants.TryGetValue(pluginId, out var grants) ? grants : new HashSet<string>();
    public void Set(string pluginId, IEnumerable<string> permissions) { _grants[pluginId] = [.. permissions]; Save(); }
    public bool Allows(string pluginId, string permission) => Get(pluginId).Contains(permission);
    private Dictionary<string, HashSet<string>> Load() { try { return JsonSerializer.Deserialize<Dictionary<string, HashSet<string>>>(File.ReadAllText(_path)) ?? []; } catch { return []; } }
    private void Save() => File.WriteAllText(_path, JsonSerializer.Serialize(_grants, new JsonSerializerOptions { WriteIndented = true }));
}

public sealed class PluginRuntimeManager : IAsyncDisposable
{
    private readonly IForgeEventBus _events;
    private readonly IForgeConnections _connections;
    private readonly PermissionStore _permissions;
    private readonly ForgeLogger _log;
    private readonly string _settingsDirectory;
    private readonly AutomationService _automation;
    private readonly CredentialStore _credentials;
    private readonly List<LoadedPlugin> _loaded = [];
    public PluginRuntimeManager(IForgeEventBus events, IForgeConnections connections, PermissionStore permissions, ForgeLogger log, string settingsDirectory, AutomationService automation, CredentialStore credentials) { _events = events; _connections = connections; _permissions = permissions; _log = log; _settingsDirectory = settingsDirectory; _automation = automation; _credentials = credentials; }

    public async Task StartAsync(IEnumerable<InstalledPlugin> plugins, CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken);
        foreach (var installed in plugins.Where(x => !string.IsNullOrWhiteSpace(x.Manifest.EntryAssembly)))
        {
            try
            {
                PluginManager.EnsureCoreCompatibility(installed.Manifest.MinimumCoreVersion, installed.Manifest.Name);
                var requested = installed.Manifest.Permissions.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var granted = _permissions.Get(installed.Manifest.Id);
                if (!requested.IsSubsetOf(granted)) { await _log.WriteAsync("WARN", installed.Manifest.Id, "Plugin not started because requested permissions have not been granted."); continue; }
                var assemblyPath = Path.GetFullPath(Path.Combine(installed.Directory, installed.Manifest.EntryAssembly!));
                if (!assemblyPath.StartsWith(Path.GetFullPath(installed.Directory), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Plugin entry assembly is outside its package.");
                var loadContext = new AssemblyLoadContext($"Forge.Plugin.{installed.Manifest.Id}", isCollectible: true);
                loadContext.Resolving += (context, name) =>
                {
                    if (name.Name == typeof(IForgePlugin).Assembly.GetName().Name) return typeof(IForgePlugin).Assembly;
                    var dependency = Path.Combine(installed.Directory, name.Name + ".dll");
                    return File.Exists(dependency) ? context.LoadFromStream(new MemoryStream(File.ReadAllBytes(dependency))) : null;
                };
                await using var assemblyFile = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, true);
                var assembly = loadContext.LoadFromStream(assemblyFile);
                var type = !string.IsNullOrWhiteSpace(installed.Manifest.EntryType) ? assembly.GetType(installed.Manifest.EntryType!, true) : assembly.GetTypes().FirstOrDefault(x => typeof(IForgePlugin).IsAssignableFrom(x) && !x.IsAbstract);
                if (type is null || Activator.CreateInstance(type) is not IForgePlugin plugin) throw new InvalidDataException("No IForgePlugin entry type was found.");
                var data = Path.Combine(_settingsDirectory, "plugin-data", installed.Manifest.Id); Directory.CreateDirectory(data);
                var settings = new JsonPluginSettings(Path.Combine(_settingsDirectory, installed.Manifest.Id + ".json"));
                await plugin.InitializeAsync(new ForgeContext(installed.Manifest.Id, data, granted, new PermissionedEventBus(installed.Manifest.Id, granted, _events), new PermissionedConnections(installed.Manifest.Id, granted, _connections), settings, new PluginAutomation(installed.Manifest.Id, _automation), new PluginSecrets(installed.Manifest.Id, _credentials)), cancellationToken);
                await plugin.StartAsync(cancellationToken); _loaded.Add(new(installed.Manifest.Id, plugin, loadContext));
                await _log.WriteAsync("INFO", installed.Manifest.Id, "Plugin started.");
            }
            catch (Exception ex) { await _log.WriteAsync("ERROR", installed.Manifest.Id, "Plugin startup failed and was isolated.", ex); }
        }
    }
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        foreach (var loaded in _loaded.AsEnumerable().Reverse())
        {
            try { await loaded.Plugin.StopAsync(cancellationToken); await loaded.Plugin.DisposeAsync(); } catch (Exception ex) { await _log.WriteAsync("ERROR", loaded.Id, "Plugin shutdown failed.", ex); }
            loaded.Context.Unload();
        }
        _loaded.Clear();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
    public async ValueTask DisposeAsync() => await StopAsync();
    private sealed record LoadedPlugin(string Id, IForgePlugin Plugin, AssemblyLoadContext Context);
    private sealed record ForgeContext(string PluginId, string DataDirectory, IReadOnlySet<string> GrantedPermissions, IForgeEventBus Events, IForgeConnections Connections, IPluginSettings Settings, IForgeAutomation Automation, IPluginSecrets Secrets) : IForgeContext;
}

internal sealed class PluginAutomation(string pluginId, AutomationService inner) : IForgeAutomation
{
    public IDisposable RegisterTrigger(AutomationTriggerDefinition definition) { RequireOwned(definition.Id); return inner.RegisterTrigger(definition); }
    public IDisposable RegisterAction(AutomationActionDefinition definition, Func<AutomationActionInvocation, CancellationToken, Task> handler) { RequireOwned(definition.Id); return inner.RegisterAction(definition, handler); }
    public Task FireAsync(string triggerId, IReadOnlyDictionary<string, string>? variables = null, CancellationToken cancellationToken = default) { RequireOwned(triggerId); return inner.FireAsync(triggerId, variables, cancellationToken); }
    private void RequireOwned(string id) { if (!id.StartsWith(pluginId + ".", StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException($"{pluginId} cannot register or fire automation ID {id}."); }
}

internal sealed class PluginSecrets(string pluginId, CredentialStore inner) : IPluginSecrets
{
    public bool CanPersist => inner.CanPersist;
    public string? Load(string key) => inner.Load(pluginId + "." + key);
    public void Save(string key, string value) => inner.Save(pluginId + "." + key, value);
    public void Delete(string key) => inner.Delete(pluginId + "." + key);
}

internal sealed class PermissionedEventBus(string pluginId, IReadOnlySet<string> grants, IForgeEventBus inner) : IForgeEventBus
{
    public IDisposable Subscribe<T>(Func<T, Task> handler)
    {
        if (typeof(T) == typeof(TwitchChatMessage) && !grants.Contains("twitch.chat.read"))
            throw new UnauthorizedAccessException($"{pluginId} requires twitch.chat.read to receive chat messages.");
        if (typeof(T) == typeof(TwitchAdBreakStarted) && !grants.Contains("twitch.ads.read"))
            throw new UnauthorizedAccessException($"{pluginId} requires twitch.ads.read to receive ad events.");
        if (IsRecapEvent(typeof(T)) && !grants.Contains("twitch.events.read"))
            throw new UnauthorizedAccessException($"{pluginId} requires twitch.events.read to receive Twitch engagement events.");
        if (typeof(T) == typeof(TwitchRewardRedeemed) && !grants.Contains("twitch.redemptions.read") && !grants.Contains("twitch.redemptions.manage"))
            throw new UnauthorizedAccessException($"{pluginId} requires Twitch redemption permission.");
        return inner.Subscribe(handler);
    }

    private static bool IsRecapEvent(Type type) => type == typeof(TwitchFollowed) || type == typeof(TwitchSubscribed) ||
        type == typeof(TwitchSubscriptionMessage) || type == typeof(TwitchSubscriptionGifted) ||
        type == typeof(TwitchCheered) || type == typeof(TwitchRaided);

    public Task PublishAsync<T>(T message, CancellationToken cancellationToken = default) => inner.PublishAsync(message, cancellationToken);
}

public sealed class ForgeConnections(IObsConnection obs, ITwitchConnection twitch) : IForgeConnections { public IObsConnection Obs { get; } = obs; public ITwitchConnection Twitch { get; } = twitch; }
public sealed class TwitchConnectionView(TwitchAuthService auth, TwitchOutboundChatService? outboundChat = null) : ITwitchConnection
{
    public bool IsConnected => auth.Identity is not null; public string? Login => auth.Identity?.Login;
    public Task<TwitchCategory?> FindCategoryAsync(string exactName, CancellationToken cancellationToken = default) => auth.FindCategoryAsync(exactName, cancellationToken);
    public Task<TwitchChannel?> GetChannelAsync(CancellationToken cancellationToken = default) => auth.GetChannelAsync(cancellationToken);
    public Task UpdateCategoryAsync(string categoryId, CancellationToken cancellationToken = default) => auth.UpdateCategoryAsync(categoryId, cancellationToken);
    public Task SendChatMessageAsync(string message, CancellationToken cancellationToken = default) => outboundChat?.SendChatMessageAsync(message, cancellationToken) ?? auth.SendChatMessageAsync(message, cancellationToken);
    public Task DeleteChatMessageAsync(string messageId, CancellationToken cancellationToken = default) => auth.DeleteChatMessageAsync(messageId, cancellationToken);
    public Task<TwitchAdSchedule?> GetAdScheduleAsync(CancellationToken cancellationToken = default) => auth.GetAdScheduleAsync(cancellationToken);
    public Task<IReadOnlyList<TwitchChatter>> GetChattersAsync(CancellationToken cancellationToken = default) => auth.GetChattersAsync(cancellationToken);
    public Task<IReadOnlyList<TwitchCustomReward>> GetCustomRewardsAsync(CancellationToken cancellationToken = default) => auth.GetCustomRewardsAsync(cancellationToken);
    public Task<TwitchCustomReward> CreateCustomRewardAsync(TwitchCustomRewardRequest reward, CancellationToken cancellationToken = default) => auth.CreateCustomRewardAsync(reward, cancellationToken);
    public Task UpdateCustomRewardAsync(string rewardId, TwitchCustomRewardRequest reward, CancellationToken cancellationToken = default) => auth.UpdateCustomRewardAsync(rewardId, reward, cancellationToken);
    public Task DeleteCustomRewardAsync(string rewardId, CancellationToken cancellationToken = default) => auth.DeleteCustomRewardAsync(rewardId, cancellationToken);
    public Task UpdateRedemptionStatusAsync(string rewardId, string redemptionId, string status, CancellationToken cancellationToken = default) => auth.UpdateRedemptionStatusAsync(rewardId, redemptionId, status, cancellationToken);
}

public sealed class JsonPluginSettings(string path) : IPluginSettings
{
    public event EventHandler? Changed;
    public T Get<T>(string key, T fallback)
    {
        try { var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path)); return values is not null && values.TryGetValue(key, out var value) ? value.Deserialize<T>() ?? fallback : fallback; }
        catch { return fallback; }
    }
    public void Set<T>(string key, T value)
    {
        Dictionary<string, JsonElement> values; try { values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path)) ?? []; } catch { values = []; }
        values[key] = JsonSerializer.SerializeToElement(value); File.WriteAllText(path, JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true })); Changed?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class PermissionedConnections(string pluginId, IReadOnlySet<string> grants, IForgeConnections inner) : IForgeConnections
{
    public IObsConnection Obs { get; } = new GuardedObs(pluginId, grants, inner.Obs);
    public ITwitchConnection Twitch => grants.Any(x => x.StartsWith("twitch.", StringComparison.OrdinalIgnoreCase)) ? new GuardedTwitch(pluginId, grants, inner.Twitch) : throw new UnauthorizedAccessException($"{pluginId} does not have Twitch permission.");
    private sealed class GuardedObs(string pluginId, IReadOnlySet<string> grants, IObsConnection inner) : IObsConnection
    {
        public bool IsConnected => grants.Any(x => x.StartsWith("obs.", StringComparison.OrdinalIgnoreCase)) && inner.IsConnected;
        public Task<JsonElement> RequestAsync(string requestType, object? requestData = null, CancellationToken cancellationToken = default)
        {
            if (!grants.Contains("obs.control") && !grants.Contains("obs.read")) throw new UnauthorizedAccessException($"{pluginId} does not have OBS permission.");
            if (!grants.Contains("obs.control") && !requestType.StartsWith("Get", StringComparison.Ordinal))
                throw new UnauthorizedAccessException($"{pluginId} requires obs.control for the {requestType} request.");
            return inner.RequestAsync(requestType, requestData, cancellationToken);
        }
    }
    private sealed class GuardedTwitch(string pluginId, IReadOnlySet<string> grants, ITwitchConnection inner) : ITwitchConnection
    {
        public bool IsConnected => inner.IsConnected; public string? Login => inner.Login;
        public Task<TwitchCategory?> FindCategoryAsync(string exactName, CancellationToken cancellationToken = default) { Require("twitch.channel.read"); return inner.FindCategoryAsync(exactName, cancellationToken); }
        public Task<TwitchChannel?> GetChannelAsync(CancellationToken cancellationToken = default) { Require("twitch.channel.read"); return inner.GetChannelAsync(cancellationToken); }
        public Task UpdateCategoryAsync(string categoryId, CancellationToken cancellationToken = default) { Require("twitch.channel.manage.broadcast"); return inner.UpdateCategoryAsync(categoryId, cancellationToken); }
        public Task SendChatMessageAsync(string message, CancellationToken cancellationToken = default) { Require("twitch.chat.write"); return inner.SendChatMessageAsync(message, cancellationToken); }
        public Task DeleteChatMessageAsync(string messageId, CancellationToken cancellationToken = default) { Require("twitch.chat.moderate"); return inner.DeleteChatMessageAsync(messageId, cancellationToken); }
        public Task<TwitchAdSchedule?> GetAdScheduleAsync(CancellationToken cancellationToken = default) { Require("twitch.ads.read"); return inner.GetAdScheduleAsync(cancellationToken); }
        public Task<IReadOnlyList<TwitchChatter>> GetChattersAsync(CancellationToken cancellationToken = default) { Require("twitch.chatters.read"); return inner.GetChattersAsync(cancellationToken); }
        public Task<IReadOnlyList<TwitchCustomReward>> GetCustomRewardsAsync(CancellationToken cancellationToken = default) { RequireRedemptions(); return inner.GetCustomRewardsAsync(cancellationToken); }
        public Task<TwitchCustomReward> CreateCustomRewardAsync(TwitchCustomRewardRequest reward, CancellationToken cancellationToken = default) { Require("twitch.redemptions.manage"); return inner.CreateCustomRewardAsync(reward, cancellationToken); }
        public Task UpdateCustomRewardAsync(string rewardId, TwitchCustomRewardRequest reward, CancellationToken cancellationToken = default) { Require("twitch.redemptions.manage"); return inner.UpdateCustomRewardAsync(rewardId, reward, cancellationToken); }
        public Task DeleteCustomRewardAsync(string rewardId, CancellationToken cancellationToken = default) { Require("twitch.redemptions.manage"); return inner.DeleteCustomRewardAsync(rewardId, cancellationToken); }
        public Task UpdateRedemptionStatusAsync(string rewardId, string redemptionId, string status, CancellationToken cancellationToken = default) { Require("twitch.redemptions.manage"); return inner.UpdateRedemptionStatusAsync(rewardId, redemptionId, status, cancellationToken); }
        private void RequireRedemptions() { if (!grants.Contains("twitch.redemptions.read") && !grants.Contains("twitch.redemptions.manage")) throw new UnauthorizedAccessException($"{pluginId} does not have Twitch redemption permission."); }
        private void Require(string permission) { if (!grants.Contains(permission)) throw new UnauthorizedAccessException($"{pluginId} does not have {permission} permission."); }
    }
}
