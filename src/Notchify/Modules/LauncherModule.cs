using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Notchify.Core;
using static Notchify.Views.ViewKit;

namespace Notchify.Modules;

public sealed class LauncherApp
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Args { get; set; } = "";
    [JsonIgnore] public ImageSource? Icon => Ui.FileIcon(Path);
}

/// <summary>App Launcher: pin go-to apps and open them with one tap. Add from a Start-menu picker or drop shortcuts in.</summary>
public sealed class LauncherModule : NotchModule
{
    public ObservableCollection<LauncherApp> Apps { get; } = new();

    public override string Id => "launcher";
    public override string Title => "Launcher";
    public override string Glyph => Glyphs.Apps;
    public override string Description => "Pin your go-to apps and open them with one tap — a dock away from the taskbar.";

    protected override void Start()
    {
        Apps.Clear();
        foreach (var a in JsonStore.Load<List<LauncherApp>>("launcher")) Apps.Add(a);
    }

    private void Save() => JsonStore.Save("launcher", Apps.ToList());

    public void Launch(LauncherApp app)
    {
        try
        {
            Process.Start(new ProcessStartInfo(app.Path, app.Args) { UseShellExecute = true });
            Notch.Shell.Collapse();
        }
        catch (Exception ex) { Notch.Hub.Notify(Glyphs.Warning, $"Couldn't open {app.Name}", ex.Message, Ui.Red); }
    }

