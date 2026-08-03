using Forge.PluginSdk;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Forge.CaptureSwitcher;

public sealed class CaptureSwitcherPlugin : IForgePlugin
{
    private IForgeContext? _context;
    private IDisposable? _categorySubscription;
    private CancellationTokenSource? _lifetime;
    private Task? _targetWorker;
    private readonly SemaphoreSlim _switchGate = new(1, 1);

    public Task InitializeAsync(IForgeContext context, CancellationToken cancellationToken)
    {
        _context = context;
        WriteCaptureTargets();
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _categorySubscription = _context!.Events.Subscribe<TwitchCategoryChanged>(HandleCategoryChangedAsync);
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _targetWorker = RefreshTargetsAsync(_lifetime.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _categorySubscription?.Dispose();
        _categorySubscription = null;
        _lifetime?.Cancel();
        if (_targetWorker is not null) try { await _targetWorker.WaitAsync(cancellationToken); } catch (OperationCanceledException) { }
        _targetWorker = null;
        _lifetime?.Dispose();
        _lifetime = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _switchGate.Dispose();
    }

    private async Task HandleCategoryChangedAsync(TwitchCategoryChanged changed)
    {
        if (!_context!.Settings.Get("enabled", true)) return;
        var mapping = _context.Settings.Get("mappingEntries", new List<CaptureSwitchMapping>())
            .FirstOrDefault(item => item.CategoryId == changed.CategoryId || item.CategoryName.Equals(changed.CategoryName, StringComparison.OrdinalIgnoreCase));
        if (mapping is null) return;
        if (!_context.Connections.Obs.IsConnected) throw new InvalidOperationException("OBS is not connected.");

        await _switchGate.WaitAsync();
        try
        {
            if (!string.IsNullOrWhiteSpace(mapping.VideoInput))
                await SetCaptureTargetAsync(mapping.VideoInput, "game_capture", mapping.TargetWindow, video: true);
            if (!string.IsNullOrWhiteSpace(mapping.AudioInput))
                await SetCaptureTargetAsync(mapping.AudioInput, "wasapi_process_output_capture", mapping.TargetWindow, video: false);
            WriteStatus(changed, mapping, "Capture targets updated");
        }
        catch (Exception ex)
        {
            WriteStatus(changed, mapping, ex.GetBaseException().Message);
            throw;
        }
        finally { _switchGate.Release(); }
    }

    private async Task SetCaptureTargetAsync(string inputName, string expectedKind, string window, bool video)
    {
        var details = await _context!.Connections.Obs.RequestAsync("GetInputSettings", new { inputName });
        var kind = details.TryGetProperty("inputKind", out var inputKind) ? inputKind.GetString() : null;
        if (!string.Equals(kind, expectedKind, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"'{inputName}' is not a compatible {(video ? "Game Capture" : "Application Audio Capture")} source.");

        var settings = video
            ? new Dictionary<string, object?> { ["mode"] = "capture_specific_window", ["window"] = window }
            : new Dictionary<string, object?> { ["window"] = window };
        await _context.Connections.Obs.RequestAsync("SetInputSettings", new { inputName, inputSettings = settings, overlay = true });
    }

    private void WriteStatus(TwitchCategoryChanged changed, CaptureSwitchMapping mapping, string message)
    {
        var status = new { changed.CategoryId, changed.CategoryName, mapping.TargetProcess, mapping.VideoInput, mapping.AudioInput, message, at = DateTimeOffset.UtcNow };
        File.WriteAllText(Path.Combine(_context!.DataDirectory, "status.json"), JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void WriteCaptureTargets()
    {
        var targets = OperatingSystem.IsWindows() ? WindowsCaptureTargets.List() : [];
        File.WriteAllText(Path.Combine(_context!.DataDirectory, "capture-targets.json"), JsonSerializer.Serialize(targets, new JsonSerializerOptions { WriteIndented = true }));
    }

    private async Task RefreshTargetsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            WriteCaptureTargets();
            await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        }
    }

    private sealed record CaptureSwitchMapping(string CategoryId, string CategoryName, string TargetProcess, string TargetWindow, string? VideoInput, string? AudioInput);
    private sealed record CaptureTarget(string DisplayName, string Process, string Window);

    private static class WindowsCaptureTargets
    {
        private const int DwmwaCloaked = 14;

        public static List<CaptureTarget> List()
        {
            var targets = new List<CaptureTarget>();
            EnumWindows((window, _) =>
            {
                if (!IsWindowVisible(window) || GetWindow(window, 4) != IntPtr.Zero) return true;
                var titleLength = GetWindowTextLength(window);
                if (titleLength == 0) return true;
                if (DwmGetWindowAttribute(window, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                GetWindowThreadProcessId(window, out var processId);
                try
                {
                    using var process = Process.GetProcessById((int)processId);
                    var processName = process.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? process.ProcessName : process.ProcessName + ".exe";
                    var title = ReadWindowText(window, titleLength);
                    var className = ReadClassName(window);
                    if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(className))
                        targets.Add(new($"{processName} — {title}", processName, $"{title}:{className}:{processName}"));
                }
                catch { }
                return true;
            }, IntPtr.Zero);
            return targets.DistinctBy(item => item.Window, StringComparer.OrdinalIgnoreCase).OrderBy(item => item.Process).ThenBy(item => item.DisplayName).ToList();
        }

        private static string ReadWindowText(IntPtr window, int length) { var value = new StringBuilder(length + 1); GetWindowText(window, value, value.Capacity); return value.ToString(); }
        private static string ReadClassName(IntPtr window) { var value = new StringBuilder(256); GetClassName(window, value, value.Capacity); return value.ToString(); }
        private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder className, int count);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    }
}
