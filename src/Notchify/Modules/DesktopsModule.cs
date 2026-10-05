using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Notchify.Core;
using Notchify.Services;
using static Notchify.Views.ViewKit;

namespace Notchify.Modules;

/// <summary>Desktops: every virtual desktop and the apps and windows open on it. Click a window to jump to it.</summary>
public sealed class DesktopsModule : NotchModule
{
    public override string Id => "desktops";
    public override string Title => "Desktops";
    public override string Glyph => Glyphs.TaskView;
    public override string Description => "See the apps and windows open on each virtual desktop, and jump to any of them.";

    public override FrameworkElement CreateView()
    {
        var list = Notch.Windows;
        var summary = Text("", "Caption");
        summary.VerticalAlignment = VerticalAlignment.Center;
        var group = Switch("Group by app", SettingsStore.Current.Spaces.GroupByApp, v =>
        {
            SettingsStore.Current.Spaces.GroupByApp = v;
            SettingsStore.Save();
        });
        group.Margin = new Thickness(0, 0, 8, 0);
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                group,
                Chip("New desktop", (_, _) => Notch.Spaces.New(), Glyphs.Add),
                Chip("Task view", (_, _) => { Notch.Shell.Collapse(); Notch.Spaces.TaskView(); }, Glyphs.TaskView),
            },
        };
        foreach (Button b in buttons.Children.OfType<Button>()) b.Margin = new Thickness(6, 0, 0, 0);
        DockPanel.SetDock(buttons, Dock.Right);
        top.Children.Add(buttons);
        top.Children.Add(summary);

        var sections = new StackPanel();
        void Render()
        {
            var desktops = list.Desktops;
            summary.Text = $"{desktops.Count} desktop{(desktops.Count == 1 ? "" : "s")} · {list.WindowCount} window{(list.WindowCount == 1 ? "" : "s")}";
            sections.Children.Clear();
            foreach (var d in desktops) sections.Children.Add(Section(d, SettingsStore.Current.Spaces.GroupByApp));
        }

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(Scroll(sections));

        void OnChanged() => Ui.Post(Render);
        void OnSettings() => Ui.Post(Render);
        root.IsVisibleChanged += (_, _) =>
        {
            if (root.IsVisible) { list.Changed += OnChanged; SettingsStore.Changed += OnSettings; list.Acquire(); Render(); }
            else { list.Changed -= OnChanged; SettingsStore.Changed -= OnSettings; list.Release(); }
        };
        return root;
    }

    private static FrameworkElement Section(DesktopWindows d, bool groupByApp)
    {
        var name = Text(d.Name, "Title", 13);
        name.VerticalAlignment = VerticalAlignment.Center;
        var head = new DockPanel { Margin = new Thickness(2, 0, 0, 6) };
        if (!d.IsCurrent)
        {
            var go = Chip("Switch here", (_, _) => { Notch.Shell.Collapse(); Notch.Spaces.SwitchTo(d.Index); }, Glyphs.Right);
            DockPanel.SetDock(go, Dock.Right);
            head.Children.Add(go);
        }
        var title = new StackPanel { Orientation = Orientation.Horizontal, Children = { name } };
        if (d.IsCurrent)
        {
            var badge = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "You're here", FontSize = 10, Foreground = Brushes.Black },
            };
            badge.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            title.Children.Add(badge);
        }
        var count = Text($"  ·  {d.Windows.Count} window{(d.Windows.Count == 1 ? "" : "s")}", "Caption");
        count.VerticalAlignment = VerticalAlignment.Center;
        title.Children.Add(count);
        head.Children.Add(title);

        UIElement body;
        if (d.Windows.Count == 0)
        {
            var none = Text("Nothing open", "Caption");
            none.Margin = new Thickness(4, 0, 0, 0);
            body = none;
        }
        else
        {
            var grid = new UniformGrid { Columns = 2 };
            if (groupByApp)
                foreach (var app in d.Windows.GroupBy(w => w.ExePath ?? w.App))
                    grid.Children.Add(AppRow(app.ToList(), d));
            else
                foreach (var w in d.Windows) grid.Children.Add(WindowRow(w, d));
            body = grid;
        }
        return Card(new StackPanel { Children = { head, body } }, new Thickness(0, 0, 0, 8));
    }

    private static Button Row(ImageSource? icon, string title, string subtitle, string tip)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        var t = Text(title, "Body", 12);
        t.TextTrimming = TextTrimming.CharacterEllipsis;
        var s = Text(subtitle, "Caption");
        s.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(t);
        text.Children.Add(s);
        var content = Columns((new Image { Source = icon, Width = 20, Height = 20 }, Px(22)), (text, Star()));
        return new Button
        {
            Style = S("ChipButton"),
            Content = content,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(0, 0, 6, 6),
            Height = double.NaN,
            ToolTip = tip,
        };
    }

    internal static Button WindowRow(OpenWindow w, DesktopWindows d)
    {
        var row = Row(w.Icon, w.Title, w.Minimized ? $"{w.App} · minimised" : w.App, $"{w.Title}\n{w.App}");
        row.Click += (_, _) => WindowListService.Activate(w);
        row.ContextMenu = WindowMenu(w, d);
        return row;
    }

    private static Button AppRow(List<OpenWindow> windows, DesktopWindows d)
    {
        var first = windows[0];
        var sub = windows.Count == 1 ? first.Title : $"{windows.Count} windows";
        var row = Row(first.Icon, windows.Count == 1 ? first.App : $"{first.App}  ×{windows.Count}", sub, string.Join("\n", windows.Select(w => w.Title)));
        row.Click += (_, _) => WindowListService.Activate(first);
        var menu = new ContextMenu();
        foreach (var w in windows)
        {
            var item = new MenuItem { Header = w.Title };
            FillMenu(item, w, d);
            menu.Items.Add(item);
        }
        row.ContextMenu = menu;
        return row;
    }

    internal static ContextMenu WindowMenu(OpenWindow w, DesktopWindows d)
    {
        var menu = new ContextMenu();
        FillMenu(menu, w, d);
        return menu;
    }

    private static void FillMenu(ItemsControl menu, OpenWindow w, DesktopWindows d)
    {
        void Item(string header, Action run)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => run();
            menu.Items.Add(mi);
        }
        Item(d.IsCurrent ? "Bring to front" : $"Go to it (switches to {d.Name})", () => WindowListService.Activate(w));
        if (!d.IsCurrent && WindowListService.CanMoveWindows) Item("Move to this desktop", () => Notch.Windows.MoveHere(w));
        if (!w.Minimized) Item("Minimise", () => Notch.Windows.Minimize(w));
        Item("Close window", () => Notch.Windows.Close(w));
    }

    public override IEnumerable<PaletteCommand> Commands => new[]
    {
        new PaletteCommand("Show open windows", Glyphs.TaskView, () => Notch.Shell.OpenTab(Id), null, "desktops windows apps virtual"),
    };
}
