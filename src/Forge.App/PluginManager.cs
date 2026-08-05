using Forge.PluginSdk;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace Forge.App;

public sealed record InstalledPlugin(PluginManifest Manifest, PluginUi Ui, string Directory);
public sealed record PluginSettingsPackage(int FormatVersion, string PluginId, string PluginVersion, DateTimeOffset ExportedAt, Dictionary<string, JsonElement> Settings);

public sealed class PluginManager
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http = new();

    public string PluginsDirectory { get; }
    public string SettingsDirectory => Profiles.SettingsDirectory;
    public string DataDirectory { get; }
    public string ProfilesDirectory { get; }
    public string CacheDirectory { get; }
    public string LogsDirectory { get; }
    public string CredentialsDirectory { get; }
    public ProfileManager Profiles { get; }

    public PluginManager()
    {
        DataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
        PluginsDirectory = Path.Combine(DataDirectory, "plugins");
        ProfilesDirectory = Path.Combine(DataDirectory, "profiles");
        CacheDirectory = Path.Combine(DataDirectory, "cache");
        LogsDirectory = Path.Combine(DataDirectory, "logs");
        CredentialsDirectory = Path.Combine(DataDirectory, "credentials");
        EnsurePortableStorage();
        Profiles = new ProfileManager(ProfilesDirectory);
        InstallBundledPlugins();
    }

    private void EnsurePortableStorage()
    {
        var rootMarker = Path.Combine(AppContext.BaseDirectory, ".forge-root");
        if (!File.Exists(rootMarker))
            throw new InvalidOperationException("Forge's portable root marker is missing. Keep .forge-root beside Forge.App.exe.");

        foreach (var directory in new[] { DataDirectory, PluginsDirectory, ProfilesDirectory, CacheDirectory, LogsDirectory, CredentialsDirectory })
            Directory.CreateDirectory(directory);

        var probe = Path.Combine(DataDirectory, ".write-test");
        try { File.WriteAllText(probe, "ok"); File.Delete(probe); }
        catch (Exception ex) { throw new InvalidOperationException("Forge must run from a writable folder. Move the Forge Tools folder somewhere you own.", ex); }
    }

    private void InstallBundledPlugins()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "BundledPlugins");
        if (!Directory.Exists(bundled)) return;
        foreach (var source in Directory.EnumerateDirectories(bundled))
        {
            var target = Path.Combine(PluginsDirectory, Path.GetFileName(source));
            if (Directory.Exists(target)) continue;
            CopyDirectory(source, target);
        }
    }

    private static void CopyDirectory(string source, string target, bool overwrite = false)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite);
        foreach (var child in Directory.EnumerateDirectories(source)) CopyDirectory(child, Path.Combine(target, Path.GetFileName(child)), overwrite);
    }

    public IReadOnlyList<InstalledPlugin> Discover()
    {
        var plugins = new List<InstalledPlugin>();
        foreach (var manifestPath in System.IO.Directory.EnumerateFiles(PluginsDirectory, "plugin.json", SearchOption.AllDirectories))
        {
            try
            {
                var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath), JsonOptions);
                if (manifest is null || !ValidateManifest(manifest, out _) || string.IsNullOrWhiteSpace(manifest.Ui)) continue;
                var directory = Path.GetDirectoryName(manifestPath)!;
                var uiPath = Path.GetFullPath(Path.Combine(directory, manifest.Ui));
                if (!uiPath.StartsWith(Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase)) continue;
                var ui = JsonSerializer.Deserialize<PluginUi>(File.ReadAllText(uiPath), JsonOptions);
                if (ui is not null) plugins.Add(new(manifest, ui, directory));
            }
            catch { /* A broken plugin must not prevent the host from launching. */ }
        }
        return plugins.OrderBy(p => p.Manifest.Name).ToList();
    }

    public async Task<CatalogDocument> LoadCatalogAsync(string source)
    {
        string json;
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            var separator = uri.Query.Length == 0 ? "?" : "&";
            using var request = new HttpRequestMessage(HttpMethod.Get, uri + separator + "forgeCache=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };
            using var response = await _http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            json = await response.Content.ReadAsStringAsync();
        }
        else json = await File.ReadAllTextAsync(source);
        var catalog = JsonSerializer.Deserialize<CatalogDocument>(json, JsonOptions) ?? new();
        if (!string.IsNullOrWhiteSpace(catalog.SourceUrl) && !string.Equals(source, catalog.SourceUrl, StringComparison.OrdinalIgnoreCase))
            return await LoadCatalogAsync(catalog.SourceUrl);
        return catalog;
    }

    public async Task InstallAsync(CatalogPlugin plugin)
    {
        if (!plugin.Available) throw new InvalidOperationException("This plugin has not been released yet.");
        EnsureCoreCompatibility(plugin.MinimumCoreVersion, plugin.Name);
        var packageBytes = await _http.GetByteArrayAsync(plugin.PackageUrl);
        var actualHash = Convert.ToHexString(SHA256.HashData(packageBytes));
        if (!actualHash.Equals(plugin.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloaded package checksum did not match the catalog.");
        VerifyPublisherSignature(plugin, packageBytes);

        var staging = Path.Combine(Path.GetTempPath(), "ForgeTools", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using var archive = new ZipArchive(new MemoryStream(packageBytes), ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                var destination = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                if (!destination.StartsWith(Path.GetFullPath(staging), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The plugin package contains an unsafe path.");
                if (string.IsNullOrEmpty(entry.Name)) Directory.CreateDirectory(destination);
                else { Directory.CreateDirectory(Path.GetDirectoryName(destination)!); entry.ExtractToFile(destination, true); }
            }

            var manifestPath = Path.Combine(staging, "plugin.json");
            var manifest = JsonSerializer.Deserialize<PluginManifest>(await File.ReadAllTextAsync(manifestPath), JsonOptions)
                ?? throw new InvalidDataException("Plugin manifest is missing.");
            if (!ValidateManifest(manifest, out var validationError)) throw new InvalidDataException(validationError);
            if (manifest.Id != plugin.Id) throw new InvalidDataException("Package identity does not match the catalog.");
            EnsureCoreCompatibility(manifest.MinimumCoreVersion, manifest.Name);

            var target = Path.Combine(PluginsDirectory, plugin.Id);
            var backup = Path.Combine(CacheDirectory, "plugin-backups", plugin.Id + "-" + Guid.NewGuid().ToString("N"));
            if (Directory.Exists(target)) CopyDirectory(target, backup, true);
            try { CopyDirectory(staging, target, true); }
            catch { if (Directory.Exists(backup)) CopyDirectory(backup, target, true); throw; }
            try { if (Directory.Exists(backup)) Directory.Delete(backup, true); } catch { /* OneDrive may retain the safety copy until its lock clears. */ }
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    public Dictionary<string, JsonElement> LoadSettings(string pluginId)
    {
        var path = Path.Combine(SettingsDirectory, pluginId + ".json");
        if (!File.Exists(path)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(path), JsonOptions) ?? []; }
        catch { return []; }
    }

    public void SaveSettings(string pluginId, Dictionary<string, object?> settings) =>
        File.WriteAllText(Path.Combine(SettingsDirectory, pluginId + ".json"), JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));

    public void SaveSettings(string pluginId, Dictionary<string, JsonElement> settings) =>
        File.WriteAllText(Path.Combine(SettingsDirectory, pluginId + ".json"), JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));

    public PluginSettingsPackage CreateSettingsExport(InstalledPlugin plugin) =>
        new(1, plugin.Manifest.Id, plugin.Manifest.Version, DateTimeOffset.UtcNow, LoadSettings(plugin.Manifest.Id));

    public static PluginSettingsPackage ReadSettingsExport(string json)
    {
        var package = JsonSerializer.Deserialize<PluginSettingsPackage>(json, JsonOptions)
            ?? throw new InvalidDataException("The settings export is empty or invalid.");
        if (package.FormatVersion != 1) throw new InvalidDataException($"Unsupported settings export format {package.FormatVersion}.");
        if (string.IsNullOrWhiteSpace(package.PluginId)) throw new InvalidDataException("The settings export has no plugin identity.");
        if (package.Settings is null) throw new InvalidDataException("The settings export contains no settings object.");
        return package;
    }

    public void Remove(string pluginId)
    {
        var target = Path.GetFullPath(Path.Combine(PluginsDirectory, pluginId));
        if (!target.StartsWith(Path.GetFullPath(PluginsDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid plugin location.");
        if (Directory.Exists(target)) Directory.Delete(target, true);
    }

    public bool IsEnabled(string pluginId) => !LoadDisabled().Contains(pluginId);
    public void SetEnabled(string pluginId, bool enabled)
    {
        var disabled = LoadDisabled(); if (enabled) disabled.Remove(pluginId); else disabled.Add(pluginId);
        File.WriteAllText(Path.Combine(SettingsDirectory, "disabled-plugins.json"), JsonSerializer.Serialize(disabled.OrderBy(x => x), new JsonSerializerOptions { WriteIndented = true }));
    }
    private HashSet<string> LoadDisabled()
    {
        try { return JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(Path.Combine(SettingsDirectory, "disabled-plugins.json"))) ?? []; }
        catch { return []; }
    }

    public static bool ValidateManifest(PluginManifest manifest, out string error)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(manifest.Id ?? "", "^[a-z0-9]+(?:[.-][a-z0-9]+)+$")) { error = "Plugin ID must be a reverse-domain-style lowercase identifier."; return false; }
        if (string.IsNullOrWhiteSpace(manifest.Name)) { error = "Plugin name is required."; return false; }
        if (!Version.TryParse(manifest.Version, out _)) { error = "Plugin version must be numeric semantic version text."; return false; }
        if (!string.IsNullOrWhiteSpace(manifest.MinimumCoreVersion) && !Version.TryParse(manifest.MinimumCoreVersion, out _)) { error = "Minimum Core version must be numeric semantic version text."; return false; }
        if (manifest.ForgeApi is not ("1" or "2")) { error = $"Plugin requires unsupported Forge API {manifest.ForgeApi}."; return false; }
        var prefixes = new[] { "storage.", "obs.", "twitch.", "network.", "filesystem.", "system." };
        if (manifest.Permissions.Any(permission => !prefixes.Any(prefix => permission.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))) { error = "Plugin declares an unknown permission."; return false; }
        error = ""; return true;
    }

    public static bool IsCoreCompatible(string? minimumCoreVersion) =>
        string.IsNullOrWhiteSpace(minimumCoreVersion) || Version.TryParse(minimumCoreVersion, out var minimum) && CoreUpdateService.CurrentVersion >= minimum;

    public static void EnsureCoreCompatibility(string? minimumCoreVersion, string pluginName)
    {
        if (!IsCoreCompatible(minimumCoreVersion))
            throw new InvalidOperationException($"{pluginName} requires Forge Tools {minimumCoreVersion} or newer. Update Core first.");
    }

    private static void VerifyPublisherSignature(CatalogPlugin plugin, byte[] packageBytes)
    {
        if (string.IsNullOrWhiteSpace(plugin.Signature) || string.IsNullOrWhiteSpace(plugin.SignerKeyId))
        {
            if (plugin.Verified) throw new InvalidDataException("A verified catalog package must have a trusted publisher signature.");
            return;
        }
        var keyPath = Path.Combine(AppContext.BaseDirectory, "trusted-publishers.json");
        var keys = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(keyPath), JsonOptions) ?? [];
        if (!keys.TryGetValue(plugin.SignerKeyId, out var encodedKey)) throw new InvalidDataException("The plugin publisher is not trusted by this Forge build.");
        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(encodedKey), out _);
        var valid = verifier.VerifyData(packageBytes, Convert.FromBase64String(plugin.Signature), HashAlgorithmName.SHA256);
        if (!valid) throw new InvalidDataException("The plugin publisher signature is invalid.");
    }
}
