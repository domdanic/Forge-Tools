using System.Diagnostics;
using System.IO.Compression;

if (args.Length != 4 || !int.TryParse(args[0], out var processId)) return 2;
var package = Path.GetFullPath(args[1]); var appDirectory = Path.GetFullPath(args[2]); var executable = Path.GetFullPath(args[3]);
if (!File.Exists(Path.Combine(appDirectory, ".forge-root")) || !File.Exists(package) || !executable.StartsWith(appDirectory, StringComparison.OrdinalIgnoreCase)) return 3;
try { Process.GetProcessById(processId).WaitForExit(30000); } catch { }
var backup = Path.Combine(appDirectory, "data", "cache", "update-backup");
var staging = Path.Combine(appDirectory, "data", "cache", "update-staging");
if (Directory.Exists(backup)) Directory.Delete(backup, true);
if (Directory.Exists(staging)) Directory.Delete(staging, true);
Directory.CreateDirectory(backup); Directory.CreateDirectory(staging);
try
{
    ZipFile.ExtractToDirectory(package, staging);
    CopyTree(appDirectory, backup, path => !IsRuntimeData(appDirectory, path));
    CopyTree(staging, appDirectory, _ => true);
    Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
    return 0;
}
catch
{
    try { CopyTree(backup, appDirectory, _ => true); Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true }); } catch { }
    return 1;
}

static bool IsRuntimeData(string root, string path)
{
    var relative = Path.GetRelativePath(root, path); return relative.Equals("data", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("data" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
static void CopyTree(string source, string target, Func<string, bool> include)
{
    foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories).Where(include)) Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Where(include)) { var destination = Path.Combine(target, Path.GetRelativePath(source, file)); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(file, destination, true); }
}
