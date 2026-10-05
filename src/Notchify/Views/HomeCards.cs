using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Notchify.Core;
using Notchify.Modules;
using Notchify.Services;
using static Notchify.Views.ViewKit;

namespace Notchify.Views;

/// <summary>
/// Home cards that are built in code. Each card only does work (timers, sampling, camera) while it's on screen.
/// The simpler cards (clock, Now Playing, volume, brightness, toggles, Bluetooth) live in HomeView.xaml.
/// </summary>
public static class HomeCards
{
    public static FrameworkElement? Create(string type) => type switch
    {
        "weather" => Weather(),
        "battery" => Battery(),
        "reminders" => Reminders(),
        "calendar" => Calendar(),
        "notes" => Notes(),
        "clipboard" => Clipboard(),
        "shelf" => Shelf(),
        "camera" => Camera(),
        "screentime" => ScreenTime(),
        "spaces" => Spaces(),
        "windows" => OpenWindows(),
        "world" => WorldClocks(),
        "timer" => Timer(),
        "system" => SystemStats(),
        "launcher" => Launcher(),
        _ => null,
    };

    // ---------------- building blocks ----------------

    private static Border CardOf(UIElement content, Thickness? padding = null) =>
        new() { Style = S("Card"), Padding = padding ?? new Thickness(12, 8, 10, 10), Child = content };

