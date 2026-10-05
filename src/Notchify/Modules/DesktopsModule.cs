using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Notchify.Core;
using Notchify.Services;
using static Notchify.Views.ViewKit;

namespace Notchify.Modules;

/// <summary>
/// Desktops: every virtual desktop and the apps and windows open on it. Click a window to jump to it,
/// Ctrl+click to select several, and drag them onto another desktop (or right-click › Move to) to move them.
/// </summary>
public sealed class DesktopsModule : NotchModule
{
    private const string DragFormat = "notchx/windows";

    /// <summary>Windows picked with Ctrl+click, by handle so the selection survives refreshes.</summary>
    private readonly HashSet<IntPtr> _selected = new();
    private Action? _render;

    public override string Id => "desktops";
    public override string Title => "Desktops";
    public override string Glyph => Glyphs.TaskView;
    public override string Description => "See the apps and windows open on each virtual desktop, jump to any of them, and drag windows between desktops.";

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

        // "2 selected · Clear", shown while something is selected.
        var selectionText = Text("", "Caption");
        selectionText.VerticalAlignment = VerticalAlignment.Center;
        var clear = Chip("Clear selection", (_, _) => { _selected.Clear(); _render?.Invoke(); });
        clear.Margin = new Thickness(8, 0, 0, 0);
        var selectionBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 0, 6), Children = { selectionText, clear } };

        var sections = new StackPanel();
        void Render()
        {
            var desktops = list.Desktops;
            // Forget selected windows that have closed.
            var open = desktops.SelectMany(d => d.Windows).Select(w => w.Handle).ToHashSet();
            _selected.RemoveWhere(h => !open.Contains(h));

            summary.Text = $"{desktops.Count} desktop{(desktops.Count == 1 ? "" : "s")} · {list.WindowCount} window{(list.WindowCount == 1 ? "" : "s")}"
                + (WindowListService.CanMoveBetweenDesktops && desktops.Count > 1 ? "  ·  Ctrl+click to select, drag onto a desktop to move" : "");
            selectionBar.Visibility = _selected.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            selectionText.Text = $"{_selected.Count} selected — drag onto a desktop, or right-click › Move to";
            sections.Children.Clear();
            foreach (var d in desktops) sections.Children.Add(Section(d, desktops, SettingsStore.Current.Spaces.GroupByApp));
        }
        _render = Render;

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        DockPanel.SetDock(selectionBar, Dock.Top);
        root.Children.Add(selectionBar);
        root.Children.Add(Scroll(sections));

        void OnChanged() => Ui.Post(Render);
        void OnSettings() => Ui.Post(Render);
        root.IsVisibleChanged += (_, _) =>
        {
            if (root.IsVisible) { list.Changed += OnChanged; SettingsStore.Changed += OnSettings; list.Acquire(); Render(); }
            else { list.Changed -= OnChanged; SettingsStore.Changed -= OnSettings; list.Release(); _selected.Clear(); }
        };
        return root;
    }

    private FrameworkElement Section(DesktopWindows d, IReadOnlyList<DesktopWindows> all, bool groupByApp)
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
            var none = Text(WindowListService.CanMoveBetweenDesktops ? "Nothing open — drop windows here to move them" : "Nothing open", "Caption");
            none.Margin = new Thickness(4, 0, 0, 4);
            body = none;
        }
        else
        {
            var grid = new UniformGrid { Columns = 2 };
            if (groupByApp)
                foreach (var app in d.Windows.GroupBy(w => w.ExePath ?? w.App))
                    grid.Children.Add(AppRow(app.ToList(), d, all));
            else
                foreach (var w in d.Windows) grid.Children.Add(WindowRow(w, d, all));
            body = grid;
        }
        var card = Card(new StackPanel { Children = { head, body } }, new Thickness(0, 0, 0, 8));
        if (WindowListService.CanMoveBetweenDesktops) MakeDropTarget(card, d);
        return card;
    }

    // ---------------- rows ----------------

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
            Height = double.NaN,
            ToolTip = tip,
        };
    }

    /// <summary>A row with an accent outline when selected.</summary>
    private Border Selectable(Button row, IReadOnlyList<OpenWindow> windows)
    {
        var frame = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1.5), Margin = new Thickness(0, 0, 6, 6), Child = row };
        void Paint()
        {
            if (windows.All(w => _selected.Contains(w.Handle))) frame.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            else frame.BorderBrush = Brushes.Transparent;
        }
        Paint();

        // Click = jump to it; Ctrl+click = select / deselect.
        Point? pressedAt = null;
        row.PreviewMouseLeftButtonDown += (_, e) =>
        {
            pressedAt = e.GetPosition(row);
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                var on = !windows.All(w => _selected.Contains(w.Handle));
                foreach (var w in windows) { if (on) _selected.Add(w.Handle); else _selected.Remove(w.Handle); }
                Paint();
                _render?.Invoke();
                e.Handled = true;
            }
        };
        row.PreviewMouseLeftButtonUp += (_, _) => pressedAt = null;
        if (WindowListService.CanMoveBetweenDesktops)
            row.PreviewMouseMove += (_, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || pressedAt is not { } start) return;
                var d = e.GetPosition(row) - start;
                if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                pressedAt = null;
                // Dragging a selected row carries the whole selection along.
                var dragged = windows.Any(w => _selected.Contains(w.Handle))
                    ? Notch.Windows.Desktops.SelectMany(x => x.Windows).Where(w => _selected.Contains(w.Handle)).ToList()
                    : windows.ToList();
                DragDrop.DoDragDrop(row, new DataObject(DragFormat, dragged), DragDropEffects.Move);
            };
        return frame;
    }

    private void MakeDropTarget(Border card, DesktopWindows d)
    {
        card.AllowDrop = true;
        var normal = card.BorderBrush;
        var normalThickness = card.BorderThickness;
        bool Accepts(DragEventArgs e) =>
            e.Data.GetData(DragFormat) is List<OpenWindow> ws && ws.Any(w => w.DesktopId != d.Id);
        card.DragEnter += (_, e) =>
        {
            if (!Accepts(e)) return;
            card.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
            card.BorderThickness = new Thickness(2);
        };
        card.DragLeave += (_, _) => { card.BorderBrush = normal; card.BorderThickness = normalThickness; };
        card.DragOver += (_, e) => { e.Effects = Accepts(e) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
        card.Drop += (_, e) =>
        {
            card.BorderBrush = normal;
            card.BorderThickness = normalThickness;
            if (e.Data.GetData(DragFormat) is not List<OpenWindow> ws) return;
            e.Handled = true;
            _selected.Clear();
            // Let the drag finish before Explorer moves things around.
            Ui.Dispatcher.BeginInvoke(() => Notch.Windows.MoveTo(ws, d));
        };
    }

    private Border WindowRow(OpenWindow w, DesktopWindows d, IReadOnlyList<DesktopWindows> all)
    {
        var row = Row(w.Icon, w.Title, w.Minimized ? $"{w.App} · minimised" : w.App, $"{w.Title}\n{w.App}");
        row.Click += (_, _) => WindowListService.Activate(w);
        row.ContextMenu = new ContextMenu();
        row.ContextMenuOpening += (_, _) =>
        {
            row.ContextMenu.Items.Clear();
            FillMenu(row.ContextMenu, w, d, all, SelectionIncluding(w));
        };
        return Selectable(row, new[] { w });
    }

    private Border AppRow(List<OpenWindow> windows, DesktopWindows d, IReadOnlyList<DesktopWindows> all)
    {
        var first = windows[0];
        var sub = windows.Count == 1 ? first.Title : $"{windows.Count} windows";
        var row = Row(first.Icon, windows.Count == 1 ? first.App : $"{first.App}  ×{windows.Count}", sub, string.Join("\n", windows.Select(w => w.Title)));
        row.Click += (_, _) => WindowListService.Activate(first);
        var menu = new ContextMenu();
        if (WindowListService.CanMoveBetweenDesktops && all.Count > 1)
        {
            AddMoveTo(menu, windows, d, all, windows.Count == 1 ? "Move to" : $"Move all {windows.Count} to");
            menu.Items.Add(new Separator());
        }
        foreach (var w in windows)
        {
            var item = new MenuItem { Header = w.Title };
            FillMenu(item, w, d, all, new[] { w });
            menu.Items.Add(item);
        }
        row.ContextMenu = menu;
        return Selectable(row, windows);
    }

    /// <summary>The selection, if <paramref name="w"/> is part of it; otherwise just <paramref name="w"/>.</summary>
    private IReadOnlyList<OpenWindow> SelectionIncluding(OpenWindow w) =>
        _selected.Contains(w.Handle) && _selected.Count > 1
            ? Notch.Windows.Desktops.SelectMany(x => x.Windows).Where(x => _selected.Contains(x.Handle)).ToList()
            : new[] { w };

    // ---------------- menus ----------------

    /// <summary>Right-click menu for one window (also used by the Home card).</summary>
    internal static ContextMenu WindowMenu(OpenWindow w, DesktopWindows d)
    {
        var menu = new ContextMenu();
        FillMenu(menu, w, d, Notch.Windows.Desktops, new[] { w });
        return menu;
    }

    private static void FillMenu(ItemsControl menu, OpenWindow w, DesktopWindows d, IReadOnlyList<DesktopWindows> all, IReadOnlyList<OpenWindow> moving)
    {
        void Item(string header, Action run)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (_, _) => run();
            menu.Items.Add(mi);
        }
        Item(d.IsCurrent ? "Bring to front" : $"Go to it (switches to {d.Name})", () => WindowListService.Activate(w));
        if (WindowListService.CanMoveBetweenDesktops && all.Count > 1)
            AddMoveTo(menu, moving, d, all, moving.Count > 1 ? $"Move {moving.Count} selected windows to" : "Move to");
        else if (!d.IsCurrent && WindowListService.CanMoveWindows)
            Item("Move to this desktop", () => Notch.Windows.MoveHere(w));
        if (!w.Minimized) Item("Minimise", () => Notch.Windows.Minimize(w));
        Item("Close window", () => Notch.Windows.Close(w));
    }

    /// <summary>"Move to ▸" with every other desktop.</summary>
    private static void AddMoveTo(ItemsControl menu, IReadOnlyList<OpenWindow> windows, DesktopWindows from, IReadOnlyList<DesktopWindows> all, string header)
    {
        var moveTo = new MenuItem { Header = header };
        foreach (var target in all.Where(x => x.Id != from.Id))
        {
            var t = target;
            var item = new MenuItem { Header = t.IsCurrent ? $"{t.Name} (this desktop)" : t.Name };
            item.Click += (_, _) => Notch.Windows.MoveTo(windows, t);
            moveTo.Items.Add(item);
        }
        menu.Items.Add(moveTo);
    }

    public override IEnumerable<PaletteCommand> Commands => new[]
    {
        new PaletteCommand("Show open windows", Glyphs.TaskView, () => Notch.Shell.OpenTab(Id), null, "desktops windows apps virtual move"),
    };
}
