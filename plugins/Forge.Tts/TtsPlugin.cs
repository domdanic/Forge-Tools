using Forge.PluginSdk;
using NAudio.Wave;
using System.Net.Http.Headers;
using System.Speech.Synthesis;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;
using System.Diagnostics;

namespace Forge.Tts;

public sealed class TtsPlugin : IForgePlugin
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(2) };
    private readonly SemaphoreSlim _queue = new(1, 1);
    private IForgeContext? _context;
    private IDisposable? _registration;

    public Task InitializeAsync(IForgeContext context, CancellationToken cancellationToken) { _context = context; return Task.CompletedTask; }
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _registration = _context!.Automation.RegisterAction(new(
            "tools.forge.tts.speak", "Speak text", "Speaks a template through the configured TTS provider.",
            [
                new("text", "Text or template", "multiline", "Use values such as {user}, {input}, and {reward} when the trigger provides them.", "{input}", true),
                new("voice", "Voice override (optional)", Description: "Windows voice name or ElevenLabs voice ID. Leave blank to use plugin settings."),
                new("rate", "Windows speech rate (-10 to 10)", Default: "0"),
                new("volume", "Volume (0–100)", Default: "100")
            ]), SpeakAsync);
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) { _registration?.Dispose(); _registration = null; return Task.CompletedTask; }
    public ValueTask DisposeAsync() { _registration?.Dispose(); _http.Dispose(); _queue.Dispose(); return ValueTask.CompletedTask; }

    private async Task SpeakAsync(AutomationActionInvocation invocation, CancellationToken cancellationToken)
    {
        var text = Expand(Read(invocation.Configuration, "text", "{input}"), invocation.Variables).Trim();
        var max = Math.Clamp(Parse(_context!.Settings.Get("maxCharacters", "300"), 300), 1, 5000);
        if (text.Length == 0) return;
        if (text.Length > max) text = text[..max];
        var blocked = _context.Settings.Get("blockedTerms", "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (blocked.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("TTS text contained a blocked term.");
        await _queue.WaitAsync(cancellationToken);
        try
        {
            var provider = _context.Settings.Get("provider", "system");
            if (provider.Equals("elevenlabs", StringComparison.OrdinalIgnoreCase)) await SpeakElevenLabsAsync(text, Read(invocation.Configuration, "voice"), cancellationToken);
            else
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows system speech is not available on this operating system. Configure a cloud TTS provider instead.");
                await SpeakSystemAsync(text, Read(invocation.Configuration, "voice"), Parse(Read(invocation.Configuration, "rate"), 0), Parse(Read(invocation.Configuration, "volume"), 100), cancellationToken);
            }
        }
        finally { _queue.Release(); }
    }

    [SupportedOSPlatform("windows")]
    private Task SpeakSystemAsync(string text, string voiceOverride, int rate, int volume, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows system speech is not available on this operating system. Configure a cloud TTS provider instead.");
        return Task.Run(() =>
        {
            using var synth = new SpeechSynthesizer();
            var voice = string.IsNullOrWhiteSpace(voiceOverride) ? _context!.Settings.Get("systemVoice", "") : voiceOverride;
            if (!string.IsNullOrWhiteSpace(voice)) synth.SelectVoice(voice);
            synth.Rate = Math.Clamp(rate, -10, 10); synth.Volume = Math.Clamp(volume, 0, 100); synth.SetOutputToDefaultAudioDevice();
            cancellationToken.ThrowIfCancellationRequested(); synth.Speak(text);
        }, cancellationToken);
    }

    private async Task SpeakElevenLabsAsync(string text, string voiceOverride, CancellationToken cancellationToken)
    {
        var key = _context!.Secrets.Load("elevenLabsApiKey");
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("Enter an ElevenLabs API key in the TTS plugin settings.");
        var voice = string.IsNullOrWhiteSpace(voiceOverride) ? _context.Settings.Get("elevenLabsVoiceId", "") : voiceOverride;
        if (string.IsNullOrWhiteSpace(voice)) throw new InvalidOperationException("Enter an ElevenLabs voice ID in the TTS plugin settings.");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.elevenlabs.io/v1/text-to-speech/{Uri.EscapeDataString(voice)}?output_format=mp3_44100_128");
        request.Headers.Add("xi-api-key", key); request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));
        request.Content = new StringContent(JsonSerializer.Serialize(new { text, model_id = _context.Settings.Get("elevenLabsModel", "eleven_multilingual_v2") }), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"ElevenLabs returned HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}");
        await using var audio = await response.Content.ReadAsStreamAsync(cancellationToken);
        if (OperatingSystem.IsWindows())
        {
            using var reader = new Mp3FileReader(audio); using var output = new WaveOutEvent(); output.Init(reader);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); output.PlaybackStopped += (_, e) => { if (e.Exception is null) done.TrySetResult(); else done.TrySetException(e.Exception); };
            using var stop = cancellationToken.Register(output.Stop); output.Play(); await done.Task;
            return;
        }
        var temp = Path.Combine(_context.DataDirectory, "tts-" + Guid.NewGuid().ToString("N") + ".mp3");
        try
        {
            await using (var file = File.Create(temp)) await audio.CopyToAsync(file, cancellationToken);
            foreach (var command in new[] { "ffplay", "paplay" })
            {
                try
                {
                    var start = new ProcessStartInfo(command) { UseShellExecute = false, CreateNoWindow = true };
                    if (command == "ffplay") { start.ArgumentList.Add("-nodisp"); start.ArgumentList.Add("-autoexit"); start.ArgumentList.Add("-loglevel"); start.ArgumentList.Add("error"); }
                    start.ArgumentList.Add(temp); using var process = Process.Start(start); if (process is null) continue; await process.WaitForExitAsync(cancellationToken); if (process.ExitCode == 0) return;
                }
                catch (System.ComponentModel.Win32Exception) { }
            }
            throw new PlatformNotSupportedException("ElevenLabs generated speech, but no Linux audio player was found. Install ffplay or paplay.");
        }
        finally { try { File.Delete(temp); } catch { } }
    }

    private static string Expand(string template, IReadOnlyDictionary<string, string> variables) { foreach (var pair in variables) template = template.Replace("{" + pair.Key + "}", pair.Value, StringComparison.OrdinalIgnoreCase); return template; }
    private static string Read(JsonElement json, string key, string fallback = "") => json.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
    private static int Parse(string value, int fallback) => int.TryParse(value, out var parsed) ? parsed : fallback;
}
