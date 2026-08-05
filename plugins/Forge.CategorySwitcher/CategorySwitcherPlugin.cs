using Forge.PluginSdk;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Forge.CategorySwitcher;

public sealed class CategorySwitcherPlugin : IForgePlugin
{
    private IForgeContext? _context;
    private CancellationTokenSource? _lifetime;
    private Task? _worker;
    private IDisposable? _chatSubscription;
    private readonly SemaphoreSlim _categoryGate = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> _commandCooldowns = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TwitchCategory> _categoryCache = new(StringComparer.OrdinalIgnoreCase);

    public Task InitializeAsync(IForgeContext context, CancellationToken cancellationToken) { _context = context; PublishRunningApps(); return Task.CompletedTask; }
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _chatSubscription = _context!.Events.Subscribe<TwitchChatMessage>(HandleChatMessageAsync);
        _worker = RunAsync(_lifetime.Token); return Task.CompletedTask;
    }
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _chatSubscription?.Dispose(); _chatSubscription = null;
        if (_lifetime is null) return; _lifetime.Cancel();
        if (_worker is not null) try { await _worker.WaitAsync(cancellationToken); } catch (OperationCanceledException) { }
        _lifetime.Dispose(); _lifetime = null;
    }
    public async ValueTask DisposeAsync() { if (_lifetime is not null) await StopAsync(CancellationToken.None); }

    private async Task HandleChatMessageAsync(TwitchChatMessage message)
    {
        var command = message.Text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(command)) return;
        var mapping = LoadMappings().FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.ChatCommand) && item.ChatCommand.Equals(command, StringComparison.OrdinalIgnoreCase));
        if (mapping is null || !CanUseCommand(mapping.CommandAccess, message)) return;
        if (mapping.CommandAccess.Equals("everyone", StringComparison.OrdinalIgnoreCase))
        {
            if (_commandCooldowns.TryGetValue(mapping.ChatCommand, out var last) && DateTimeOffset.UtcNow - last < TimeSpan.FromSeconds(10)) return;
            _commandCooldowns[mapping.ChatCommand] = DateTimeOffset.UtcNow;
        }

        await _categoryGate.WaitAsync();
        try
        {
            var channel = await _context!.Connections.Twitch.GetChannelAsync();
            if (channel?.CategoryId != mapping.CategoryId) await _context.Connections.Twitch.UpdateCategoryAsync(mapping.CategoryId);
            WriteStatus(mapping.Process, mapping.CategoryName, $"Chat command used by {message.UserLogin}");
            await _context.Events.PublishAsync(new TwitchCategoryChanged(mapping.CategoryId, mapping.CategoryName, _context.PluginId + ".chat", DateTimeOffset.UtcNow));
        }
        finally { _categoryGate.Release(); }
    }

    private static bool CanUseCommand(string access, TwitchChatMessage message) => access.ToLowerInvariant() switch
    {
        "everyone" => true,
        "broadcaster" => message.IsBroadcaster,
        _ => message.IsBroadcaster || message.IsModerator
    };

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        string? candidate = null; DateTimeOffset candidateSince = default; string? appliedCategoryId = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                PublishRunningApps();
                if (_context!.Settings.Get("enabled", true) && OperatingSystem.IsWindows() && _context.Connections.Twitch.IsConnected)
                {
                    var process = ActiveWindow.GetProcessName();
                    if (!string.Equals(candidate, process, StringComparison.OrdinalIgnoreCase)) { candidate = process; candidateSince = DateTimeOffset.UtcNow; }
                    var delay = Math.Clamp(ParseInt(_context.Settings.Get("debounceSeconds", "5"), 5), 1, 60);
                    if (!string.IsNullOrWhiteSpace(candidate) && DateTimeOffset.UtcNow - candidateSince >= TimeSpan.FromSeconds(delay))
                    {
                        var mappings = LoadMappings();
                        var mapping = mappings.FirstOrDefault(item => item.Process.Equals(candidate, StringComparison.OrdinalIgnoreCase));
                        var categoryName = mapping?.CategoryName ?? _context.Settings.Get("fallbackCategory", "").Trim();
                        if (!string.IsNullOrWhiteSpace(categoryName) && await CanUpdateCategoryAsync(cancellationToken))
                        {
                            var category = mapping is not null && !string.IsNullOrWhiteSpace(mapping.CategoryId)
                                ? new TwitchCategory(mapping.CategoryId, mapping.CategoryName)
                                : await FindCategoryAsync(categoryName, cancellationToken);
                            if (category is not null && category.Id != appliedCategoryId)
                            {
                                var channel = await _context.Connections.Twitch.GetChannelAsync(cancellationToken);
                                if (channel?.CategoryId != category.Id) await _context.Connections.Twitch.UpdateCategoryAsync(category.Id, cancellationToken);
                                appliedCategoryId = category.Id;
                                WriteStatus(candidate, category.Name, "Updated");
                                await _context.Events.PublishAsync(
                                    new TwitchCategoryChanged(category.Id, category.Name, _context.PluginId, DateTimeOffset.UtcNow),
                                    cancellationToken);
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { WriteStatus(candidate, null, ex.Message); }
            await Task.Delay(1000, cancellationToken);
        }
    }

    private async Task<bool> CanUpdateCategoryAsync(CancellationToken cancellationToken)
    {
        var mode = _context!.Settings.Get("updateWhen", "").Trim().ToLowerInvariant();
        if (mode.Length == 0) mode = _context.Settings.Get("onlyWhileStreaming", true) ? "streaming" : "always";
        if (mode == "always") return true;
        if (!_context.Connections.Obs.IsConnected) return false;
        if (mode == "obs-connected") return true;
        var result = await _context.Connections.Obs.RequestAsync("GetStreamStatus", cancellationToken: cancellationToken);
        return result.ValueKind == JsonValueKind.Object && result.TryGetProperty("outputActive", out var active) && active.GetBoolean();
    }
    private async Task<TwitchCategory?> FindCategoryAsync(string name, CancellationToken cancellationToken)
    {
        if (_categoryCache.TryGetValue(name, out var cached)) return cached;
        var category = await _context!.Connections.Twitch.FindCategoryAsync(name, cancellationToken); if (category is not null) _categoryCache[name] = category; return category;
    }
    private void WriteStatus(string? process, string? category, string message)
    {
        var status = new { process, category, message, at = DateTimeOffset.UtcNow }; File.WriteAllText(Path.Combine(_context!.DataDirectory, "status.json"), JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }));
    }
    private void PublishRunningApps()
    {
        if (!OperatingSystem.IsWindows()) return;
        File.WriteAllText(Path.Combine(_context!.DataDirectory, "running-apps.json"), JsonSerializer.Serialize(WindowsApps.ListProcessNames(), new JsonSerializerOptions { WriteIndented = true }));
    }
    private static int ParseInt(string value, int fallback) => int.TryParse(value, out var parsed) ? parsed : fallback;
    private List<SavedMapping> LoadMappings()
    {
        var entries = _context!.Settings.Get("mappingEntries", new List<SavedMapping>());
        if (entries.Count > 0) return entries;
        return ParseMappings(_context.Settings.Get("mappings", "")).Select(pair => new SavedMapping(pair.Key, "", pair.Value)).ToList();
    }
    private static Dictionary<string, string> ParseMappings(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith('#')) continue; var separator = line.IndexOf('='); if (separator < 1) continue;
            var process = line[..separator].Trim(); var category = line[(separator + 1)..].Trim(); if (process.Length > 0 && category.Length > 0) result[process] = category;
        }
        return result;
    }

    private sealed record SavedMapping(string Process, string CategoryId, string CategoryName, string ChatCommand = "", string CommandAccess = "moderators");

    private static class ActiveWindow
    {
        public static string? GetProcessName()
        {
            var window = GetForegroundWindow(); if (window == IntPtr.Zero) return null; GetWindowThreadProcessId(window, out var processId);
            try { using var process = Process.GetProcessById((int)processId); return process.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? process.ProcessName : process.ProcessName + ".exe"; } catch { return null; }
        }
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    }

    private static class WindowsApps
    {
        private const int DwmwaCloaked = 14;
        public static List<string> ListProcessNames()
        {
            var processIds = new HashSet<uint>();
            EnumWindows((window, _) =>
            {
                if (!IsWindowVisible(window) || GetWindowTextLength(window) == 0 || GetWindow(window, 4) != IntPtr.Zero) return true;
                if (DwmGetWindowAttribute(window, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                GetWindowThreadProcessId(window, out var processId); processIds.Add(processId); return true;
            }, IntPtr.Zero);
            return processIds.Select(processId =>
            {
                try { using var process = Process.GetProcessById((int)processId); return process.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? process.ProcessName : process.ProcessName + ".exe"; }
                catch { return null; }
            }).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name).ToList()!;
        }
        private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    }
}
