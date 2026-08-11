using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Forge.PluginSdk;
using System.Text.Json;

namespace Forge.App;

public sealed class AutomationBindingEditor : StackPanel
{
    private readonly AutomationService _automation;
    private readonly string _pluginId;
    private readonly string _view;
    private readonly Action<string> _status;
    private readonly StackPanel _items = new() { Spacing = 10 };

    public AutomationBindingEditor(AutomationService automation, string pluginId, string? view, Action<string> status)
    {
        _automation = automation; _pluginId = pluginId; _view = view ?? "all"; _status = status;
        Spacing = 10;
        var add = new Button { Content = "Add automation", HorizontalAlignment = HorizontalAlignment.Left };
        add.Click += (_, _) => { var trigger = RelevantTriggers().FirstOrDefault(); var action = RelevantActions().FirstOrDefault(); if (trigger is null || action is null) { _status("Install and enable at least one compatible trigger and action plugin first."); return; } _automation.SaveBinding(new(Guid.NewGuid().ToString("N"), "New automation", trigger.Id, true, [new(action.Id, EmptyObject())])); Refresh(); };
        Children.Add(add); Children.Add(_items);
        _automation.Changed += OnChanged;
        DetachedFromVisualTree += (_, _) => _automation.Changed -= OnChanged;
        Refresh();
    }

    private void OnChanged(object? sender, EventArgs e) { if (Dispatcher.UIThread.CheckAccess()) Refresh(); else Dispatcher.UIThread.Post(Refresh); }
    private IEnumerable<AutomationTriggerDefinition> RelevantTriggers() => _view.Equals("trigger", StringComparison.OrdinalIgnoreCase) ? _automation.Triggers.Where(x => x.Id.StartsWith(_pluginId + ".", StringComparison.OrdinalIgnoreCase)) : _automation.Triggers;
    private IEnumerable<AutomationActionDefinition> RelevantActions() => _view.Equals("action", StringComparison.OrdinalIgnoreCase) ? _automation.Actions.Where(x => x.Id.StartsWith(_pluginId + ".", StringComparison.OrdinalIgnoreCase)) : _automation.Actions;
    private bool Relevant(AutomationBinding binding) => _view.Equals("trigger", StringComparison.OrdinalIgnoreCase)
        ? binding.TriggerId.StartsWith(_pluginId + ".", StringComparison.OrdinalIgnoreCase)
        : _view.Equals("action", StringComparison.OrdinalIgnoreCase)
            ? binding.Actions.Any(x => x.ActionId.StartsWith(_pluginId + ".", StringComparison.OrdinalIgnoreCase))
            : true;

    private void Refresh()
    {
        _items.Children.Clear();
        var bindings = _automation.Bindings.Where(Relevant).ToList();
        if (bindings.Count == 0) _items.Children.Add(new TextBlock { Text = "No automations yet.", Foreground = Brushes.LightGray });
        foreach (var binding in bindings) _items.Children.Add(BuildBinding(binding));
    }

