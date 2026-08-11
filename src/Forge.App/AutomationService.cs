using Forge.PluginSdk;
using System.Text.Json;

namespace Forge.App;

public sealed class AutomationService : IForgeAutomation
{
    private readonly string _path;
    private readonly ForgeLogger _log;
    private readonly object _sync = new();
    private readonly Dictionary<string, AutomationTriggerDefinition> _triggers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RegisteredAction> _actions = new(StringComparer.OrdinalIgnoreCase);
    private List<AutomationBinding> _bindings;

    public AutomationService(string settingsDirectory, ForgeLogger log)
    {
        _path = Path.Combine(settingsDirectory, "automations.json");
        _log = log;
        _bindings = Load();
    }

    public event EventHandler? Changed;
    public IReadOnlyList<AutomationTriggerDefinition> Triggers { get { lock (_sync) return _triggers.Values.OrderBy(x => x.Name).ToList(); } }
    public IReadOnlyList<AutomationActionDefinition> Actions { get { lock (_sync) return _actions.Values.Select(x => x.Definition).OrderBy(x => x.Name).ToList(); } }
    public IReadOnlyList<AutomationBinding> Bindings { get { lock (_sync) return [.. _bindings]; } }

    public IDisposable RegisterTrigger(AutomationTriggerDefinition definition)
    {
        ValidateId(definition.Id);
        lock (_sync) _triggers[definition.Id] = definition;
        Changed?.Invoke(this, EventArgs.Empty);
        return new Registration(() => { lock (_sync) _triggers.Remove(definition.Id); Changed?.Invoke(this, EventArgs.Empty); });
    }

    public IDisposable RegisterAction(AutomationActionDefinition definition, Func<AutomationActionInvocation, CancellationToken, Task> handler)
    {
        ValidateId(definition.Id);
        lock (_sync) _actions[definition.Id] = new(definition, handler);
        Changed?.Invoke(this, EventArgs.Empty);
        return new Registration(() => { lock (_sync) _actions.Remove(definition.Id); Changed?.Invoke(this, EventArgs.Empty); });
    }

    public void SaveBinding(AutomationBinding binding)
    {
        lock (_sync)
        {
            var index = _bindings.FindIndex(x => x.Id.Equals(binding.Id, StringComparison.OrdinalIgnoreCase));
            if (index < 0) _bindings.Add(binding); else _bindings[index] = binding;
            Save();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveBinding(string id)
    {
        lock (_sync) { _bindings.RemoveAll(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase)); Save(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task FireAsync(string triggerId, IReadOnlyDictionary<string, string>? variables = null, CancellationToken cancellationToken = default)
    {
        List<AutomationBinding> bindings;
        lock (_sync) bindings = _bindings.Where(x => x.Enabled && x.TriggerId.Equals(triggerId, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var binding in bindings)
        {
            foreach (var step in binding.Actions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (step.DelayMilliseconds > 0) await Task.Delay(Math.Clamp(step.DelayMilliseconds, 0, 86_400_000), cancellationToken);
                RegisteredAction? action; lock (_sync) _actions.TryGetValue(step.ActionId, out action);
                if (action is null) { await _log.WriteAsync("WARN", "forge.core.automation", $"Binding {binding.Name} skipped missing action {step.ActionId}."); continue; }
                try { await action.Handler(new(step.Configuration, variables ?? new Dictionary<string, string>()), cancellationToken); }
                catch (Exception ex) { await _log.WriteAsync("ERROR", action.Definition.Id, $"Automation binding {binding.Name} failed an action but continued.", ex); }
            }
        }
    }

    private List<AutomationBinding> Load() { try { return JsonSerializer.Deserialize<List<AutomationBinding>>(File.ReadAllText(_path)) ?? []; } catch { return []; } }
    private void Save() => File.WriteAllText(_path, JsonSerializer.Serialize(_bindings, new JsonSerializerOptions { WriteIndented = true }));
    private static void ValidateId(string id) { if (string.IsNullOrWhiteSpace(id) || !id.Contains('.')) throw new ArgumentException("Automation IDs must be stable namespaced identifiers.", nameof(id)); }
    private sealed record RegisteredAction(AutomationActionDefinition Definition, Func<AutomationActionInvocation, CancellationToken, Task> Handler);
    private sealed class Registration(Action dispose) : IDisposable { private Action? _dispose = dispose; public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke(); }
}
