using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Forge.App;

public sealed record CoreRelease(Version Version, string PackageUrl, string ChecksumUrl, string? ReleaseNotes, string? ReleaseUrl);

public sealed class CoreUpdateService
{
    private const long MaximumDownloadBytes = 500L * 1024 * 1024;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly string _cacheDirectory;

    public CoreUpdateService(string cacheDirectory) => _cacheDirectory = cacheDirectory;

    public async Task<CoreRelease?> CheckAsync(string repository, CancellationToken cancellationToken = default)
    {
        if (repository.Split('/', StringSplitOptions.RemoveEmptyEntries).Length != 2)
            throw new InvalidOperationException("The Core update repository is not configured correctly.");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository.Trim()}/releases?per_page=30");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ForgeTools", CurrentVersion.ToString()));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        CoreRelease? newest = null;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
            if (release.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean()) continue;
            var tag = GetString(release, "tag_name");
            if (tag is null || !tag.StartsWith("core-v", StringComparison.OrdinalIgnoreCase) || !Version.TryParse(tag[6..], out var version)) continue;
            if (version <= CurrentVersion || newest is not null && version <= newest.Version) continue;

            var packageName = OperatingSystem.IsWindows() ? "ForgeTools-win-x64.zip" : "ForgeTools-linux-x64.zip";
            var assets = release.GetProperty("assets").EnumerateArray().ToArray();
            var packageUrl = AssetUrl(assets, packageName);
            var checksumUrl = AssetUrl(assets, packageName + ".sha256");
            if (packageUrl is null || checksumUrl is null)
                throw new InvalidOperationException($"Forge Tools {version} cannot be installed automatically because its package or checksum is missing.");
            newest = new CoreRelease(version, packageUrl, checksumUrl, GetString(release, "body"), GetString(release, "html_url"));
        }

        return newest;
    }

    public async Task<string> DownloadAndStageAsync(CoreRelease release, CancellationToken cancellationToken = default)
    {
        var packageUri = ValidateGitHubUri(release.PackageUrl);
        var checksumUri = ValidateGitHubUri(release.ChecksumUrl);
        var updateDirectory = Path.Combine(_cacheDirectory, "core-update");
        Directory.CreateDirectory(updateDirectory);
        var packagePath = Path.Combine(updateDirectory, $"ForgeTools-{release.Version}.zip");
        var checksumPath = packagePath + ".sha256";
        await DownloadFileAsync(packageUri, packagePath, MaximumDownloadBytes, cancellationToken);
        await DownloadFileAsync(checksumUri, checksumPath, 64 * 1024, cancellationToken);
        await VerifyChecksumAsync(packagePath, checksumPath, cancellationToken);
        ValidateArchive(packagePath);
        return packagePath;
    }

    public void ApplyWithUpdater(string packagePath)
    {
        var appDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        VerifyTargetWritable(appDirectory);
        var updaterName = OperatingSystem.IsWindows() ? "Forge.Updater.exe" : "Forge.Updater";
        var updaterSource = Path.Combine(appDirectory, updaterName);
        if (!File.Exists(updaterSource)) throw new FileNotFoundException("Forge updater helper was not found.", updaterSource);

        // Run the helper shipped by the incoming release from portable data. This lets
        // updater fixes take effect before any live application files are replaced.
        var helperDirectory = Path.Combine(_cacheDirectory, "updater-runtime");
        if (Directory.Exists(helperDirectory)) Directory.Delete(helperDirectory, true);
        ZipFile.ExtractToDirectory(packagePath, helperDirectory);
        var updater = Path.Combine(helperDirectory, updaterName);
        if (!File.Exists(updater)) throw new InvalidDataException("The Core update does not contain its updater helper.");
        var executableName = OperatingSystem.IsWindows() ? "Forge.App.exe" : "Forge.App";
        var start = new ProcessStartInfo(updater) { UseShellExecute = false };
        start.ArgumentList.Add(Environment.ProcessId.ToString());
        start.ArgumentList.Add(Path.GetFullPath(packagePath));
        start.ArgumentList.Add(appDirectory);
        start.ArgumentList.Add(executableName);
        _ = Process.Start(start) ?? throw new InvalidOperationException("Unable to launch the Forge updater helper.");
    }

    public static Version CurrentVersion => Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

    private static string? AssetUrl(IEnumerable<JsonElement> assets, string name) => assets
        .Where(asset => string.Equals(GetString(asset, "name"), name, StringComparison.OrdinalIgnoreCase))
        .Select(asset => GetString(asset, "browser_download_url"))
        .FirstOrDefault();

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Uri ValidateGitHubUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Automatic updates only accept HTTPS GitHub release URLs.");
        return uri;
    }

    private static void VerifyTargetWritable(string appDirectory)
    {
        var probe = Path.Combine(appDirectory, $".forge-update-test-{Guid.NewGuid():N}");
        try { using (File.Create(probe)) { } }
        catch (Exception ex) { throw new UnauthorizedAccessException("Forge Tools cannot update because its application folder is not writable.", ex); }
        finally { if (File.Exists(probe)) File.Delete(probe); }
    }

    private async Task DownloadFileAsync(Uri uri, string destination, long limit, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("The update asset exceeds the allowed download size.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        var buffer = new byte[81920]; long total = 0; int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read; if (total > limit) throw new InvalidDataException("The update asset exceeds the allowed download size.");
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static async Task VerifyChecksumAsync(string packagePath, string checksumPath, CancellationToken cancellationToken)
    {
        var expected = Regex.Match(await File.ReadAllTextAsync(checksumPath, cancellationToken), @"\b[A-Fa-f0-9]{64}\b").Value;
        if (expected.Length != 64) throw new InvalidDataException("The release checksum file is invalid.");
        await using var package = File.OpenRead(packagePath);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(package, cancellationToken));
        if (!expected.Equals(actual, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The downloaded update failed SHA-256 verification.");
    }

    private static void ValidateArchive(string packagePath)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        var names = archive.Entries.Select(entry => entry.FullName).ToArray();
        if (archive.Entries.Any(entry => Path.IsPathRooted(entry.FullName) || entry.FullName.Split('/', '\\').Contains(".."))) throw new InvalidDataException("The Core update contains an unsafe path.");
        var launcher = OperatingSystem.IsWindows() ? "Forge.App.exe" : "Forge.App";
        if (!names.Contains(launcher, StringComparer.OrdinalIgnoreCase) || !names.Contains(".forge-root", StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("The Core update is missing its launcher or portable root marker.");
    }
}