    /// <summary>"TITLE" on the left, small buttons on the right.</summary>
    private static DockPanel Header(string title, params UIElement[] buttons)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        foreach (var b in Enumerable.Reverse(buttons))
        {
            DockPanel.SetDock(b, Dock.Right);
            dock.Children.Add(b);
        }
        var t = Text(title.ToUpperInvariant(), "SectionHeader");
        t.VerticalAlignment = VerticalAlignment.Center;
        t.Margin = new Thickness(2, 0, 0, 0);
        dock.Children.Add(t);
        return dock;
    }

    private static Button Small(string glyph, string tip, Action run)
    {
        var b = IconButton(glyph, tip, (_, _) => run());
        b.Width = 26;
        b.Height = 26;
        b.FontSize = 12;
        return b;
    }

    /// <summary>Header on top, body filling the rest.</summary>
    private static DockPanel Layout(UIElement header, UIElement body, UIElement? top = null)
    {
        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        if (top != null)
        {
            DockPanel.SetDock(top, Dock.Top);
            dock.Children.Add(top);
        }
        dock.Children.Add(body);
        return dock;
    }

    private static TextBlock Faint(string text)
    {
        var t = Text(text, "Caption");
        t.TextWrapping = TextWrapping.Wrap;
        t.Margin = new Thickness(2, 4, 0, 0);
        return t;
    }

    private static TextBlock Bound(object source, string path, string style = "Body", double? size = null)
    {
        var t = Text("", style, size);
        t.SetBinding(TextBlock.TextProperty, new Binding(path) { Source = source, Mode = BindingMode.OneWay });
        return t;
    }

    /// <summary>Run <paramref name="onShow"/> while the card is visible and <paramref name="onHide"/> when it goes away.</summary>
    private static void WhileVisible(FrameworkElement el, Action onShow, Action? onHide = null)
    {
        el.IsVisibleChanged += (_, _) => { if (el.IsVisible) onShow(); else onHide?.Invoke(); };
    }

    private static ScrollViewer ScrollList(UIElement content) =>
        new() { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

    // ---------------- cards ----------------

    private static FrameworkElement Weather()
    {
        var w = Notch.Weather;
        var icon = Bound(w, nameof(WeatherService.Icon), "Body", 30);
        icon.FontFamily = new FontFamily("Segoe UI Emoji");
        icon.VerticalAlignment = VerticalAlignment.Center;
        var temp = Bound(w, nameof(WeatherService.Temperature), "Big", 28);
        var info = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            Children = { Bound(w, nameof(WeatherService.Condition), "Title", 13), Bound(w, nameof(WeatherService.Range), "Caption"), Bound(w, nameof(WeatherService.Place), "Caption") },
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { icon, temp, info } };
        var card = CardOf(row);
        card.MouseLeftButtonUp += (_, _) => _ = w.RefreshAsync();
        card.ToolTip = "Click to refresh · set your city in Settings › Weather";
        return card;
    }

    private static FrameworkElement Battery()
    {
        var b = Notch.Battery;
        var big = Text("", "Big", 26);
        var glyph = Icon("", 22);
        var state = Text("", "Caption");
        var devices = new StackPanel();
        void Render()
        {
            glyph.Text = b.HasBattery ? b.Glyph : Glyphs.Bolt;
            big.Text = b.HasBattery ? $"{b.Percent}%" : "AC";
            state.Text = !b.HasBattery ? "Plugged in · no battery" : b.Charging ? "Charging" : b.PluggedIn ? "Plugged in" : "On battery";
            devices.Children.Clear();
            foreach (var d in Notch.Bluetooth.Devices.Take(4))
            {
                var name = Text(d.Name, "Caption");
                var row = Columns((Icon(d.Glyph, 12), Px(20)), (name, Star()), (Text(d.Text, "Caption"), Auto), (Icon(d.BatteryGlyph, 13), Px(22)));
                row.Margin = new Thickness(0, 2, 0, 0);
                devices.Children.Add(row);
            }
        }
        b.PropertyChanged += (_, _) => Render();
        Notch.Bluetooth.Devices.CollectionChanged += (_, _) => Render();
        Render();
        var top = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { glyph, new StackPanel { Margin = new Thickness(8, 0, 0, 0), Children = { big, state } } },
        };
        glyph.VerticalAlignment = VerticalAlignment.Center;
        var stack = new StackPanel { Children = { top, devices } };
        devices.Margin = new Thickness(0, 6, 0, 0);
        var card = CardOf(ScrollList(stack));
        WhileVisible(card, () => _ = Notch.Bluetooth.RefreshDevicesAsync());
        return card;
    }

    private static FrameworkElement Reminders()
    {
        if (Notch.Modules.Get("calendar") is not CalendarModule cal || !cal.IsRunning)
            return CardOf(Layout(Header("Reminders"), Faint("Turn on Calendar in Settings › Features to use reminders.")));
        var input = new TextBox { Tag = "Remind me… “Call Sam at 5pm”" };
        input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            cal.AddReminder(input.Text);
            input.Clear();
            e.Handled = true;
        };
        var list = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        var empty = Faint("Nothing due. Try “Pay rent tomorrow 9am”.");
        void Render()
        {
            list.Children.Clear();
            var open = cal.Reminders.Where(r => !r.Done).Take(8).ToList();
            empty.Visibility = open.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var r in open)
            {
                var rem = r;
                var done = new CheckBox { Style = S("Switch"), ToolTip = "Done", VerticalAlignment = VerticalAlignment.Center };
                done.Click += (_, _) => { rem.Done = true; cal.Save(); Render(); };
                var text = new StackPanel { Children = { Text(r.Text, "Body"), Text(r.DueText, "Caption", 10) } };
                if (r.Overdue) ((TextBlock)text.Children[1]).Foreground = Ui.Red;
                list.Children.Add(Columns((text, Star()), (done, Auto)));
            }
        }
        cal.Reminders.CollectionChanged += (_, _) => Render();
        Render();
        var body = ScrollList(new StackPanel { Children = { empty, list } });
        var card = CardOf(Layout(Header("Reminders", Small(Glyphs.PopOut, "Open Calendar", () => Notch.Shell.OpenTab("calendar"))), body, input));
        WhileVisible(card, Render);
        return card;
    }

    private static FrameworkElement Calendar()
    {
        var c = Notch.Calendar;
        var list = new StackPanel();
        var empty = Faint("No upcoming events. Add iCal links in Settings › Calendar.");
        void Render()
        {
            list.Children.Clear();
            empty.Visibility = c.Upcoming.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var e in c.Upcoming.Take(6))
            {
                var day = Text(e.DayText, "Caption");
                day.Width = 60;
                var row = Columns((day, Auto), (new StackPanel { Children = { Text(e.Title, "Body"), Text(e.TimeText, "Caption", 10) } }, Star()));
                row.Margin = new Thickness(0, 0, 0, 4);
                list.Children.Add(row);
            }
        }
        c.Upcoming.CollectionChanged += (_, _) => Ui.Post(Render);
        Render();
        var header = Header("Calendar",
            Small(Glyphs.Refresh, "Refresh", () => _ = c.RefreshAsync()),
            Small(Glyphs.PopOut, "Open Calendar", () => Notch.Shell.OpenTab("calendar")));
        return CardOf(Layout(header, ScrollList(new StackPanel { Children = { empty, list } })));
    }

    private static FrameworkElement Notes()
    {
        if (Notch.Modules.Get("notes") is not NotesModule notes || !notes.IsRunning)
            return CardOf(Layout(Header("Notes"), Faint("Turn on Notes in Settings › Features.")));
        var input = new TextBox { Tag = "Jot something down…" };
        input.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            notes.Add(input.Text);
            input.Clear();
            e.Handled = true;
        };
        var list = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        void Render()
        {
            list.Children.Clear();
            foreach (var n in notes.Notes.Where(n => !n.Done).Take(8))
            {
                var note = n;
                var tick = Small(Glyphs.Check, "Done", () => { note.Done = true; notes.Save(); Render(); });
                var t = Text(n.Text, "Body");
                t.TextWrapping = TextWrapping.Wrap;
                t.VerticalAlignment = VerticalAlignment.Center;
                var row = Columns((t, Star()), (tick, Auto));
                row.Margin = new Thickness(0, 0, 0, 2);
                list.Children.Add(row);
            }
        }
        notes.Notes.CollectionChanged += (_, _) => Render();
        Render();
        var card = CardOf(Layout(Header("Notes", Small(Glyphs.PopOut, "Open Notes", () => Notch.Shell.OpenTab("notes"))), ScrollList(list), input));
        WhileVisible(card, Render);
        return card;
    }

    private static FrameworkElement Clipboard()
    {
        var clip = Notch.Clipboard;
        var list = new StackPanel();
        var dirty = true;
        void Render()
        {
            if (!dirty) return;
            dirty = false;
            list.Children.Clear();
            foreach (var item in clip.Items.Take(8))
            {
                var i = item;
                var b = new Button { Style = S("ChipButton"), Background = Brushes.Transparent, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(4, 3, 4, 3), Margin = new Thickness(0, 0, 0, 1), ToolTip = "Click to copy" };
                UIElement lead = item.Kind == ClipKind.Image && item.Thumbnail is { } th
                    ? new Image { Source = th, Width = 34, Height = 22, Stretch = Stretch.UniformToFill }
                    : Icon(item.KindGlyph, 12);
                var preview = Text(item.Preview, "Body", 11);
                b.Content = Columns((lead, Px(40)), (preview, Star()), (Text(item.Age, "Caption", 10), Auto));
                b.Click += (_, _) =>
                {
                    clip.Copy(i);
                    Notch.Hub.Notify(Glyphs.Copy, "Copied", null, Ui.Green, IslandPriority.Low, 1.2, "home-copy");
                };
                list.Children.Add(b);
            }
            if (list.Children.Count == 0) list.Children.Add(Faint("Copy something and it shows up here."));
        }
        var card = CardOf(Layout(Header("Clipboard", Small(Glyphs.PopOut, "Open Clipboard", () => Notch.Shell.OpenTab("clipboard"))), ScrollList(list)));
        clip.Items.CollectionChanged += (_, _) => { dirty = true; if (card.IsVisible) Render(); };
        WhileVisible(card, Render);
        return card;
    }

    private static FrameworkElement Shelf()
    {
        var shelf = Notch.Shelf;
        var tiles = new WrapPanel();
        void Render()
        {
            tiles.Children.Clear();
            foreach (var item in shelf.Items.Take(12))
            {
                var i = item;
                var name = Text(item.Name, "Caption", 10);
                name.HorizontalAlignment = HorizontalAlignment.Center;
                name.MaxWidth = 64;
                var tile = new StackPanel
                {
                    Width = 66, Margin = new Thickness(0, 0, 4, 4), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = item.Path + "\nDrag out · double-click to open",
                    Children = { new Image { Source = item.Thumbnail, Height = 34, Stretch = Stretch.Uniform, Margin = new Thickness(0, 2, 0, 2) }, name },
                };
                Point? pressed = null;
                tile.MouseLeftButtonDown += (_, e) =>
                {
                    if (e.ClickCount == 2) { Ui.OpenUrl(i.Path); e.Handled = true; return; }
                    pressed = e.GetPosition(tile);
                };
                tile.MouseLeftButtonUp += (_, _) => pressed = null;
                tile.MouseMove += (_, e) =>
                {
                    if (e.LeftButton != MouseButtonState.Pressed || pressed is not { } start) return;
                    var d = e.GetPosition(tile) - start;
                    if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                    pressed = null;
                    if (i.Exists) DragDrop.DoDragDrop(tile, new DataObject(DataFormats.FileDrop, new[] { i.Path }), DragDropEffects.Copy | DragDropEffects.Move);
                };
                tiles.Children.Add(tile);
            }
            if (tiles.Children.Count == 0) tiles.Children.Add(Faint("Drop files here — they stay until you drag them out."));
        }
        shelf.Items.CollectionChanged += (_, _) => Render();
        Render();
        var card = CardOf(Layout(Header("Shelf", Small(Glyphs.PopOut, "Open Shelf", () => Notch.Shell.OpenTab("shelf"))), ScrollList(tiles)));
        card.AllowDrop = true;
        card.Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) { shelf.Add(files); e.Handled = true; }
        };
        return card;
    }

    private static FrameworkElement Camera()
    {
        // The camera only switches on when you ask, and off again whenever the notch closes.
        var mirror = new CameraMirror(collapsible: false) { CornerRadius = new CornerRadius(10) };
        var start = Chip("Turn on camera", (_, _) => { }, Glyphs.Camera);
        var overlay = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Children = { Faint("Camera mirror · nothing is recorded"), start },
        };
        start.Margin = new Thickness(0, 6, 0, 0);
        start.Click += (_, _) => { overlay.Visibility = Visibility.Collapsed; mirror.Start(); };
        var grid = new Grid { Children = { mirror, overlay } };
        var card = CardOf(grid, new Thickness(4));
        WhileVisible(card, () => { }, () => { mirror.Stop(); overlay.Visibility = Visibility.Visible; });
        return card;
    }

    private static FrameworkElement ScreenTime()
    {
        var st = Notch.ScreenTime;
        var total = Text("", "Big", 24);
        var bars = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        void Render()
        {
            total.Text = Services.ScreenTimeService.Format(st.Today);
            bars.Children.Clear();
            var top = st.TopToday(4);
            var max = top.Count > 0 ? top[0].Time.TotalSeconds : 1;
            foreach (var (app, time) in top)
            {
                var bar = new ProgressBar { Maximum = 1, Value = time.TotalSeconds / Math.Max(1, max), Height = 4, Margin = new Thickness(0, 2, 0, 4) };
                bars.Children.Add(Columns((Text(app, "Caption"), Star()), (Text(Services.ScreenTimeService.Format(time), "Caption"), Auto)));
                bars.Children.Add(bar);
            }
            if (top.Count == 0) bars.Children.Add(Faint(SettingsStore.Current.Behavior.TrackScreenTime ? "Counting starts now." : "Screen time is off (Settings › General)."));
        }
        var card = CardOf(Layout(Header("Screen time today"), ScrollList(bars), total));
        st.Updated += () => { if (card.IsVisible) Render(); };
        WhileVisible(card, Render);
        return card;
    }

    private static FrameworkElement Spaces()
    {
        var sp = Notch.Spaces;
        var name = Bound(sp, nameof(SpacesService.Name), "Title", 15);
        var pos = Bound(sp, nameof(SpacesService.Position), "Caption");
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { name, pos } };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                Small(Glyphs.Left, "Previous desktop (Win+Ctrl+←)", sp.Previous),
                Small(Glyphs.Right, "Next desktop (Win+Ctrl+→)", sp.Next),
                Small(Glyphs.Add, "New desktop (Win+Ctrl+D)", sp.New),
                Small(Glyphs.Apps, "Task view (Win+Tab)", () => { Notch.Shell.Collapse(); sp.TaskView(); }),
            },
        };
        var card = CardOf(Columns((Icon(Glyphs.Apps, 18), Px(30)), (info, Star()), (buttons, Auto)));
        WhileVisible(card, sp.Acquire, sp.Release);
        return card;
    }

    /// <summary>
    /// One row per place: name, local time (in the user's clock format), how far ahead or behind it is, and the
    /// weather. Rows are tinted warm by day and indigo by night. Ticks only while visible.
    /// </summary>
    private static FrameworkElement WorldClocks()
    {
        var weather = Notch.Weather;
        var rows = new StackPanel();
        var empty = Faint("Add cities in Settings › Weather to see their time and weather here.");
        var settings = Small(Glyphs.Settings, "Edit places", () => Notch.Shell.ShowSettings("Weather"));
        var dayTint = new LinearGradientBrush(Color.FromArgb(0x26, 0xFF, 0xC8, 0x6E), Color.FromArgb(0x08, 0x6E, 0xB4, 0xFF), 0);
        var nightTint = new LinearGradientBrush(Color.FromArgb(0x30, 0x4B, 0x3C, 0xB4), Color.FromArgb(0x0A, 0x14, 0x1E, 0x50), 0);
        dayTint.Freeze();
        nightTint.Freeze();
        var tick = new DispatcherTimer();
        var clocks = new List<(TextBlock Time, TextBlock Diff, Border Row, PlaceWeather? Weather, WeatherPlace Place)>();

        void Build()
        {
            rows.Children.Clear();
            clocks.Clear();
            var places = SettingsStore.Current.Weather.Places;
            empty.Visibility = places.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var place in places)
            {
                var pw = weather.World.FirstOrDefault(x => x.Place.Latitude == place.Latitude && x.Place.Longitude == place.Longitude);
                // Line 1: the city, full width and bright, with the weather on the right.
                var name = Text(place.Name, "Title", 13.5);
                name.VerticalAlignment = VerticalAlignment.Center;
                var temp = Text(pw == null ? "" : $"{pw.Icon} {pw.Temperature}", "Body", 12.5);
                temp.FontFamily = new FontFamily("Segoe UI Emoji, Segoe UI Variable Text");
                temp.VerticalAlignment = VerticalAlignment.Center;
                temp.Margin = new Thickness(8, 0, 0, 0);
                temp.ToolTip = pw?.Condition;
                // Line 2: the local time, then how far ahead or behind it is.
                var time = Text("", "Title", 15);
                time.Typography.NumeralAlignment = FontNumeralAlignment.Tabular;
                time.TextTrimming = TextTrimming.None;
                var diff = Text("", "Caption", 11);
                diff.TextTrimming = TextTrimming.None;
                diff.Margin = new Thickness(0, 0, 0, 1);
                diff.VerticalAlignment = VerticalAlignment.Bottom;
                time.Margin = new Thickness(0, 0, 8, 0);
                // Side by side when there's room; on a narrow card the difference drops below the time instead of overlapping.
                var second = new WrapPanel { Margin = new Thickness(0, 1, 0, 0), Children = { time, diff } };
                var row = new Border
                {
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(10, 6, 10, 6),
                    Margin = new Thickness(0, 0, 0, 4),
                    Child = new StackPanel { Children = { Columns((name, Star()), (temp, Auto)), second } },
                    ToolTip = place.FullName,
                };
                rows.Children.Add(row);
                clocks.Add((time, diff, row, pw, place));
            }
            Tick();
        }

        void Tick()
        {
            foreach (var c in clocks)
            {
                var now = c.Weather?.Now ?? PlaceTime.Now(c.Place);
                c.Time.Text = ClockFormat.Time(now.DateTime);
                c.Diff.Text = PlaceTime.Difference(now);
                // Day/night from the forecast when we have it, else from the local hour.
                var isDay = c.Weather?.IsDay ?? now.Hour is >= 7 and < 19;
                c.Row.Background = isDay ? dayTint : nightTint;
            }
            tick.Interval = ClockFormat.TickInterval;
        }
        tick.Tick += (_, _) => Tick();

        var head = Header("World clocks", settings);
        var card = CardOf(Layout(head, ScrollList(new StackPanel { Children = { rows, empty } })));
        void OnWorld() => Ui.Post(Build);
        WhileVisible(card,
            () => { weather.WorldUpdated += OnWorld; Build(); tick.Start(); _ = weather.RefreshWorldAsync(); },
            () => { weather.WorldUpdated -= OnWorld; tick.Stop(); });
        return card;
    }

    /// <summary>The current desktop's windows as app icons; click to jump, right-click for more. "All" opens the Desktops tab.</summary>
    private static FrameworkElement OpenWindows()
    {
        var list = Notch.Windows;
        var title = Text("", "SectionHeader");
        title.VerticalAlignment = VerticalAlignment.Center;
        title.Margin = new Thickness(2, 0, 0, 0);
        var all = Small(Glyphs.TaskView, "All desktops", () => Notch.Shell.OpenTab("desktops"));
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(all, Dock.Right);
        head.Children.Add(all);
        head.Children.Add(title);
        var icons = new WrapPanel();
        var empty = Faint("Nothing open on this desktop");

        void Render()
        {
            var d = list.Current;
            var others = list.Desktops.Where(x => !x.IsCurrent).Sum(x => x.Windows.Count);
            title.Text = $"{(d?.Name ?? "This desktop").ToUpperInvariant()} · {d?.Windows.Count ?? 0}" + (others > 0 ? $"  (+{others} elsewhere)" : "");
            icons.Children.Clear();
            empty.Visibility = d is { Windows.Count: > 0 } ? Visibility.Collapsed : Visibility.Visible;
            if (d == null) return;
            foreach (var w in d.Windows)
            {
                var b = new Button
                {
                    Style = S("ChipButton"),
                    Width = 38,
                    Height = 38,
                    Padding = new Thickness(0),
                    Margin = new Thickness(0, 0, 4, 4),
                    Content = new Image { Source = w.Icon, Width = 22, Height = 22, Opacity = w.Minimized ? 0.5 : 1 },
                    ToolTip = $"{w.Title}\n{w.App}{(w.Minimized ? " · minimised" : "")}",
                    ContextMenu = DesktopsModule.WindowMenu(w, d),
                };
                b.Click += (_, _) => WindowListService.Activate(w);
                icons.Children.Add(b);
            }
        }

        var card = CardOf(Layout(head, ScrollList(new StackPanel { Children = { icons, empty } })));
        void OnChanged() => Ui.Post(Render);
        WhileVisible(card,
            () => { list.Changed += OnChanged; list.Acquire(); Render(); },
            () => { list.Changed -= OnChanged; list.Release(); });
        return card;
    }

    private static FrameworkElement Timer()
    {
        if (Notch.Modules.Get("timer") is not TimerModule timer || !timer.IsRunning)
            return CardOf(Layout(Header("Timer"), Faint("Turn on Timer in Settings › Features.")));
        var phase = Text("", "Caption");
        var time = Text("", "Big", 26);
        var progress = new ProgressBar { Maximum = 1, Height = 4, Margin = new Thickness(0, 4, 0, 6) };
        var play = Small(Glyphs.Play, "Start / pause", timer.StartPause);
        var reset = Small(Glyphs.Refresh, "Reset", timer.Reset);
        var skip = Small(Glyphs.Next, "Skip", timer.Skip);
        void Render()
        {
            phase.Text = TimerModule.PhaseName(timer.Phase) + (timer.Running ? "" : " · paused");
            time.Text = Ui.FormatSpan(timer.Remaining);
            progress.Value = timer.Progress;
            play.Content = timer.Running ? Glyphs.Pause : Glyphs.Play;
        }
        var stack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children = { phase, time, progress, new StackPanel { Orientation = Orientation.Horizontal, Children = { play, reset, skip } } },
        };
        var card = CardOf(stack);
        timer.Changed += () => { if (card.IsVisible) Render(); };
        // The module only ticks while running; keep a paused display fresh too.
        WhileVisible(card, Render);
        return card;
    }

    private static FrameworkElement SystemStats()
    {
        var s = Notch.Stats;
        var cpu = Text("", "Title", 13);
        var mem = Text("", "Title", 13);
        var net = Text("", "Caption");
        void Render()
        {
            cpu.Text = $"CPU {s.Cpu:0}%";
            mem.Text = $"RAM {s.Memory:0}%";
            net.Text = $"↓ {s.Download}   ↑ {s.Upload}";
        }
        s.Updated += () => Ui.Post(Render);
        var row = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children = { Columns((cpu, Star()), (mem, Star())), net },
        };
        var card = CardOf(row);
        card.MouseLeftButtonUp += (_, _) => Notch.Shell.OpenTab("stats");
        card.ToolTip = "Click for details";
        WhileVisible(card, () => { s.Acquire(); Render(); }, s.Release);
        return card;
    }

    private static FrameworkElement Launcher()
    {
        if (Notch.Modules.Get("launcher") is not LauncherModule launcher || !launcher.IsRunning)
            return CardOf(Layout(Header("Apps"), Faint("Turn on Launcher in Settings › Features.")));
        var panel = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        void Render()
        {
            panel.Children.Clear();
            foreach (var app in launcher.Apps)
            {
                var a = app;
                var b = new Button { Style = S("IconButton"), Width = 40, Height = 40, ToolTip = a.Name, Margin = new Thickness(0, 0, 4, 4),
                    Content = new Image { Source = a.Icon, Width = 26, Height = 26 } };
                b.Click += (_, _) => launcher.Launch(a);
                panel.Children.Add(b);
            }
            var add = Small(Glyphs.Add, "Add apps in the Launcher tab", () => Notch.Shell.OpenTab("launcher"));
            add.Width = add.Height = 40;
            panel.Children.Add(add);
        }
        launcher.Apps.CollectionChanged += (_, _) => Render();
        Render();
        return CardOf(ScrollList(panel), new Thickness(10, 8, 6, 4));
    }
}
