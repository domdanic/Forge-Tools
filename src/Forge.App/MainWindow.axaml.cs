using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Forge.PluginSdk;
using System.Text.Json;

namespace Forge.App;

public sealed partial class MainWindow : Window
{
    private static readonly IBrush PanelBrush = Brush.Parse("#191C22");
    private readonly PluginManager _plugins = new();
    private readonly TwitchAuthService _twitch;
    private readonly ForgeEventBus _events = new();
    private readonly ForgeLogger _logger;
    private readonly CredentialStore _credentials;
    private readonly ObsWebSocketService _obs;
    private PermissionStore _permissions;
    private PluginRuntimeManager _runtime;
    private readonly CoreUpdateService _updates;
    private readonly DiagnosticsService _diagnostics;
    private readonly Dictionary<string, Dictionary<string, Control>> _fields = [];
    private List<TabItem> _tabs = [];
    private StackPanel? _catalogList;
    private TextBlock? _twitchStatus;
    private Button? _twitchAction;
    private TextBlock? _obsStatus;

    public MainWindow()
    {
        _twitch = new(_plugins.CredentialsDirectory);
        _logger = new(_plugins.LogsDirectory);
        _credentials = new(_plugins.CredentialsDirectory);
        _obs = new(_events, _logger);
        _permissions = new(_plugins.SettingsDirectory);
        _runtime = new(_events, new ForgeConnections(_obs, new TwitchConnectionView(_twitch)), _permissions, _logger, _plugins.SettingsDirectory);
        _updates = new(_plugins.CacheDirectory);
        _diagnostics = new(_plugins);
        _plugins.Profiles.Changed += async (_, changed) => await _events.PublishAsync(changed);
        InitializeComponent();
        Opened += async (_, _) => await RefreshAsync();
        Closed += async (_, _) => { await _events.PublishAsync(new ForgeStopping(DateTimeOffset.UtcNow)); await _runtime.DisposeAsync(); await _obs.DisposeAsync(); };
    }

    private async Task RefreshAsync()
    {
        var selectedIndex = MainTabs.SelectedIndex;
        MainTabs.SelectedIndex = -1;
        MainTabs.ItemsSource = null;
        _tabs = [];
        _fields.Clear();
        var installed = _plugins.Discover();
        AddHomeTab(installed);
        AddConnectionsTab();
        AddCatalogTab();
        foreach (var plugin in installed) AddPluginTab(plugin);
        AddSettingsTab();
        MainTabs.ItemsSource = null;
        MainTabs.ItemsSource = _tabs;
        MainTabs.SelectedIndex = Math.Clamp(selectedIndex, 0, _tabs.Count - 1);
        StatusText.Text = $"{installed.Count} plugin{(installed.Count == 1 ? "" : "s")} installed";
        await _runtime.StartAsync(installed.Where(x => _plugins.IsEnabled(x.Manifest.Id)));
        await _events.PublishAsync(new ForgeStarted(DateTimeOffset.UtcNow));
        await CheckCatalogAsync(installed);
    }

    private void AddHomeTab(IReadOnlyList<InstalledPlugin> installed)
    {
        var panel = NewPanel();
        panel.Children.Add(Heading("Your streaming workshop", 28));
        panel.Children.Add(Secondary("Forge Core stays lean. Install only the tools your setup needs."));
        panel.Children.Add(Card(new TextBlock { Text = $"{installed.Count} installed plugin{(installed.Count == 1 ? "" : "s")}\nForge Plugin API 1", FontSize = 18, LineHeight = 28 }));
        panel.Children.Add(Heading("Installed tools", 20, new(0, 24, 0, 10)));
        if (installed.Count == 0) panel.Children.Add(Secondary("No plugins installed yet. Visit the Plugin Library to add one."));
        foreach (var plugin in installed)
            panel.Children.Add(Card(new TextBlock { Text = $"{plugin.Manifest.Name}  ·  v{plugin.Manifest.Version}\n{plugin.Manifest.Description}", TextWrapping = TextWrapping.Wrap, LineHeight = 22 }));
        AddTab("Home", panel);
    }

