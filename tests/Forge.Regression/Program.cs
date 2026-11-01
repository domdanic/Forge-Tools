using System.Collections;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Forge.ChatGames;
using Forge.PluginSdk;

internal static class Program
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static object Call(object o, string name, params object[] args) => o.GetType().GetMethod(name, Private)!.Invoke(o, args)!;
    static T Field<T>(object o, string name) => (T)o.GetType().GetField(name, Private)!.GetValue(o)!;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    static IList Items(ComboBox c) => ((IEnumerable<ComboBoxItem>)c.ItemsSource!).ToList();
    static object Property(object o, string name) => o.GetType().GetProperty(name)!.GetValue(o)!;
    static void ClickEdit(object editor)
    {
        var control = (Control)editor;
        var scroll = new ScrollViewer { Content = control };
        var window = new Window { Width = 1100, Height = 250, Content = scroll };
        window.Show();
        window.UpdateLayout();
        scroll.Offset = new Vector(0, scroll.Extent.Height);
        window.UpdateLayout();
        var before = scroll.Offset.Y;
        var button = control.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Edit"));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Check(before > 0 && scroll.Offset.Y < before, "Edit did not bring the form back into view");
        window.Close();
    }

    [STAThread]
    static void Main()
    {
        AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        Application.Current!.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        Decks();
        Hands();
        Editors();
        Console.WriteLine("PASS: deck depletion, dealer continuity, face-card labels, saved-rule edits and source choices.");
    }

    static void Decks()
    {
        var type = typeof(ChatGamesPlugin).Assembly.GetType("Forge.ChatGames.BlackjackDeck")!;
        var draw = type.GetMethod("Draw")!;
        for (var round = 0; round < 1000; round++)
        {
            var deck = Activator.CreateInstance(type)!;
            var counts = new int[14];
            for (var n = 0; n < 52; n++)
            {
                var rank = (int)draw.Invoke(deck, null)!;
                Check(rank is >= 1 and <= 13 && ++counts[rank] <= 4, "Rank dealt more than four times");
            }
            Check(counts.Skip(1).All(n => n == 4), "Deck must contain four of every rank");
            try { draw.Invoke(deck, null); throw new Exception("Empty deck silently refilled"); }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException) { }
        }
    }

    static string Label(int rank) => rank switch { 1 => "A", 11 => "J", 12 => "Q", 13 => "K", _ => rank.ToString() };
    static void Hands()
    {
        var plugin = new ChatGamesPlugin();
        typeof(ChatGamesPlugin).GetField("_context", Private)!.SetValue(plugin, new Context());
        var active = Field<IDictionary>(plugin, "_blackjack");
        var seen = new HashSet<int>();
        for (var round = 0; round < 2000; round++)
        {
            string id = round.ToString();
            string opening = (string)Call(plugin, "StartBlackjack", id, new[] { "!blackjack", "10" });
            if (!active.Contains(id)) continue; // Natural blackjack resolves immediately.
            var game = active[id]!;
            var dealer = (List<int>)Property(game, "Dealer");
            var original = dealer.ToArray();
            seen.Add(original[0]);
            Check(opening.Contains($"Dealer shows {Label(original[0])}."), "Opening card label mismatch");
            var result = (string)Call(plugin, "Hit", id);
            if (active.Contains(id)) result = (string)Call(plugin, "Stand", id);
            Check(dealer.Take(2).SequenceEqual(original), "Dealer's initial cards changed");
            Check(result.Contains("vs dealer " + string.Join(" ", dealer.Select(Label)) + " ("), "Final dealer display mismatch");
            var player = (List<int>)Property(game, "Player");
            Check(player.Concat(dealer).GroupBy(x => x).All(g => g.Count() <= 4), "Player and dealer did not share a deck");
            Check(!active.Contains(id), "Settled hand retained");
        }
        Check(seen.Count == 13, "All upcard labels were not exercised");
    }

    static void Editors()
    {
        var assembly = typeof(Forge.App.App).Assembly;
        var categoryType = assembly.GetType("Forge.App.ProcessCategoryMappingEditor")!;
        var saved = JsonSerializer.SerializeToElement(new[] { new { Process = "closed-game.exe", CategoryId = "1", CategoryName = "Game", ChatCommand = "!old", CommandAccess = "moderators" } });
        var editor = Activator.CreateInstance(categoryType, new object?[] { null, null, saved, null, (Action<string>)(_ => { }) })!;
        var mappings = (IList)Property(editor, "Mappings");
        ClickEdit(editor);
        Check(Property(Field<ComboBox>(editor, "_process").SelectedItem!, "Tag").Equals("closed-game.exe"), "Closed app lost during edit");
        Check(Field<Button>(editor, "_save").Content!.Equals("Save changes"), "Edit mode not indicated");
        Call(editor, "RefreshProcesses");
        Check(Field<ComboBox>(editor, "_process").SelectedItem is not null, "Refresh lost saved app");
        Field<TextBox>(editor, "_command").Text = "!new";
        var changed = 0;
        EventHandler handler = (_, _) => changed++;
        categoryType.GetEvent("Changed")!.AddEventHandler(editor, handler);
        Call(editor, "SaveMapping");
        Check(mappings.Count == 1 && Property(mappings[0]!, "ChatCommand").Equals("!new") && changed == 1, "Edited rule did not replace and persist");

        var captureType = assembly.GetType("Forge.App.CaptureSwitchMappingEditor")!;
        saved = JsonSerializer.SerializeToElement(new[] { new { CategoryId = "1", CategoryName = "Game", TargetProcess = "closed-game.exe", TargetWindow = "title:class:closed-game.exe", VideoInput = "Old video", AudioInput = "Audio" } });
        var capture = Activator.CreateInstance(captureType, new object?[] { null, null, null, saved, null, (Action<string>)(_ => { }) })!;
        var video = Field<ComboBox>(capture, "_videoInputs");
        var alternative = new ComboBoxItem { Content = "New video", Tag = "New video" };
        video.ItemsSource = new[] { alternative };
        mappings = (IList)Property(capture, "Mappings");
        ClickEdit(capture);
        Check(Items(video).Contains(alternative) && Items(video).Count == 3, "Edit discarded OBS choices or none option");
        Call(capture, "RefreshTargets");
        Check(Field<ComboBox>(capture, "_targets").SelectedItem is not null, "Saved capture target lost");
        video.SelectedItem = alternative;
        changed = 0;
        captureType.GetEvent("Changed")!.AddEventHandler(capture, handler);
        Call(capture, "SaveMapping");
        Check(mappings.Count == 1 && Property(mappings[0]!, "VideoInput").Equals("New video") && changed == 1, "Capture changes not saved");
        Check(Property(mappings[0]!, "TargetWindow").Equals("title:class:closed-game.exe"), "Saved window changed");
    }

    sealed class Context : IForgeContext
    {
        public string PluginId => "test";
        public string DataDirectory => "";
        public IReadOnlySet<string> GrantedPermissions => new HashSet<string>();
        public IPluginSettings Settings { get; } = new Settings();
        public IForgeEventBus Events => throw new NotSupportedException();
        public IForgeConnections Connections => throw new NotSupportedException();
        public IForgeAutomation Automation => throw new NotSupportedException();
        public IPluginSecrets Secrets => throw new NotSupportedException();
    }
    sealed class Settings : IPluginSettings
    {
        public T Get<T>(string key, T fallback) => fallback;
        public void Set<T>(string key, T value) => throw new NotSupportedException();
        public event EventHandler? Changed { add { } remove { } }
    }
}
