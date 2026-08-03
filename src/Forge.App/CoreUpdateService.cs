using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;

namespace Forge.App;

public sealed record CoreRelease(string Version, string PackageUrl, string Sha256, string? ReleaseNotes);

public sealed class CoreUpdateService
{
    private readonly HttpClient _http = new();
    private readonly string _cacheDirectory;
    public CoreUpdateService(string cacheDirectory) => _cacheDirectory = cacheDirectory;
    public async Task<CoreRelease?> CheckAsync(string manifestUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl)) return null;
        var json = await _http.GetStringAsync(manifestUrl, cancellationToken);
        var release = JsonSerializer.Deserialize<CoreRelease>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (release is null || !Version.TryParse(release.Version, out var available)) return null;
        var current = typeof(CoreUpdateService).Assembly.GetName().Version ?? new Version(0, 0);
        return available > current ? release : null;
    }
    public async Task<string> DownloadAndStageAsync(CoreRelease release, CancellationToken cancellationToken = default)
    {
        var bytes = await _http.GetByteArrayAsync(release.PackageUrl, cancellationToken);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Core update checksum verification failed.");
        using (var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
            if (archive.Entries.Any(entry => Path.IsPathRooted(entry.FullName) || entry.FullName.Split('/', '\\').Contains(".."))) throw new InvalidDataException("Core update contains an unsafe path.");
        var path = Path.Combine(_cacheDirectory, "core-update.zip"); await File.WriteAllBytesAsync(path, bytes, cancellationToken); return path;
    }
    public void ApplyWithUpdater(string packagePath)
    {
        var updaterName = OperatingSystem.IsWindows() ? "Forge.Updater.exe" : "Forge.Updater";
        var updater = Path.Combine(AppContext.BaseDirectory, updaterName);
        if (!File.Exists(updater)) throw new FileNotFoundException("Forge updater helper was not found.", updater);
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Forge executable path is unavailable.");
        Process.Start(new ProcessStartInfo(updater) { UseShellExecute = false, ArgumentList = { Environment.ProcessId.ToString(), packagePath, AppContext.BaseDirectory, executable } });
    }
}