    private void AddConnectionsTab()
    {
        var panel = NewPanel();
        panel.Children.Add(Heading("Connections", 28));
        panel.Children.Add(Secondary("Connect once in Forge Core. Plugins request permission to use shared services rather than collecting separate credentials."));
        var saved = _plugins.LoadSettings("forge.core.connections");

        var obs = new StackPanel();
        var obsHost = Field(obs, "Host", ReadString(saved, "obsHost", "127.0.0.1"));
        var obsPort = Field(obs, "WebSocket port", ReadString(saved, "obsPort", "4455"));
        obs.Children.Add(new TextBlock { Text = "WebSocket password" });
        var obsPassword = new TextBox { PasswordChar = '●', PlaceholderText = "Enter the password configured in OBS", Margin = new(0, 5, 0, 12) };
        obsPassword.Text = _credentials.Load("obs.websocket.password") ?? "";
        obs.Children.Add(obsPassword);
        obs.Children.Add(new TextBlock { Text = "The password is a credential and is never written to the portable settings file.", Foreground = Brushes.LightGray, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new(0, -6, 0, 10) });
        var obsAuto = new CheckBox { Content = "Connect automatically when Forge starts", IsChecked = ReadBool(saved, "obsAutoConnect", true), Margin = new(0, 7, 0, 12) };
        obs.Children.Add(obsAuto);
        var rememberObs = new CheckBox { Content = _credentials.CanPersist ? "Remember password on this device" : "Password persistence is unavailable on this operating system", IsChecked = _credentials.CanPersist && !string.IsNullOrEmpty(obsPassword.Text), IsEnabled = _credentials.CanPersist, Margin = new(0, 0, 0, 12) };
        obs.Children.Add(rememberObs);
        var obsActions = new StackPanel { Orientation = Orientation.Horizontal };
        var saveObs = Button("Save OBS settings");
        saveObs.Click += (_, _) =>
        {
            var current = _plugins.LoadSettings("forge.core.connections");
            SaveConnections(obsHost.Text ?? "127.0.0.1", obsPort.Text ?? "4455", obsAuto.IsChecked ?? false, ReadString(current, "twitchChannel", ""));
            if (rememberObs.IsChecked == true && !string.IsNullOrEmpty(obsPassword.Text)) _credentials.Save("obs.websocket.password", obsPassword.Text);
            else _credentials.Delete("obs.websocket.password");
            StatusText.Text = "OBS settings saved";
        };
        var connectObs = Button("Connect");
        _obsStatus = Status("Not connected");
        async Task ToggleObsAsync(bool showError)
        {
            try
            {
                if (_obs.IsConnected) { await _obs.DisconnectAsync(); connectObs.Content = "Connect"; _obsStatus.Text = "Not connected"; _obsStatus.Foreground = Brushes.Goldenrod; return; }
                connectObs.IsEnabled = false; _obsStatus.Text = "Connecting…";
                if (!int.TryParse(obsPort.Text, out var port)) throw new InvalidOperationException("OBS WebSocket port must be a number.");
                await _obs.ConnectAsync(obsHost.Text ?? "127.0.0.1", port, obsPassword.Text);
                connectObs.Content = "Disconnect"; _obsStatus.Text = "Connected"; _obsStatus.Foreground = Brushes.LightGreen;
            }
            catch (Exception ex) { _obsStatus.Text = "Connection failed"; _obsStatus.Foreground = Brushes.OrangeRed; if (showError) await ShowNoticeAsync("OBS connection failed", ex.Message); }
            finally { connectObs.IsEnabled = true; }
        }
        connectObs.Click += async (_, _) => await ToggleObsAsync(true);
        obsActions.Children.Add(saveObs); obsActions.Children.Add(connectObs); obsActions.Children.Add(_obsStatus); obs.Children.Add(obsActions);
        panel.Children.Add(ConnectionCard("OBS Studio", "Shared scenes, sources, streaming state, and events through OBS WebSocket.", obs));

        var twitch = new StackPanel();
        var channel = Field(twitch, "Channel name", ReadString(saved, "twitchChannel", ""));
        var twitchActions = new StackPanel { Orientation = Orientation.Horizontal };
        var saveTwitch = Button("Save channel");
        saveTwitch.Click += (_, _) => { var current = _plugins.LoadSettings("forge.core.connections"); SaveConnections(ReadString(current, "obsHost", "127.0.0.1"), ReadString(current, "obsPort", "4455"), ReadBool(current, "obsAutoConnect", true), channel.Text ?? ""); StatusText.Text = "Twitch channel saved"; };
        _twitchAction = Button("Sign in with Twitch");
        _twitchAction.Click += async (_, _) => await ConnectTwitchAsync();
        _twitchStatus = Status("Checking account…");
        twitchActions.Children.Add(saveTwitch); twitchActions.Children.Add(_twitchAction); twitchActions.Children.Add(_twitchStatus); twitch.Children.Add(twitchActions);
        panel.Children.Add(ConnectionCard("Twitch", "One Forge sign-in for chat, channel events, moderation, alerts, and plugin permissions.", twitch));
        panel.Children.Add(Heading("Connection permissions", 18, new(0, 22, 0, 4)));
        panel.Children.Add(Secondary("Plugins will declare scopes such as obs.scenes.read or twitch.chat.write. Forge will ask before exposing a shared connection."));
        AddTab("Connections", panel);
        _ = RestoreTwitchAsync();
        if (obsAuto.IsChecked == true && !string.IsNullOrEmpty(obsPassword.Text)) _ = ToggleObsAsync(false);
    }

