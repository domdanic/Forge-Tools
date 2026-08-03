using Forge.PluginSdk;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.Versioning;

namespace Forge.App;

public sealed class ForgeEventBus : IForgeEventBus
{
    private readonly ConcurrentDictionary<Type, List<Delegate>> _handlers = new();
    public IDisposable Subscribe<T>(Func<T, Task> handler)
    {
        var list = _handlers.GetOrAdd(typeof(T), _ => []);
        lock (list) list.Add(handler);
        return new Subscription(() => { lock (list) list.Remove(handler); });
    }
    public async Task PublishAsync<T>(T message, CancellationToken cancellationToken = default)
    {
        if (!_handlers.TryGetValue(typeof(T), out var list)) return;
        Delegate[] snapshot; lock (list) snapshot = [.. list];
        foreach (var handler in snapshot.Cast<Func<T, Task>>()) { cancellationToken.ThrowIfCancellationRequested(); await handler(message); }
    }
    private sealed class Subscription(Action dispose) : IDisposable { public void Dispose() => dispose(); }
}

public sealed class ForgeLogger
{
    private readonly string _logPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public ForgeLogger(string logsDirectory) => _logPath = Path.Combine(logsDirectory, $"forge-{DateTime.UtcNow:yyyyMMdd}.log");
    public async Task WriteAsync(string level, string source, string message, Exception? exception = null)
    {
        var safe = Sanitize(message + (exception is null ? "" : $" | {exception.GetType().Name}: {exception.Message}"));
        await _gate.WaitAsync();
        try { await File.AppendAllTextAsync(_logPath, $"{DateTimeOffset.Now:O} [{level}] [{source}] {safe}{Environment.NewLine}"); }
        finally { _gate.Release(); }
    }
    private static string Sanitize(string value) => System.Text.RegularExpressions.Regex.Replace(value, "(?i)(access_token|refresh_token|password|authorization)\\s*[:=]\\s*[^\\s,;]+", "$1=[REDACTED]");
}

public sealed class CredentialStore
{
    private readonly string _directory;
    public CredentialStore(string directory) => _directory = directory;
    public bool CanPersist => OperatingSystem.IsWindows();
    public void Save(string key, string secret)
    {
        if (!OperatingSystem.IsWindows()) return;
        SaveWindows(Path.Combine(_directory, SafeName(key)), secret);
    }
    public string? Load(string key)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var path = Path.Combine(_directory, SafeName(key));
        if (!File.Exists(path)) return null;
        try { return LoadWindows(path); }
        catch { File.Delete(path); return null; }
    }
    public void Delete(string key) { var path = Path.Combine(_directory, SafeName(key)); if (File.Exists(path)) File.Delete(path); }
    private static string SafeName(string key) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key))) + ".credential";
    [SupportedOSPlatform("windows")]
    private static void SaveWindows(string path, string secret) => File.WriteAllBytes(path, ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser));
    [SupportedOSPlatform("windows")]
    private static string LoadWindows(string path) => System.Text.Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser));
}

public sealed record ForgeProfile(string Id, string Name, DateTimeOffset CreatedAt);

public sealed class ProfileManager
{
    private readonly string _directory;
    private readonly string _statePath;
    public string ActiveProfileId { get; private set; }
    public event EventHandler<ProfileChanged>? Changed;
    public ProfileManager(string directory)
    {
        _directory = directory; _statePath = Path.Combine(directory, "active.json");
        ActiveProfileId = LoadActive(); EnsureProfile(ActiveProfileId, ActiveProfileId == "default" ? "Default" : ActiveProfileId);
    }
    public IReadOnlyList<ForgeProfile> List() => Directory.EnumerateDirectories(_directory).Select(path =>
    {
        var info = Path.Combine(path, "profile.json");
        try { return JsonSerializer.Deserialize<ForgeProfile>(File.ReadAllText(info)); } catch { return null; }
    }).Where(x => x is not null).Cast<ForgeProfile>().OrderBy(x => x.Name).ToList();
    public ForgeProfile Create(string name)
    {
        var id = Guid.NewGuid().ToString("N"); return EnsureProfile(id, name.Trim());
    }
    public void Activate(string id)
    {
        if (!Directory.Exists(Path.Combine(_directory, id))) throw new DirectoryNotFoundException("Profile not found.");
        var previous = ActiveProfileId; ActiveProfileId = id;
        File.WriteAllText(_statePath, JsonSerializer.Serialize(new { activeProfileId = id }, new JsonSerializerOptions { WriteIndented = true }));
        Changed?.Invoke(this, new(previous, id));
    }
    public string SettingsDirectory { get { var path = Path.Combine(_directory, ActiveProfileId, "settings"); Directory.CreateDirectory(path); return path; } }
    private ForgeProfile EnsureProfile(string id, string name)
    {
        var path = Path.Combine(_directory, id); Directory.CreateDirectory(path);
        var info = Path.Combine(path, "profile.json");
        if (File.Exists(info)) return JsonSerializer.Deserialize<ForgeProfile>(File.ReadAllText(info))!;
        var profile = new ForgeProfile(id, string.IsNullOrWhiteSpace(name) ? "Untitled profile" : name, DateTimeOffset.UtcNow);
        File.WriteAllText(info, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true })); return profile;
    }
    private string LoadActive()
    {
        try { using var doc = JsonDocument.Parse(File.ReadAllText(_statePath)); return doc.RootElement.GetProperty("activeProfileId").GetString() ?? "default"; }
        catch { return "default"; }
    }
}
