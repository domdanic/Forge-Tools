using Forge.PluginSdk;
using NAudio.Wave;
using System.Diagnostics;
using System.Text.Json;

namespace Forge.AudioPlayer;

public sealed class AudioPlayerPlugin : IForgePlugin
{
    private IForgeContext? _context;
    private IDisposable? _registration;
    private readonly object _sync = new();
    private readonly Dictionary<string, Playback> _active = new(StringComparer.OrdinalIgnoreCase);

    public Task InitializeAsync(IForgeContext context, CancellationToken cancellationToken) { _context = context; return Task.CompletedTask; }
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _registration = _context!.Automation.RegisterAction(new(
            "tools.forge.audio-player.play-file", "Play audio file", "Plays an audio file without copying it into Forge.",
            [
                new("path", "Audio file", "file", "Forge stores this path as a reference and does not copy the file.", Required: true),
                new("volume", "Volume (0–100)", Default: "100"),
                new("overlap", "If already playing", "select", Default: "overlap", Options: [new() { Label = "Play another copy", Value = "overlap" }, new() { Label = "Restart", Value = "restart" }, new() { Label = "Ignore this trigger", Value = "ignore" }])
            ]), PlayAsync);
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) { _registration?.Dispose(); _registration = null; Playback[] active; lock (_sync) { active = [.. _active.Values]; _active.Clear(); } foreach (var playback in active) playback.Dispose(); return Task.CompletedTask; }
    public ValueTask DisposeAsync() { _registration?.Dispose(); return ValueTask.CompletedTask; }

    private async Task PlayAsync(AutomationActionInvocation invocation, CancellationToken cancellationToken)
    {
        var path = Read(invocation.Configuration, "path");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) throw new FileNotFoundException("The configured audio file could not be found. Audio Player does not copy source files.", path);
        var behavior = Read(invocation.Configuration, "overlap", "overlap");
        lock (_sync)
        {
            if (_active.TryGetValue(path, out var existing))
            {
                if (behavior == "ignore") return;
                if (behavior == "restart") { existing.Dispose(); _active.Remove(path); }
            }
        }
        if (OperatingSystem.IsWindows()) await PlayWindowsAsync(path, Math.Clamp(ReadInt(invocation.Configuration, "volume", 100), 0, 100) / 100f, cancellationToken);
        else await PlayExternalAsync(path, cancellationToken);
    }

    private async Task PlayWindowsAsync(string path, float volume, CancellationToken cancellationToken)
    {
        var reader = new AudioFileReader(path) { Volume = volume };
        var output = new WaveOutEvent(); output.Init(reader);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var playback = new Playback(output, reader, done);
        lock (_sync) _active[path] = playback;
        output.PlaybackStopped += (_, e) => { lock (_sync) { if (_active.TryGetValue(path, out var current) && ReferenceEquals(current, playback)) _active.Remove(path); } if (e.Exception is null) done.TrySetResult(); else done.TrySetException(e.Exception); playback.Dispose(); };
        using var stop = cancellationToken.Register(() => { output.Stop(); done.TrySetCanceled(cancellationToken); });
        output.Play(); await done.Task;
    }

    private async Task PlayExternalAsync(string path, CancellationToken cancellationToken)
    {
        var configured = _context!.Settings.Get("linuxPlayer", "").Trim();
        var candidates = string.IsNullOrWhiteSpace(configured) ? new[] { "ffplay", "paplay", "aplay" } : new[] { configured };
        foreach (var command in candidates)
        {
            try
            {
                var start = new ProcessStartInfo(command) { UseShellExecute = false, CreateNoWindow = true };
                if (Path.GetFileName(command).StartsWith("ffplay", StringComparison.OrdinalIgnoreCase)) { start.ArgumentList.Add("-nodisp"); start.ArgumentList.Add("-autoexit"); start.ArgumentList.Add("-loglevel"); start.ArgumentList.Add("error"); }
                start.ArgumentList.Add(path);
                using var process = Process.Start(start); if (process is null) continue;
                await process.WaitForExitAsync(cancellationToken); if (process.ExitCode != 0) throw new InvalidOperationException($"{command} exited with code {process.ExitCode}."); return;
            }
            catch (System.ComponentModel.Win32Exception) { }
        }
        throw new PlatformNotSupportedException("No supported Linux audio player was found. Install ffplay, paplay, or aplay, or configure the player command in Audio Player.");
    }

    private static string Read(JsonElement json, string key, string fallback = "") => json.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
    private static int ReadInt(JsonElement json, string key, int fallback) => int.TryParse(Read(json, key), out var value) ? value : fallback;
    private sealed class Playback(WaveOutEvent output, AudioFileReader reader, TaskCompletionSource completion) : IDisposable { public void Dispose() { try { output.Stop(); } catch { } output.Dispose(); reader.Dispose(); completion.TrySetResult(); } }
}