    private async Task RestoreTwitchAsync()
    {
        try
        {
            var identity = await _twitch.RestoreAsync();
            SetTwitchIdentity(identity);
        }
        catch (Exception ex)
        {
            if (_twitchStatus is not null) { _twitchStatus.Text = $"Connection check failed: {ex.Message}"; _twitchStatus.Foreground = Brushes.OrangeRed; }
        }
    }

    private async Task ConnectTwitchAsync()
    {
        if (_twitchAction?.Tag is TwitchIdentity)
        {
            await _twitch.SignOutAsync();
            SetTwitchIdentity(null);
            StatusText.Text = "Signed out of Twitch";
            return;
        }

        using var cancellation = new CancellationTokenSource();
        Window? dialog = null;
        try
        {
            _twitchAction!.IsEnabled = false;
            _twitchStatus!.Text = "Requesting activation code…";
            var authorization = await _twitch.BeginAsync(cancellation.Token);
            var code = new TextBlock { Text = authorization.UserCode, FontSize = 32, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Brush.Parse("#F06432"), Margin = new(0, 10, 0, 10) };
            var progress = new TextBlock { Text = "Waiting for authorization…", Foreground = Brushes.LightGray, HorizontalAlignment = HorizontalAlignment.Center };
            var open = Button("Open Twitch activation page");
            open.HorizontalAlignment = HorizontalAlignment.Center;
            open.Click += async (_, _) => await Launcher.LaunchUriAsync(authorization.VerificationUri);
            var cancel = Button("Cancel"); cancel.HorizontalAlignment = HorizontalAlignment.Center;
            dialog = new Window { Title = "Connect Twitch", Width = 500, SizeToContent = SizeToContent.Height, CanResize = false, Content = new StackPanel { Margin = new(24), Children = { Heading("Connect Forge to Twitch", 24), Secondary("Open Twitch, enter this code, and approve the requested permissions. Forge never sees your Twitch password."), code, open, progress, cancel } } };
            cancel.Click += (_, _) => { cancellation.Cancel(); dialog.Close(); };
            dialog.Closed += (_, _) => cancellation.Cancel();
            _ = Launcher.LaunchUriAsync(authorization.VerificationUri);
            var completion = _twitch.CompleteAsync(authorization, cancellation.Token);
            var showing = dialog.ShowDialog(this);
            var identity = await completion;
            cancellation.CancelAfter(Timeout.InfiniteTimeSpan);
            dialog.Close();
            await showing;
            SetTwitchIdentity(identity);
            StatusText.Text = $"Connected to Twitch as {identity.Login}";
        }
        catch (OperationCanceledException) { SetTwitchIdentity(null); }
        catch (Exception ex) { if (dialog?.IsVisible == true) dialog.Close(); SetTwitchIdentity(null); await ShowNoticeAsync("Twitch connection failed", ex.Message); }
        finally { if (_twitchAction is not null) _twitchAction.IsEnabled = true; }
    }