    private Control BuildBinding(AutomationBinding initial)
    {
        var binding = initial;
        var body = new StackPanel { Spacing = 8 };
        var top = new Grid { ColumnDefinitions = new("*,Auto,Auto") };
        var name = new TextBox { Text = binding.Name, PlaceholderText = "Automation name" };
        var enabled = new CheckBox { Content = "Enabled", IsChecked = binding.Enabled, Margin = new(10, 0) }; enabled.SetValue(Grid.ColumnProperty, 1);
        var remove = new Button { Content = "Remove" }; remove.SetValue(Grid.ColumnProperty, 2);
        top.Children.Add(name); top.Children.Add(enabled); top.Children.Add(remove); body.Children.Add(top);

        body.Children.Add(new TextBlock { Text = "When this happens" });
        var triggers = RelevantTriggers().ToList();
        var trigger = new ComboBox { ItemsSource = triggers, SelectedItem = triggers.FirstOrDefault(x => x.Id.Equals(binding.TriggerId, StringComparison.OrdinalIgnoreCase)), ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<AutomationTriggerDefinition>((x, _) => new TextBlock { Text = x.Name }) };
        if (trigger.SelectedItem is null) trigger.ItemsSource = new[] { new AutomationTriggerDefinition(binding.TriggerId, "Missing trigger: " + binding.TriggerId) }.Concat(triggers).ToList();
        body.Children.Add(trigger);
        var steps = new StackPanel { Spacing = 8 }; body.Children.Add(new TextBlock { Text = "Do these actions in order", FontWeight = FontWeight.Medium }); body.Children.Add(steps);

        void Commit()
        {
            var currentTrigger = (trigger.SelectedItem as AutomationTriggerDefinition)?.Id ?? binding.TriggerId;
            binding = binding with { Name = string.IsNullOrWhiteSpace(name.Text) ? "Untitled automation" : name.Text.Trim(), Enabled = enabled.IsChecked == true, TriggerId = currentTrigger };
            _automation.SaveBinding(binding); _status("Automation saved");
        }
        void RebuildSteps()
        {
            steps.Children.Clear();
            for (var i = 0; i < binding.Actions.Count; i++) steps.Children.Add(BuildStep(binding, i, updated => { binding = updated; _automation.SaveBinding(binding); RebuildSteps(); }));
            var addAction = new Button { Content = "Add action", HorizontalAlignment = HorizontalAlignment.Left };
            addAction.Click += (_, _) => { var action = RelevantActions().FirstOrDefault(); if (action is null) { _status("No action plugins are currently available."); return; } binding = binding with { Actions = [.. binding.Actions, new AutomationActionStep(action.Id, EmptyObject())] }; _automation.SaveBinding(binding); RebuildSteps(); };
            steps.Children.Add(addAction);
        }
        name.LostFocus += (_, _) => Commit(); enabled.IsCheckedChanged += (_, _) => Commit(); trigger.SelectionChanged += (_, _) => Commit();
        remove.Click += (_, _) => { _automation.RemoveBinding(binding.Id); _status("Automation removed"); Refresh(); };
        RebuildSteps();
        return new Border { Background = Brush.Parse("#191C22"), Padding = new(14), CornerRadius = new(5), Child = body };
    }

