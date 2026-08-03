using System.IO.Compression;
using System.Text.Json;

namespace Forge.App;

public sealed class DiagnosticsService
{
    private readonly PluginManager _plugins;
    public DiagnosticsService(PluginManager plugins) => _plugins = plugins;
    public string CreateBundle()
    {
        var path = Path.Combine(_plugins.CacheDirectory, $"forge-diagnostics-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var system = JsonSerializer.Serialize(new { forgeVersion = typeof(DiagnosticsService).Assembly.GetName().Version?.ToString(), os = Environment.OSVersion.ToString(), framework = Environment.Version.ToString(), architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(), profile = _plugins.Profiles.ActiveProfileId }, new JsonSerializerOptions { WriteIndented = true });
        Write(archive, "system.json", system);
        foreach (var plugin in _plugins.Discover()) Write(archive, $"plugins/{plugin.Manifest.Id}.json", JsonSerializer.Serialize(plugin.Manifest, new JsonSerializerOptions { WriteIndented = true }));
        foreach (var log in Directory.EnumerateFiles(_plugins.LogsDirectory, "*.log").OrderByDescending(File.GetLastWriteTimeUtc).Take(5)) archive.CreateEntryFromFile(log, "logs/" + Path.GetFileName(log));
        Write(archive, "NOTICE.txt", "This bundle excludes settings and credentials. Logs are sanitized when written, but review before sharing.");
        return path;
    }
    private static void Write(ZipArchive archive, string name, string content) { var entry = archive.CreateEntry(name); using var writer = new StreamWriter(entry.Open()); writer.Write(content); }
}