    private void SetTwitchIdentity(TwitchIdentity? identity)
    {
        if (_twitchStatus is null || _twitchAction is null) return;
        _twitchAction.Tag = identity;
        _twitchAction.Content = identity is null ? "Sign in with Twitch" : "Sign out";
        _twitchStatus.Text = identity is null ? "Signed out" : $"Connected as {identity.Login}";
        _twitchStatus.Foreground = identity is null ? Brushes.Goldenrod : Brushes.LightGreen;
    }

    private void SaveConnections(string host, string port, bool autoConnect, string channel) =>
        _plugins.SaveSettings("forge.core.connections", new Dictionary<string, object?> { ["obsHost"] = host, ["obsPort"] = port, ["obsAutoConnect"] = autoConnect, ["twitchChannel"] = channel });

    private void AddCatalogTab()
    {
        var panel = NewPanel();
        panel.Children.Add(Heading("Plugin Library", 26));
        panel.Children.Add(Secondary("Curated plugins appear here. Community packages remain installable without being endorsed."));
        _catalogList = new StackPanel { Margin = new(0, 20, 0, 0) };
        _catalogList.Children.Add(new TextBlock { Text = "Checking for available plugins and updates…" });
        panel.Children.Add(_catalogList);
        AddTab("Plugin Library", panel);
    }

    private async Task CheckCatalogAsync(IReadOnlyList<InstalledPlugin> installed)
    {
        if (_catalogList is null) return;
        _catalogList.Children.Clear();
        try
        {
            var catalog = await _plugins.LoadCatalogAsync(Path.Combine(AppContext.BaseDirectory, "catalog.json"));
            if (catalog.Plugins.Count == 0) { _catalogList.Children.Add(Secondary("No remote catalog has been configured yet. The bundled sample demonstrates the cross-platform plugin UI system.")); return; }
            foreach (var item in catalog.Plugins)
            {
                var current = installed.FirstOrDefault(x => x.Manifest.Id == item.Id);
                var action = !item.Available ? "Coming soon" : current is null ? "Install" : current.Manifest.Version == item.Version ? "Installed" : "Update";
                var row = new Grid { ColumnDefinitions = new("*,Auto"), Background = PanelBrush, Margin = new(0, 0, 0, 10) };
                row.Children.Add(new TextBlock { Text = $"{item.Name}  {item.Version}\n{item.Description}", Margin = new(14), TextWrapping = TextWrapping.Wrap });
                var button = Button(action); button.IsEnabled = action is not "Installed" and not "Coming soon"; button.Tag = item; button.SetValue(Grid.ColumnProperty, 1); button.Click += Install_Click; row.Children.Add(button);
                _catalogList.Children.Add(row);
            }
        }
        catch (Exception ex) { _catalogList.Children.Add(new TextBlock { Text = $"Catalog check failed: {ex.Message}", Foreground = Brushes.OrangeRed, TextWrapping = TextWrapping.Wrap }); }
    }

