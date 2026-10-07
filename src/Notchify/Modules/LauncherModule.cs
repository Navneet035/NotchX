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
    /// <summary>Optional user-chosen icon (image, .ico, or an exe/shortcut to borrow from).</summary>
    public string? CustomIcon { get; set; }
    [JsonIgnore] public ImageSource? Icon => Ui.ItemIcon(CustomIcon, Path);
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

    public void Add(string path, string? name = null, string? icon = null)
    {
        if (Apps.Any(a => a.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) return;
        if (name == null)
        {
            var trimmed = path.TrimEnd('\\', '/');
            // A folder keeps its whole name ("Project.v2"); a drive root has none, so use the path ("D:\").
            name = Directory.Exists(path) ? (System.IO.Path.GetFileName(trimmed) is { Length: > 0 } n ? n : path)
                : System.IO.Path.GetFileNameWithoutExtension(path);
        }
        Apps.Add(new LauncherApp { Name = name, Path = path, CustomIcon = icon });
        Save();
    }

    /// <summary>From the "type to add" box: apps (Store apps too) get their icon saved, since there's no file to read it from.</summary>
    public void Add(Services.ShortcutTarget t) =>
        Add(t.Path, t.Name, t.IsApp ? Services.ShortcutCatalog.SaveIcon(t) : null);

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
        var picker = new Views.ShortcutPicker();
        var tiles = new WrapPanel();
        var empty = Text("Pin apps and folders with +, or drop shortcuts here", "Caption");
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
                Item("Change icon…", () =>
                {
                    if (Ui.PickIcon() is not { } icon) return;
                    Ui.ForgetIcon(a.CustomIcon);
                    a.CustomIcon = icon;
                });
                if (a.CustomIcon != null) Item("Reset icon", () => { Ui.ForgetIcon(a.CustomIcon); a.CustomIcon = null; });
                Item("Remove", () => { Ui.ForgetIcon(a.CustomIcon); Apps.Remove(a); });
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
            var add = new Button { Style = S("ChipButton"), Width = 82, Height = 78, Content = Icon(Glyphs.Add, 20), ToolTip = "Add an app, folder or file" };
            add.Click += (_, _) => picker.Show();
            tiles.Children.Add(add);
        }

        // "Type to add": apps (Store apps too), folders like Downloads, drives, or a typed path.
        picker.Picked += t => { Add(t); Rebuild(); };

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