    public void Add(string path)
    {
        if (Apps.Any(a => a.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
        Apps.Add(new LauncherApp { Name = System.IO.Path.GetFileNameWithoutExtension(path), Path = path });
        Save();
    }

    /// <summary>Start-menu shortcuts for the picker (per-user + all users).</summary>
    public static List<(string Name, string Path)> StartMenuApps()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        };
        return roots.Where(Directory.Exists)
            .SelectMany(r => Directory.EnumerateFiles(r, "*.lnk", SearchOption.AllDirectories))
            .Select(p => (Name: System.IO.Path.GetFileNameWithoutExtension(p), Path: p))
            .Where(x => !x.Name.Contains("uninstall", StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => x.Name).Select(g => g.First())
            .OrderBy(x => x.Name).ToList();
    }

    public override FrameworkElement CreateView()
    {
        var root = new Grid { AllowDrop = true };
        var picker = new Border { Visibility = Visibility.Collapsed, Padding = new Thickness(10), CornerRadius = new CornerRadius(12) };
        var tiles = new WrapPanel();
        var empty = Text("Pin apps with + or drop shortcuts here", "Caption");
        empty.HorizontalAlignment = HorizontalAlignment.Center;
        empty.VerticalAlignment = VerticalAlignment.Center;

        void Rebuild()
        {
            tiles.Children.Clear();
            empty.Visibility = Apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var app in Apps)
            {
                var a = app;
                var tile = new Button { Style = S("ChipButton"), Width = 82, Height = 78, Margin = new Thickness(0, 0, 6, 6), Background = Brushes.Transparent, ToolTip = a.Path };
                var sp = new StackPanel();
                sp.Children.Add(new Image { Source = a.Icon, Width = 32, Height = 32, Margin = new Thickness(0, 2, 0, 6) });
                var name = Text(a.Name, "Caption");
                name.HorizontalAlignment = HorizontalAlignment.Center;
                name.Foreground = (Brush)Application.Current.FindResource("TextBrush");
                sp.Children.Add(name);
                tile.Content = sp;
                tile.Click += (_, _) => Launch(a);
                var menu = new ContextMenu();
                void Item(string header, Action act) { var mi = new MenuItem { Header = header }; mi.Click += (_, _) => { act(); Save(); Rebuild(); }; menu.Items.Add(mi); }
                Item("Move left", () => { var i = Apps.IndexOf(a); if (i > 0) Apps.Move(i, i - 1); });
                Item("Move right", () => { var i = Apps.IndexOf(a); if (i < Apps.Count - 1) Apps.Move(i, i + 1); });
                Item("Run as administrator", () => { try { Process.Start(new ProcessStartInfo(a.Path) { UseShellExecute = true, Verb = "runas" }); } catch { } });
                Item("Remove", () => Apps.Remove(a));
                tile.ContextMenu = menu;
                // Drag a tile onto another to reorder. Only start once the mouse has really moved,
                // otherwise the drag swallows the click and the app never launches.
                Point? pressedAt = null;
                tile.PreviewMouseLeftButtonDown += (_, e) => pressedAt = e.GetPosition(tile);
                tile.PreviewMouseLeftButtonUp += (_, _) => pressedAt = null;
                tile.PreviewMouseMove += (_, e) =>
                {
                    if (e.LeftButton != MouseButtonState.Pressed || pressedAt is not { } start) return;
                    var d = e.GetPosition(tile) - start;
                    if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                    pressedAt = null;
                    DragDrop.DoDragDrop(tile, new DataObject("notchify/launcher", a), DragDropEffects.Move);
                };
                tile.AllowDrop = true;
                tile.Drop += (_, e) =>
                {
                    if (e.Data.GetData("notchify/launcher") is LauncherApp dragged && dragged != a)
                    {
                        Apps.Move(Apps.IndexOf(dragged), Apps.IndexOf(a));
                        Save(); Rebuild(); e.Handled = true;
                    }
                };
                tiles.Children.Add(tile);
            }
            var add = new Button { Style = S("ChipButton"), Width = 82, Height = 78, Content = Icon(Glyphs.Add, 20), ToolTip = "Add an app" };
            add.Click += (_, _) => picker.Visibility = Visibility.Visible;
            tiles.Children.Add(add);
        }

        // Picker overlay
        var search = new TextBox { Tag = "Search installed apps…" };
        var results = new ListBox { Margin = new Thickness(0, 8, 0, 0), DisplayMemberPath = "Name" };
        var all = new List<(string Name, string Path)>();
        var close = IconButton(Glyphs.Close, "Close", (_, _) => picker.Visibility = Visibility.Collapsed);
        var browse = Chip("Browse…", (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Apps|*.exe;*.lnk;*.bat;*.cmd;*.url|All files|*.*" };
            if (dlg.ShowDialog() == true) { Add(dlg.FileName); Rebuild(); picker.Visibility = Visibility.Collapsed; }
        });
        var top = new DockPanel();
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(browse, Dock.Right);
        top.Children.Add(close);
        top.Children.Add(browse);
        top.Children.Add(search);
        var pickerPanel = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        pickerPanel.Children.Add(top);
        pickerPanel.Children.Add(results);
        picker.Child = pickerPanel;
        picker.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
        picker.IsVisibleChanged += (_, _) =>
        {
            if (!picker.IsVisible) return;
            if (all.Count == 0) all = StartMenuApps();
            results.ItemsSource = all.Select(x => new { x.Name, x.Path }).ToList();
            search.Focus();
        };
        search.TextChanged += (_, _) =>
            results.ItemsSource = all.Where(x => x.Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase)).Select(x => new { x.Name, x.Path }).ToList();
        results.MouseDoubleClick += (_, _) => Pick();
        results.KeyDown += (_, e) => { if (e.Key == Key.Enter) Pick(); };
        search.KeyDown += (_, e) => { if (e.Key == Key.Enter) { results.SelectedIndex = 0; Pick(); } };
        void Pick()
        {
            if (results.SelectedItem == null) return;
            var path = (string)results.SelectedItem.GetType().GetProperty("Path")!.GetValue(results.SelectedItem)!;
            Add(path);
            Rebuild();
            picker.Visibility = Visibility.Collapsed;
        }

        root.Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            {
                foreach (var f in files) Add(f);
                Rebuild();
                e.Handled = true; // don't also drop onto the shelf
            }
        };

        root.Children.Add(Scroll(tiles));
        root.Children.Add(empty);
        root.Children.Add(picker);
        Rebuild();
        return root;
    }

    public override IEnumerable<PaletteCommand> Commands =>
        Apps.Select(a => new PaletteCommand($"Launch {a.Name}", Glyphs.Apps, () => Launch(a), null, "open app run"));
}