    private async void Install_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CatalogPlugin plugin } button) return;
        var permissionText = plugin.Id + " requests installation from the curated catalog."
            + (plugin.Permissions.Length == 0 ? "\n\nNo shared-service permissions are declared." : "\n\nRequested permissions:\n" + string.Join("\n", plugin.Permissions.Select(x => "• " + x)))
            + "\n\nContinue?";
        if (!await ConfirmAsync("Install plugin", permissionText, "Allow & Install")) return;
        button.IsEnabled = false; button.Content = "Installing…";
        try
        {
            await _plugins.InstallAsync(plugin);
            _permissions.Set(plugin.Id, plugin.Permissions);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            await _logger.WriteAsync("ERROR", "forge.core.plugins", $"Installation of {plugin.Id} failed.", ex);
            await ShowNoticeAsync("Plugin installation failed", ex.Message);
            button.IsEnabled = true;
            button.Content = "Retry";
        }
    }

    private void AddPluginTab(InstalledPlugin plugin)
    {
        var panel = NewPanel();
        panel.Children.Add(Heading(plugin.Manifest.Name, 26));
        panel.Children.Add(Secondary($"{plugin.Manifest.Description}\n{plugin.Manifest.Author} · v{plugin.Manifest.Version}"));
        var management = new StackPanel { Orientation = Orientation.Horizontal };
        var enabled = new CheckBox { Content = "Enabled", IsChecked = _plugins.IsEnabled(plugin.Manifest.Id), VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 12, 0) };
        enabled.IsCheckedChanged += async (_, _) => { _plugins.SetEnabled(plugin.Manifest.Id, enabled.IsChecked == true); await _runtime.StartAsync(_plugins.Discover().Where(x => _plugins.IsEnabled(x.Manifest.Id))); StatusText.Text = $"{plugin.Manifest.Name} {(enabled.IsChecked == true ? "enabled" : "disabled")}"; };
        var uninstall = Button("Uninstall");
        uninstall.Click += async (_, _) => { if (!await ConfirmAsync("Uninstall plugin", $"Remove {plugin.Manifest.Name}? Its profile settings will be kept.", "Remove")) return; await _runtime.StopAsync(); _plugins.Remove(plugin.Manifest.Id); await RefreshAsync(); };
        management.Children.Add(enabled); management.Children.Add(uninstall); panel.Children.Add(management);
        if (plugin.Manifest.Permissions.Length > 0)
        {
            panel.Children.Add(Heading("Permissions", 17, new(0, 8, 0, 4)));
            panel.Children.Add(Secondary(string.Join("\n", plugin.Manifest.Permissions.Select(x => "• " + x))));
            var granted = plugin.Manifest.Permissions.All(x => _permissions.Allows(plugin.Manifest.Id, x));
            var permissionButton = Button(granted ? "Permissions granted" : "Grant requested permissions"); permissionButton.IsEnabled = !granted; permissionButton.HorizontalAlignment = HorizontalAlignment.Left;
            permissionButton.Click += async (_, _) => { _permissions.Set(plugin.Manifest.Id, plugin.Manifest.Permissions); permissionButton.Content = "Permissions granted"; permissionButton.IsEnabled = false; await _runtime.StartAsync(_plugins.Discover()); StatusText.Text = $"Permissions granted to {plugin.Manifest.Name}"; };
            panel.Children.Add(permissionButton);
        }
        var settings = _plugins.LoadSettings(plugin.Manifest.Id);
        var fields = new Dictionary<string, Control>();
        foreach (var section in plugin.Ui.Sections)
        {
            panel.Children.Add(Heading(section.Title, 19, new(0, 14, 0, 8)));
            foreach (var spec in section.Controls) AddControl(panel, fields, settings, spec, plugin.Manifest.Id);
        }
        _fields[plugin.Manifest.Id] = fields;
        var save = Button("Save settings"); save.HorizontalAlignment = HorizontalAlignment.Left; save.Margin = new(0, 20, 0, 0); save.Click += (_, _) => SavePlugin(plugin.Manifest.Id); panel.Children.Add(save);
        AddTab(plugin.Manifest.Name, panel);
    }

    private void AddSettingsTab()
    {
        var panel = NewPanel(); panel.Children.Add(Heading("Forge settings", 26));
        panel.Children.Add(Secondary("Forge is always portable. Plugins, settings, profiles, logs, cache, and encrypted credential records stay inside this Forge folder."));
        panel.Children.Add(Heading("Portable data folder", 18)); panel.Children.Add(Secondary(_plugins.DataDirectory));
        var open = Button("Open Forge data folder"); open.HorizontalAlignment = HorizontalAlignment.Left;
        open.Click += async (_, _) => await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(_plugins.DataDirectory)); panel.Children.Add(open);
        panel.Children.Add(Secondary("Copy the Forge folder to move your setup. Authenticated services may require reconnection because credential encryption is device-bound. Delete the folder to remove everything."));
        panel.Children.Add(Heading("Profiles", 18, new(0, 22, 0, 4)));
        panel.Children.Add(Secondary("Profiles keep separate plugin settings and permission grants for different channels or streaming setups."));
        var profileItems = _plugins.Profiles.List().Select(x => new ComboBoxItem { Content = x.Name, Tag = x.Id }).ToList();
        var profileSelect = new ComboBox { ItemsSource = profileItems, SelectedItem = profileItems.FirstOrDefault(x => Equals(x.Tag, _plugins.Profiles.ActiveProfileId)), MinWidth = 260 };
        panel.Children.Add(profileSelect);
        var profileActions = new StackPanel { Orientation = Orientation.Horizontal };
        var activate = Button("Activate profile");
        activate.Click += async (_, _) =>
        {
            if (profileSelect.SelectedItem is not ComboBoxItem { Tag: string id } || id == _plugins.Profiles.ActiveProfileId) return;
            await _runtime.DisposeAsync(); _plugins.Profiles.Activate(id);
            _permissions = new(_plugins.SettingsDirectory); _runtime = new(_events, new ForgeConnections(_obs, new TwitchConnectionView(_twitch)), _permissions, _logger, _plugins.SettingsDirectory);
            await RefreshAsync(); StatusText.Text = "Profile activated";
        };
        var create = Button("Create profile");
        create.Click += async (_, _) =>
        {
            var name = await PromptAsync("Create profile", "Profile name"); if (string.IsNullOrWhiteSpace(name)) return;
            var profile = _plugins.Profiles.Create(name); profileItems.Add(new ComboBoxItem { Content = profile.Name, Tag = profile.Id }); profileSelect.ItemsSource = null; profileSelect.ItemsSource = profileItems; profileSelect.SelectedItem = profileItems.Last();
        };
        profileActions.Children.Add(activate); profileActions.Children.Add(create); panel.Children.Add(profileActions);
        panel.Children.Add(Heading("Updates", 18, new(0, 22, 0, 4)));
        panel.Children.Add(Secondary("Forge checks the official GitHub Core release channel. Updates are downloaded only with your approval, SHA-256 verified, and applied without touching portable data."));
        var updateActions = new StackPanel { Orientation = Orientation.Horizontal };
        var refresh = Button("Reload plugins"); refresh.HorizontalAlignment = HorizontalAlignment.Left; refresh.Click += async (_, _) => await RefreshAsync(); updateActions.Children.Add(refresh);
        var checkCore = Button("Check Core update");
        checkCore.Click += async (_, _) =>
        {
            checkCore.IsEnabled = false;
            try
            {
                using var source = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "update-source.json")));
                var repository = source.RootElement.GetProperty("repository").GetString() ?? "";
                checkCore.Content = "Checking…";
                var release = await _updates.CheckAsync(repository);
                if (release is null) { await ShowNoticeAsync("Core updates", $"Forge Tools {CoreUpdateService.CurrentVersion.ToString(3)} is up to date."); return; }
                var notes = string.IsNullOrWhiteSpace(release.ReleaseNotes) ? "No release notes were provided." : release.ReleaseNotes;
                if (!await ConfirmAsync("Core update available", $"Forge Tools {release.Version} is available.\n\n{notes}\n\nDownload, verify, install, and restart now? Your portable data will be preserved and the current application files will be kept for rollback.", "Update & Restart")) return;
                checkCore.Content = "Downloading…";
                StatusText.Text = $"Downloading and verifying Forge Tools {release.Version}…";
                var package = await _updates.DownloadAndStageAsync(release);
                checkCore.Content = "Restarting…";
                StatusText.Text = "Update verified. Restarting Forge Tools…";
                _updates.ApplyWithUpdater(package);
                Close();
            }
            catch (Exception ex) { await ShowNoticeAsync("Update check failed", ex.Message); }
            finally { checkCore.IsEnabled = true; checkCore.Content = "Check Core update"; }
        };
        updateActions.Children.Add(checkCore); panel.Children.Add(updateActions);
        panel.Children.Add(Heading("Diagnostics", 18, new(0, 22, 0, 4)));
        panel.Children.Add(Secondary("Create a sanitized support bundle containing versions, plugin manifests, and recent logs. Settings and credentials are excluded."));
        var diagnostics = Button("Create diagnostics bundle"); diagnostics.HorizontalAlignment = HorizontalAlignment.Left;
        diagnostics.Click += async (_, _) => { try { var path = _diagnostics.CreateBundle(); await ShowNoticeAsync("Diagnostics ready", $"Saved locally to:\n{path}\n\nReview it before sharing."); } catch (Exception ex) { await ShowNoticeAsync("Diagnostics failed", ex.Message); } };
        panel.Children.Add(diagnostics);
        panel.Children.Add(Heading("About", 18, new(0, 22, 0, 4)));
        panel.Children.Add(Secondary($"Forge Tools {typeof(MainWindow).Assembly.GetName().Version}\nAvalonia 12 · .NET {Environment.Version}\nPlugin API 1–2"));
        AddTab("Settings", panel);
    }

    private void AddTab(string title, Control content) => _tabs.Add(new TabItem { Header = title, Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto } });
    private static StackPanel NewPanel() => new() { Margin = new(22), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
    private static TextBlock Heading(string text, double size, Thickness? margin = null) => new() { Text = text, FontSize = size, FontWeight = FontWeight.Bold, Margin = margin ?? new(0, 0, 0, 8) };
    private static TextBlock Secondary(string text) => new() { Text = text, Foreground = Brushes.LightGray, TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 14) };
    private static Border Card(Control content) => new() { Background = PanelBrush, Padding = new(14), Margin = new(0, 0, 0, 8), CornerRadius = new(5), Child = content };
    private static Border ConnectionCard(string title, string description, Control body) { var stack = new StackPanel(); stack.Children.Add(Heading(title, 21)); stack.Children.Add(Secondary(description)); stack.Children.Add(body); return Card(stack); }
    private static Button Button(string text) => new() { Content = text };
    private static TextBlock Status(string text) => new() { Text = text, Foreground = Brushes.Goldenrod, VerticalAlignment = VerticalAlignment.Center, Margin = new(12, 0, 0, 0) };
    private static TextBox Field(Panel panel, string label, string value) { panel.Children.Add(new TextBlock { Text = label }); var field = new TextBox { Text = value }; panel.Children.Add(field); return field; }

    private void AddControl(Panel panel, Dictionary<string, Control> fields, Dictionary<string, JsonElement> settings, UiControl spec, string pluginId)
    {
        panel.Children.Add(new TextBlock { Text = spec.Label, FontWeight = FontWeight.Medium });
        Control field;
        if (spec.Type.Equals("process-category-mappings", StringComparison.OrdinalIgnoreCase))
        {
            settings.TryGetValue(spec.Key, out var saved);
            string? optionsSource = null;
            if (!string.IsNullOrWhiteSpace(spec.OptionsSource))
            {
                var pluginData = Path.GetFullPath(Path.Combine(_plugins.SettingsDirectory, "plugin-data", pluginId));
                var candidate = Path.GetFullPath(Path.Combine(pluginData, spec.OptionsSource));
                if (candidate.StartsWith(pluginData + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) optionsSource = candidate;
            }
            var editor = new ProcessCategoryMappingEditor(_twitch, saved.ValueKind == JsonValueKind.Undefined ? null : saved, optionsSource, message => StatusText.Text = message);
            editor.Changed += (_, _) => SavePlugin(pluginId);
            field = editor;
        }
        else if (spec.Type.Equals("toggle", StringComparison.OrdinalIgnoreCase)) field = new CheckBox { Content = spec.Description ?? "Enabled", IsChecked = ReadBool(settings, spec.Key, spec.Default), Margin = new(0, 7, 0, 14) };
        else if (spec.Type.Equals("select", StringComparison.OrdinalIgnoreCase))
        {
            var selected = ReadString(settings, spec.Key, spec.Default);
            var combo = new ComboBox();
            var items = spec.Options.Select(x => new ComboBoxItem { Content = x.Label, Tag = x.Value }).ToList(); combo.ItemsSource = items; combo.SelectedItem = items.FirstOrDefault(x => Equals(x.Tag, selected)); field = combo;
        }
        else if (spec.Type.Equals("multiline", StringComparison.OrdinalIgnoreCase)) field = new TextBox { Text = ReadString(settings, spec.Key, spec.Default), AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, MinHeight = 140 };
        else field = new TextBox { Text = ReadString(settings, spec.Key, spec.Default) };
        fields[spec.Key] = field; panel.Children.Add(field);
    }

    private void SavePlugin(string pluginId)
    {
        var values = _fields[pluginId].ToDictionary(pair => pair.Key, pair => pair.Value switch { TextBox text => (object?)text.Text, CheckBox check => check.IsChecked ?? false, ComboBox { SelectedItem: ComboBoxItem item } => item.Tag, ProcessCategoryMappingEditor editor => editor.Mappings, _ => null });
        _plugins.SaveSettings(pluginId, values); StatusText.Text = "Settings saved";
    }

    private async Task ShowNoticeAsync(string title, string message)
    {
        var close = Button("OK");
        var dialog = new Window { Title = title, Width = 480, SizeToContent = SizeToContent.Height, CanResize = false, Content = new StackPanel { Margin = new(22), Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 16) }, close } } };
        close.Click += (_, _) => dialog.Close(); await dialog.ShowDialog(this);
    }

    private async Task<string?> PromptAsync(string title, string label)
    {
        var input = new TextBox(); var save = Button("Create"); var cancel = Button("Cancel"); string? result = null;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal }; buttons.Children.Add(save); buttons.Children.Add(cancel);
        var dialog = new Window { Title = title, Width = 420, SizeToContent = SizeToContent.Height, CanResize = false, Content = new StackPanel { Margin = new(22), Children = { new TextBlock { Text = label }, input, buttons } } };
        save.Click += (_, _) => { result = input.Text; dialog.Close(); }; cancel.Click += (_, _) => dialog.Close(); await dialog.ShowDialog(this); return result;
    }

    private async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var yes = Button(confirmText); var no = Button("Cancel"); var result = false;
        var actions = new StackPanel { Orientation = Orientation.Horizontal }; actions.Children.Add(yes); actions.Children.Add(no);
        var dialog = new Window { Title = title, Width = 440, SizeToContent = SizeToContent.Height, CanResize = false, Content = new StackPanel { Margin = new(22), Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 14) }, actions } } };
        yes.Click += (_, _) => { result = true; dialog.Close(); }; no.Click += (_, _) => dialog.Close(); await dialog.ShowDialog(this); return result;
    }

    private static string ReadString(Dictionary<string, JsonElement> settings, string key, object? fallback) => settings.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : fallback is JsonElement j && j.ValueKind == JsonValueKind.String ? j.GetString() ?? "" : fallback?.ToString() ?? "";
    private static bool ReadBool(Dictionary<string, JsonElement> settings, string key, object? fallback) => settings.TryGetValue(key, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback is JsonElement j && j.ValueKind is JsonValueKind.True or JsonValueKind.False ? j.GetBoolean() : fallback is bool b && b;
}
