using System.Diagnostics;
using System.IO.Compression;

if (args.Length != 4 || !int.TryParse(args[0], out var processId)) return 2;
var package = Path.GetFullPath(args[1]);
var appDirectory = Path.GetFullPath(args[2]).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
var executableName = args[3];
var executable = Path.Combine(appDirectory, executableName);
var marker = Path.Combine(appDirectory, ".forge-root");
if (!File.Exists(marker) || !File.Exists(package) || Path.GetFileName(executableName) != executableName) return 3;

var cache = Path.Combine(appDirectory, "data", "cache");
var runs = Path.Combine(cache, "update-runs");
var runRoot = Path.Combine(runs, $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}");
var backup = Path.Combine(runRoot, "backup");
var staging = Path.Combine(runRoot, "staging");
var logPath = Path.Combine(cache, "last-update.log");
Directory.CreateDirectory(cache);

try
{
    await File.AppendAllTextAsync(logPath, $"{DateTimeOffset.Now:u} Applying update from {package}{Environment.NewLine}");
    try { Process.GetProcessById(processId).WaitForExit(30000); } catch { }
    CleanupOldUpdateWork(cache, runs);
    Directory.CreateDirectory(backup);
    Directory.CreateDirectory(staging);
    ExtractValidated(package, staging);
    if (!File.Exists(Path.Combine(staging, executableName)) || !File.Exists(Path.Combine(staging, ".forge-root")))
        throw new InvalidDataException("The staged release is missing its launcher or portable root marker.");

    CopyTopLevel(appDirectory, backup);
    try
    {
        // Overlay the release instead of requiring wholesale directory deletion.
        // OneDrive and antivirus/indexing providers can deny deletion of synced
        // directories even when their files remain writable.
        CopyTopLevel(staging + Path.DirectorySeparatorChar, appDirectory);
        var launched = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true })
            ?? throw new InvalidOperationException("The updated Forge Tools process could not be launched.");
        await Task.Delay(TimeSpan.FromSeconds(5));
        if (launched.HasExited) throw new InvalidOperationException("Updated Forge Tools exited during startup.");
        await File.AppendAllTextAsync(logPath, $"{DateTimeOffset.Now:u} Update completed successfully.{Environment.NewLine}");
        TryDelete(runRoot);
        return 0;
    }
    catch
    {
        CopyTopLevel(backup + Path.DirectorySeparatorChar, appDirectory);
        Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
        throw;
    }
}
catch (Exception ex)
{
    try { await File.AppendAllTextAsync(logPath, $"{DateTimeOffset.Now:u} Update failed and was rolled back: {ex}{Environment.NewLine}"); } catch { }
    return 1;
}

static void CleanupOldUpdateWork(string cache, string runs)
{
    // Legacy fixed work directories may remain locked by antivirus, sync, or
    // indexing providers. Their cleanup must never be required for an update.
    TryDelete(Path.Combine(cache, "update-backup"));
    TryDelete(Path.Combine(cache, "update-staging"));
    if (!Directory.Exists(runs)) return;
    try
    {
        foreach (var directory in Directory.EnumerateDirectories(runs)) TryDelete(directory);
    }
    catch { }
}

static void TryDelete(string path)
{
    try { if (Directory.Exists(path)) Directory.Delete(path, true); }
    catch { }
}

static void CopyTopLevel(string source, string target)
{
    Directory.CreateDirectory(target);
    foreach (var directory in Directory.EnumerateDirectories(source).Where(path => !Path.GetFileName(path).Equals("data", StringComparison.OrdinalIgnoreCase)))
        CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
    foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
}

static void CopyDirectory(string source, string target)
{
    Directory.CreateDirectory(target);
    foreach (var directory in Directory.EnumerateDirectories(source)) CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
    foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
}

static void ExtractValidated(string package, string staging)
{
    var root = Path.GetFullPath(staging).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    using var archive = ZipFile.OpenRead(package);
    foreach (var entry in archive.Entries)
    {
        var destination = Path.GetFullPath(Path.Combine(staging, entry.FullName));
        if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The update archive contains an unsafe path.");
        if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(destination); continue; }
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        entry.ExtractToFile(destination, true);
    }
}