    private Control BuildStep(AutomationBinding binding, int index, Action<AutomationBinding> update)
    {
        var step = binding.Actions[index];
        var panel = new StackPanel { Spacing = 6, Margin = new(8, 4) };
        var header = new Grid { ColumnDefinitions = new("*,110,Auto,Auto,Auto") };
        var actions = RelevantActions().ToList();
        if (!actions.Any(x => x.Id.Equals(step.ActionId, StringComparison.OrdinalIgnoreCase))) actions.Insert(0, new(step.ActionId, "Missing action: " + step.ActionId, "Plugin is not installed or enabled.", []));
        var select = new ComboBox { ItemsSource = actions, SelectedItem = actions.First(x => x.Id.Equals(step.ActionId, StringComparison.OrdinalIgnoreCase)), ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<AutomationActionDefinition>((x, _) => new TextBlock { Text = x.Name }) };
        var delay = new NumericUpDown { Minimum = 0, Maximum = 86400, Value = step.DelayMilliseconds / 1000m, FormatString = "0 sec" }; delay.SetValue(Grid.ColumnProperty, 1);
        var up = new Button { Content = "↑", IsEnabled = index > 0 }; up.SetValue(Grid.ColumnProperty, 2);
        var down = new Button { Content = "↓", IsEnabled = index < binding.Actions.Count - 1 }; down.SetValue(Grid.ColumnProperty, 3);
        var remove = new Button { Content = "Remove" }; remove.SetValue(Grid.ColumnProperty, 4);
        header.Children.Add(select); header.Children.Add(delay); header.Children.Add(up); header.Children.Add(down); header.Children.Add(remove); panel.Children.Add(header);
        var fields = new StackPanel { Spacing = 5 }; panel.Children.Add(fields);

        void SaveConfig(string actionId, Dictionary<string, object?> values)
        {
            var list = binding.Actions.ToList(); list[index] = step = step with { ActionId = actionId, Configuration = JsonSerializer.SerializeToElement(values), DelayMilliseconds = (int)((delay.Value ?? 0) * 1000) }; update(binding with { Actions = list });
        }
        void BuildFields()
        {
            fields.Children.Clear();
            var definition = select.SelectedItem as AutomationActionDefinition;
            if (definition is null || definition.Name.StartsWith("Missing action:", StringComparison.Ordinal)) { fields.Children.Add(new TextBlock { Text = "This action will resume automatically when its plugin is installed and enabled.", Foreground = Brushes.Goldenrod }); return; }
            var values = ReadValues(step.Configuration);
            foreach (var parameter in definition.Parameters)
            {
                fields.Children.Add(new TextBlock { Text = parameter.Label });
                Control control;
                if (parameter.Type.Equals("file", StringComparison.OrdinalIgnoreCase))
                {
                    var row = new Grid { ColumnDefinitions = new("*,Auto") }; var path = new TextBox { Text = Value(values, parameter), PlaceholderText = "Choose an audio file" }; var browse = new Button { Content = "Browse…" }; browse.SetValue(Grid.ColumnProperty, 1); row.Children.Add(path); row.Children.Add(browse);
                    browse.Click += async (_, _) => { var top = TopLevel.GetTopLevel(this); if (top is null) return; var files = await top.StorageProvider.OpenFilePickerAsync(new() { Title = parameter.Label, AllowMultiple = false }); if (files.Count > 0) { path.Text = files[0].TryGetLocalPath() ?? files[0].Path.LocalPath; values[parameter.Key] = path.Text; SaveConfig(definition.Id, values); } };
                    path.LostFocus += (_, _) => { values[parameter.Key] = path.Text; SaveConfig(definition.Id, values); }; control = row;
                }
                else if (parameter.Type.Equals("select", StringComparison.OrdinalIgnoreCase))
                {
                    var options = parameter.Options ?? []; var combo = new ComboBox { ItemsSource = options, SelectedItem = options.FirstOrDefault(x => x.Value == Value(values, parameter)), ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<UiOption>((x, _) => new TextBlock { Text = x.Label }) };
                    combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is UiOption option) { values[parameter.Key] = option.Value; SaveConfig(definition.Id, values); } }; control = combo;
                }
                else
                {
                    var text = new TextBox { Text = Value(values, parameter), AcceptsReturn = parameter.Type.Equals("multiline", StringComparison.OrdinalIgnoreCase), MinHeight = parameter.Type.Equals("multiline", StringComparison.OrdinalIgnoreCase) ? 80 : 0 };
                    text.LostFocus += (_, _) => { values[parameter.Key] = text.Text; SaveConfig(definition.Id, values); }; control = text;
                }
                if (!string.IsNullOrWhiteSpace(parameter.Description)) ToolTip.SetTip(control, parameter.Description);
                fields.Children.Add(control);
            }
        }
        select.SelectionChanged += (_, _) => { if (select.SelectedItem is AutomationActionDefinition action) { step = step with { ActionId = action.Id, Configuration = EmptyObject() }; BuildFields(); SaveConfig(action.Id, ReadValues(step.Configuration)); } };
        delay.ValueChanged += (_, _) => SaveConfig(step.ActionId, ReadValues(step.Configuration));
        remove.Click += (_, _) => { var list = binding.Actions.ToList(); list.RemoveAt(index); update(binding with { Actions = list }); };
        up.Click += (_, _) => Move(-1); down.Click += (_, _) => Move(1);
        void Move(int delta) { var list = binding.Actions.ToList(); (list[index], list[index + delta]) = (list[index + delta], list[index]); update(binding with { Actions = list }); }
        BuildFields(); return panel;
    }

    private static Dictionary<string, object?> ReadValues(JsonElement value) { try { return value.Deserialize<Dictionary<string, object?>>() ?? []; } catch { return []; } }
    private static string Value(Dictionary<string, object?> values, AutomationParameter parameter) => values.TryGetValue(parameter.Key, out var value) ? value switch { JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString() ?? "", _ => value?.ToString() ?? "" } : parameter.Default ?? "";
    private static JsonElement EmptyObject() => JsonSerializer.SerializeToElement(new Dictionary<string, object?>());
}
