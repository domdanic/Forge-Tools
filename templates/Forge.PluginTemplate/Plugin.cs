using Forge.PluginSdk;

namespace Forge.PluginTemplate;

public sealed class Plugin : IForgePlugin
{
    private IForgeContext? _context;
    private IDisposable? _subscription;
    public Task InitializeAsync(IForgeContext context, CancellationToken cancellationToken) { _context = context; return Task.CompletedTask; }
    public Task StartAsync(CancellationToken cancellationToken) { _subscription = _context!.Events.Subscribe<ObsConnectionChanged>(message => { Console.WriteLine($"OBS connected: {message.Connected}"); return Task.CompletedTask; }); return Task.CompletedTask; }
    public Task StopAsync(CancellationToken cancellationToken) { _subscription?.Dispose(); return Task.CompletedTask; }
    public ValueTask DisposeAsync() { _subscription?.Dispose(); return ValueTask.CompletedTask; }
}
