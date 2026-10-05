using System.Diagnostics;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Notchify.Core;
using Notchify.Modules;
using Notchify.Services;
using static Notchify.Views.ViewKit;

namespace Notchify.Shell;

/// <summary>
/// Settings. Every change applies immediately (live preview) and is saved to
/// %APPDATA%\NotchX\settings.json, which is also safe to edit by hand.
/// </summary>
public sealed class SettingsWindow : Window
{
    private readonly ContentControl _page = new();
    private readonly ListBox _nav = new() { Width = 196 };
    private readonly List<(string Key, string Glyph, Func<UIElement> Build)> _pages;

    private static AppSettings S => SettingsStore.Current;

    public SettingsWindow()
    {
        Title = $"{AppInfo.Name} Settings";
        Icon = AppInfo.Icon;
        Width = 940;
        Height = 720;
        MinWidth = 760;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = (FontFamily)FindResource("UiFont");
        SetResourceReference(BackgroundProperty, "PanelBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        SourceInitialized += (_, _) =>
        {
            // Dark title bar on Windows 10/11.
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var on = 1;
            Native.DwmSetWindowAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        };

        _pages = new()
        {
            ("General", Glyphs.Settings, General),
            ("Appearance", Glyphs.Color, Appearance),
            ("Features", Glyphs.Apps, Features),
            ("Behaviour", Glyphs.Mouse, Behaviour),
            ("Home", Glyphs.Home, Home),
            ("Tabs", Glyphs.View, Tabs),
            ("Notifications", Glyphs.Bell, Notifications),
            ("Now Playing", Glyphs.Music, NowPlaying),
            ("Weather", Glyphs.Sun, Weather),
            ("Calendar", Glyphs.Calendar, Calendar),
            ("Clipboard", Glyphs.Clipboard, ClipboardPage),
            ("Quick Search", Glyphs.Search, QuickSearch),
            ("Timer", Glyphs.Stopwatch, Timer),
            ("Screen Capture", Glyphs.Crop, Capture),
            ("Documents", Glyphs.Document, Documents),
            ("AI Usage", Glyphs.Robot, AiUsage),
            ("Terminal", Glyphs.Terminal, Terminal),
            ("Cuely", Glyphs.Reading, Cuely),
            ("Windows & Spaces", Glyphs.Snap, WindowsAndSpaces),
            ("Displays", Glyphs.Monitor, Displays),
            ("Battery", Glyphs.Battery(70, false), BatteryPage),
            ("Connectivity", Glyphs.Bluetooth, Connectivity),
            ("Caffeine", Glyphs.Bolt, Caffeine),
            ("Permissions", Glyphs.Shield, Permissions),
            ("Developer", Glyphs.Code, Developer),
            ("About", Glyphs.Info, About),
            ($"Quit {AppInfo.Name}", Glyphs.Close, Quit),
        };
        foreach (var (key, glyph, _) in _pages)
        {
            var label = new TextBlock { Text = key, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            var icon = Icon(glyph, 13);
            icon.Width = 18;
            _nav.Items.Add(new ListBoxItem { Tag = key, Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, label } } });
        }
        _nav.SelectionChanged += (_, _) =>
        {
            if (_nav.SelectedItem is not ListBoxItem { Tag: string key }) return;
            var page = _pages.First(p => p.Key == key);
            var stack = new StackPanel { Margin = new Thickness(4, 0, 18, 24) };
            stack.Children.Add(Text(key, "Big", 22));
            stack.Children.Add(page.Build());
            _page.Content = Scroll(stack);
        };
        _nav.SelectedIndex = 0;
        ScrollViewer.SetVerticalScrollBarVisibility(_nav, ScrollBarVisibility.Auto);

        var grid = Columns((new Border { Child = _nav, Padding = new Thickness(10, 12, 6, 12) }, Auto), (new Border { Child = _page, Padding = new Thickness(10, 14, 0, 0) }, Star()));
        Content = grid;
        Closed += (_, _) => SettingsStore.SaveNow();
    }

    /// <summary>Jump to a page by name ("Home", "Screen Capture", …).</summary>
    public void Navigate(string key)
    {
        var item = _nav.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == key);
        if (item != null) { _nav.SelectedItem = item; item.BringIntoView(); }
    }

    // ---------------- building blocks ----------------

    private static void Changed(Action? after = null)
    {
        SettingsStore.NotifyChanged();
        after?.Invoke();
    }

    private static UIElement Header(string text, string? sub = null)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 16, 0, 8) };
        sp.Children.Add(Text(text, "Title", 15));
        if (sub != null)
        {
            var t = Text(sub, "Caption");
            t.TextWrapping = TextWrapping.Wrap;
            sp.Children.Add(t);
        }
        return sp;
    }

    private static TextBlock Note(string text)
    {
        var t = Text(text, "Caption");
        t.TextWrapping = TextWrapping.Wrap;
        t.Margin = new Thickness(0, 2, 0, 6);
        return t;
    }

    private static UIElement Toggle(string label, Func<bool> get, Action<bool> set, Action? after = null, string? tip = null)
    {
        var c = Switch(label, get(), v => { set(v); Changed(after); }, tip);
        c.Margin = new Thickness(0, 4, 0, 4);
        return c;
    }

    private static UIElement Row(string label, UIElement control, string? hint = null)
    {
        var l = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        l.Children.Add(Text(label, "Body"));
        if (hint != null)
        {
            var h = Text(hint, "Caption", 10);
            h.TextWrapping = TextWrapping.Wrap;
            l.Children.Add(h);
        }
        var row = Columns((l, Star()), (control, Px(320)));
        row.Margin = new Thickness(0, 5, 0, 5);
        return row;
    }

    private static UIElement Slide(string label, double min, double max, Func<double> get, Action<double> set, string format = "0", Action? after = null, string? hint = null)
    {
        var value = Text(get().ToString(format), "Caption");
        value.Width = 48;
        value.TextAlignment = TextAlignment.Right;
        var slider = new Slider { Minimum = min, Maximum = max, Value = get() };
        slider.ValueChanged += (_, e) =>
        {
            set(e.NewValue);
            value.Text = e.NewValue.ToString(format);
            Changed(after);
        };
        return Row(label, Columns((slider, Star()), (value, Auto)), hint);
    }

    private static UIElement Field(string label, Func<string> get, Action<string> set, string? hint = null, Action? after = null, bool multiline = false)
    {
        var box = new TextBox { Text = get(), AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap };
        if (multiline) { box.MinHeight = 70; box.VerticalContentAlignment = VerticalAlignment.Top; }
        box.LostFocus += (_, _) => { if (box.Text != get()) { set(box.Text); Changed(after); } };
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter && !multiline) { set(box.Text); Changed(after); } };
        return Row(label, box, hint);
    }

    private static UIElement Choice(string label, string[] options, Func<string> get, Action<string> set, Action? after = null, string? hint = null)
    {
        var combo = new ComboBox { ItemsSource = options, SelectedItem = options.Contains(get()) ? get() : options[0] };
        combo.SelectionChanged += (_, _) => { set((string)combo.SelectedItem); Changed(after); };
        return Row(label, combo, hint);
    }

    /// <summary>A drop-down whose labels differ from the stored values.</summary>
    private static UIElement Choice(string label, (string Label, string Value)[] options, Func<string> get, Action<string> set, Action? after = null, string? hint = null)
    {
        var labels = options.Select(o => o.Label).ToArray();
        var current = options.FirstOrDefault(o => o.Value == get()).Label ?? labels[0];
        return Choice(label, labels, () => current, v => { current = v; set(options.First(o => o.Label == v).Value); }, after, hint);
    }

    private static UIElement ColorPick(string label, Func<string> get, Action<string> set, Action? after = null)
    {
        var swatch = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = Ui.Brush(get()), Margin = new Thickness(0, 0, 8, 0) };
        var box = new TextBox { Text = get(), Width = 110 };
        void Apply(string hex)
        {
            if (!hex.StartsWith('#')) hex = "#" + hex;
            try { ColorConverter.ConvertFromString(hex); } catch { return; }
            set(hex);
            box.Text = hex;
            swatch.Background = Ui.Brush(hex);
            Changed(after);
        }
        box.LostFocus += (_, _) => Apply(box.Text);
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Apply(box.Text); };
        var presets = new WrapPanel { Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Width = 120 };
        foreach (var hex in new[] { "#FF0A84FF", "#FF30D158", "#FF34D399", "#FFFF9F0A", "#FFFF375F", "#FFBF5AF2", "#FF64D2FF", "#FFFFFFFF" })
        {
            var b = new Button { Width = 14, Height = 14, Margin = new Thickness(1), Cursor = Cursors.Hand, ToolTip = hex };
            b.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Button'><Border CornerRadius='7' Background='" + hex + "' /></ControlTemplate>");
            b.Click += (_, _) => Apply(hex);
            presets.Children.Add(b);
        }
        return Row(label, new StackPanel { Orientation = Orientation.Horizontal, Children = { swatch, box, presets } });
    }

    private static StackPanel Buttons(params UIElement[] buttons)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 4) };
        foreach (var b in buttons) sp.Children.Add(b);
        return sp;
    }

    private static StackPanel Page(params UIElement[] children)
    {
        var sp = new StackPanel();
        foreach (var c in children) sp.Children.Add(c);
        return sp;
    }

    /// <summary>Clock options, with a live sample of the result.</summary>
    private static UIElement ClockSection(Action preview)
    {
        var c = S.Clock;
        var sample = Text("", "Body");
        sample.Margin = new Thickness(0, 2, 0, 6);
        void Update()
        {
            sample.Text = "Looks like: " + ClockFormat.Header(DateTime.Now);
            preview();
        }
        var panel = Page(
            Header("Clock", "The time and date at the top right of the open notch. The time format also applies to the Home clock card."),
            Toggle("Show the clock in the open notch", () => c.ShowInHeader, v => c.ShowInHeader = v, Update),
            Choice("Time format", new[] { ("Same as Windows", "System"), ("12-hour (2:05 PM)", "12h"), ("24-hour (14:05)", "24h") },
                () => c.TimeFormat, v => c.TimeFormat = v, Update),
            Toggle("Show seconds", () => c.ShowSeconds, v => c.ShowSeconds = v, Update),
            Toggle("Show AM / PM", () => c.ShowAmPm, v => c.ShowAmPm = v, Update, "Only matters for 12-hour times"),
            Choice("Date", new[] { ("Day and date (Sat 4 Oct)", "Short"), ("Day only (Sat)", "Day"), ("Full (Saturday, 4 October)", "Long"), ("Numbers, like Windows (04/10/2026)", "Numeric"), ("No date", "None") },
                () => c.DateStyle, v => c.DateStyle = v, Update),
            sample);
        sample.Text = "Looks like: " + ClockFormat.Header(DateTime.Now);
        return panel;
    }

    private static StackPanel Indented(StackPanel panel)
    {
        panel.Margin = new Thickness(24, 0, 0, 0);
        return panel;
    }

    private static void PreviewHome() { Notch.Shell.OpenTab("home"); }

    // ---------------- General ----------------

    private UIElement General()
    {
        var b = S.Behavior;
        return Page(
            Header("Startup"),
            Toggle($"Start {AppInfo.Name} with Windows", () => b.StartWithWindows, v => b.StartWithWindows = v),
            Toggle("Show tray icon", () => b.ShowTrayIcon, v => b.ShowTrayIcon = v, tip: "Left-click opens the notch, middle-click toggles Caffeine"),
            Header("Shortcuts", "Use names like Ctrl+Shift+Space, Alt+N, Win+Shift+K."),
            Field("Command palette", () => b.PaletteHotkey, v => b.PaletteHotkey = v),
            Field("Open / close the notch", () => b.ToggleHotkey, v => b.ToggleHotkey = v),
            Field("Screen capture", () => S.Capture.Hotkey, v => S.Capture.Hotkey = v, "Empty = no shortcut"),
            Header("Screen time", "Powers the Screen time card. Counted on this PC only; idle time and the lock screen don't count."),
            Toggle("Track screen time", () => b.TrackScreenTime, v => b.TrackScreenTime = v),
            Header("Your data", $"Everything lives in {Paths.Root}."),
            Buttons(
                Chip("Open data folder", (_, _) => Ui.OpenUrl(Paths.Root), Glyphs.Folder),
                Chip("Edit settings.json", (_, _) => { SettingsStore.SaveNow(); Process.Start(new ProcessStartInfo("notepad.exe", Paths.SettingsFile)); }, Glyphs.Edit)));
    }

    // ---------------- Appearance ----------------

    private UIElement Appearance()
    {
        var a = S.Appearance;
        void PreviewExpanded() => Notch.Shell.Expand();

        var gradientRows = Page(
            ColorPick("Second colour", () => a.GradientColor, v => a.GradientColor = v, PreviewExpanded),
            Slide("Direction (°)", 0, 360, () => a.GradientAngle, v => a.GradientAngle = v, after: PreviewExpanded, hint: "0 = left to right, 90 = top to bottom"));
        var imagePath = Text(string.IsNullOrEmpty(a.BackgroundImage) ? "No picture chosen" : System.IO.Path.GetFileName(a.BackgroundImage), "Caption");
        imagePath.VerticalAlignment = VerticalAlignment.Center;
        imagePath.Margin = new Thickness(8, 0, 0, 0);
        var imageRows = Page(
            Row("Picture", new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    Chip("Choose…", (_, _) =>
                    {
                        var dlg = new OpenFileDialog { Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff|All files|*.*" };
                        if (dlg.ShowDialog(this) != true) return;
                        a.BackgroundImage = dlg.FileName;
                        imagePath.Text = System.IO.Path.GetFileName(dlg.FileName);
                        Changed(PreviewExpanded);
                    }, Glyphs.Photo),
                    imagePath,
                },
            }),
            Slide("Picture opacity", 0, 1, () => a.BackgroundImageOpacity, v => a.BackgroundImageOpacity = v, "0.00", PreviewExpanded));
        void SyncBackgroundRows()
        {
            gradientRows.Visibility = a.BackgroundType == "Gradient" ? Visibility.Visible : Visibility.Collapsed;
            imageRows.Visibility = a.BackgroundType == "Image" ? Visibility.Visible : Visibility.Collapsed;
        }
        SyncBackgroundRows();

        var glassNote = Note("Windows transparency effects are off (Settings › Personalisation › Colours), so the notch uses a solid fill.");
        glassNote.Visibility = GlassBackdrop.SystemAllowsBlur ? Visibility.Collapsed : Visibility.Visible;

        return Page(
            Header("Size & shape", "Drag the sliders — the notch updates live."),
            Choice("Style", new[] { "Notch", "Pill" }, () => a.Style, v => a.Style = v, hint: "Notch hugs the top edge; Pill floats with round corners"),
            Slide("Collapsed width (length)", 100, 400, () => a.CollapsedWidth, v => a.CollapsedWidth = v),
            Slide("Collapsed height", 20, 48, () => a.CollapsedHeight, v => a.CollapsedHeight = v),
            Slide("Open width", 460, 1400, () => a.ExpandedWidth, v => a.ExpandedWidth = v, after: PreviewExpanded),
            Slide("Open height", 180, 700, () => a.ExpandedHeight, v => a.ExpandedHeight = v, after: PreviewExpanded, hint: "Taller = bigger Home cards"),
            Slide("Corner radius", 0, 40, () => a.CornerRadius, v => a.CornerRadius = v, after: PreviewExpanded),
            Header("Text size"),
            Slide("Text & controls", 0.8, 1.4, () => a.UiScale, v => a.UiScale = v, "0.00×", PreviewExpanded, "Scales everything inside the notch — raise the open height if it gets cramped"),
            Choice("Tab labels", new[] { ("Always", "Always"), ("Only on the open tab", "Selected"), ("Never (icons only)", "Never") },
                () => a.TabLabels, v => a.TabLabels = v, PreviewExpanded, "Names under the tab icons. Tabs that don't fit go into “More ▾” at the end of the row"),
            ClockSection(PreviewExpanded),
            Header("Frosted glass", "Blurs whatever is behind the open notch, like Windows 11's own flyouts."),
            Toggle("Frosted glass", () => a.Glass, v => a.Glass = v, PreviewExpanded),
            glassNote,
            Slide("Tint", 0, 1, () => a.TintOpacity, v => a.TintOpacity = v, "0.00", PreviewExpanded, "0 = clear glass, 1 = solid colour"),
            Slide("Sheen & rim highlight", 0, 1, () => a.GlassIntensity, v => a.GlassIntensity = v, "0.00", PreviewExpanded),
            Header("Colours & background", "The collapsed pill always uses the background colour so it blends with the screen edge."),
            ColorPick("Accent colour", () => a.AccentColor, v => a.AccentColor = v),
            ColorPick("Background colour", () => a.BackgroundColor, v => a.BackgroundColor = v, PreviewExpanded),
            Choice("Background", new[] { "Solid", "Gradient", "Image" }, () => a.BackgroundType, v => a.BackgroundType = v,
                () => { SyncBackgroundRows(); PreviewExpanded(); }, "Fill of the open notch and islands"),
            gradientRows,
            imageRows,
            Header("Motion"),
            Toggle("Animations", () => a.AnimationsEnabled, v => a.AnimationsEnabled = v),
            Slide("Animation speed", 0.25, 3, () => a.AnimationSpeed, v => a.AnimationSpeed = v, "0.00×"));
    }

    // ---------------- Features ----------------

    private UIElement Features()
    {
        var stack = new StackPanel();
        stack.Children.Add(Header("Features", "Turn any feature on or off. Turning one off stops it completely (its tab, pill chips and background work)."));
        void Render()
        {
            while (stack.Children.Count > 1) stack.Children.RemoveAt(1);
            foreach (var m in Notch.Modules.All)
            {
                var module = m;
                var desc = Text(m.Description, "Caption");
                desc.TextWrapping = TextWrapping.Wrap;
                var info = new StackPanel { Children = { Text(m.Title + (m.HasTab ? "" : "  · background"), "Body"), desc } };
                var sw = new CheckBox { Style = S("Switch"), IsChecked = Notch.Modules.IsEnabled(m), VerticalAlignment = VerticalAlignment.Center };
                sw.Click += (_, _) => { Notch.Modules.SetEnabled(module.Id, sw.IsChecked == true); Render(); };
                var row = Columns((Icon(m.Glyph, 16), Px(34)), (info, Star()), (sw, Px(52)));
                stack.Children.Add(Card(row, new Thickness(0, 0, 0, 6)));
            }
        }
        Render();
        return stack;
    }

    // ---------------- Behaviour ----------------

    private UIElement Behaviour()
    {
        var b = S.Behavior;
        var openOn = Notch.Modules.Tabs.Select(t => (t.Title, t.Id))
            .Append(("Last used tab", "last"))
            .Append(("Smart — music while playing, timer while running, else Home", "smart"))
            .ToArray();
        return Page(
            Header("Opening", "Also available by right-clicking the notch."),
            Choice("Open on", openOn, () => b.DefaultTab, v => b.DefaultTab = v, hint: "The tab shown when the notch opens (hover, click or shortcut)"),
            Toggle("Hover to open", () => b.HoverToOpen, v => b.HoverToOpen = v),
            Slide("Hover delay (ms)", 0, 1000, () => b.HoverDelayMs, v => b.HoverDelayMs = (int)v),
            Toggle("Peek on hover (mini player instead of opening)", () => b.PeekOnHover, v => b.PeekOnHover = v),
            Header("Closing"),
            Toggle("Auto-collapse when the mouse leaves", () => b.AutoCollapse, v => b.AutoCollapse = v),
            Slide("Auto-collapse delay (ms)", 100, 3000, () => b.AutoCollapseDelayMs, v => b.AutoCollapseDelayMs = (int)v),
            Header("Gestures"),
            Toggle("Scroll on the tab bar / swipe to switch tabs", () => b.ScrollToSwitchTabs, v => b.ScrollToSwitchTabs = v),
            Toggle("Scroll on the collapsed pill to change volume", () => b.ScrollOnPillChangesVolume, v => b.ScrollOnPillChangesVolume = v),
            Header("Privacy & fullscreen"),
            Toggle($"Hide {AppInfo.Name} from screen capture (OBS, Zoom, Teams, screenshots)", () => b.HideFromScreenCapture, v => b.HideFromScreenCapture = v),
            Toggle("Hide the pill while an app is fullscreen", () => b.HideInFullscreen, v => b.HideInFullscreen = v));
    }

    // ---------------- Home ----------------

    private UIElement Home()
    {
        var root = new StackPanel();
        void Render()
        {
            root.Children.Clear();
            root.Children.Add(Note("Cards sit on a 12-column grid. Width is in columns (12 = full width), height in rows (two rows fill the notch; more rows scroll). Shorter cards slide in under taller ones."));
            root.Children.Add(Buttons(
                Chip("Arrange in the notch", (_, _) => { Notch.Shell.OpenTab("home"); LayoutEditor.IsEditing = true; }, Glyphs.Edit, accent: true),
                Chip("Reset to default", (_, _) => { LayoutEditor.ResetHome(); Render(); }, Glyphs.Refresh)));

            root.Children.Add(Header("Preview"));
            root.Children.Add(LayoutPreview());

            var cards = LayoutEditor.HomeCards();
            root.Children.Add(Header($"On Home ({cards.Count})", "Order, width and height. Changes show in the notch straight away."));
            foreach (var card in cards)
            {
                var c = card;
                var info = LayoutEditor.Info(c.Type)!;
                UIElement Stepper(string label, int value, int min, int max, Action<int> set)
                {
                    var minus = IconButton("", $"Less {label.ToLowerInvariant()}", (_, _) => { set(value - 1); Render(); });
                    var plus = IconButton(Glyphs.Add, $"More {label.ToLowerInvariant()}", (_, _) => { set(value + 1); Render(); });
                    minus.IsEnabled = value > min; minus.Opacity = value > min ? 1 : 0.35;
                    plus.IsEnabled = value < max; plus.Opacity = value < max ? 1 : 0.35;
                    var t = Text($"{label} {value}", "Caption");
                    t.Width = 52;
                    t.TextAlignment = TextAlignment.Center;
                    t.VerticalAlignment = VerticalAlignment.Center;
                    return new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 6, 0), Children = { minus, t, plus } };
                }
                var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { Text(info.Name, "Body"), Text(info.Description, "Caption", 10) } };
                var width = Stepper("Width", c.Cols, info.MinCols, LayoutEditor.GridColumns, v => LayoutEditor.Resize(c.Type, v, c.Rows));
                var height = Stepper("Height", c.Rows, info.MinRows, LayoutEditor.MaxRows, v => LayoutEditor.Resize(c.Type, c.Cols, v));
                var up = IconButton(Glyphs.Up, "Move earlier", (_, _) => { LayoutEditor.Move(c.Type, -1); Render(); });
                var down = IconButton(Glyphs.Down, "Move later", (_, _) => { LayoutEditor.Move(c.Type, 1); Render(); });
                var remove = IconButton(Glyphs.Delete, "Remove from Home", (_, _) => { LayoutEditor.Remove(c.Type); Render(); });
                var row = Columns((Icon(info.Glyph, 15), Px(30)), (name, Star()), (width, Auto), (height, Auto), (up, Auto), (down, Auto), (remove, Auto));
                root.Children.Add(Card(row, new Thickness(0, 0, 0, 6)));
            }

            var available = LayoutEditor.Available().ToList();
            root.Children.Add(Header("Add cards", available.Count == 0 ? "Every card is on Home." : "Click to add. New cards go at the end."));
            var gallery = new WrapPanel();
            foreach (var info in available)
            {
                var type = info.Type;
                var desc = Text(info.Description, "Caption", 10);
                desc.TextWrapping = TextWrapping.Wrap;
                var tile = new Button
                {
                    Style = S("ChipButton"), Width = 214, Height = 62, Margin = new Thickness(0, 0, 8, 8),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch,
                    Padding = new Thickness(10, 6, 10, 6),
                    Content = Columns((Icon(info.Glyph, 16), Px(28)), (new StackPanel { Children = { Text($"{info.Name}  ·  {info.Cols}×{info.Rows}", "Title", 12), desc } }, Star())),
                };
                tile.Click += (_, _) => { LayoutEditor.Add(type); Render(); };
                gallery.Children.Add(tile);
            }
            root.Children.Add(gallery);
        }
        Render();
        return root;
    }

    /// <summary>A miniature of the Home grid: where each card lands after packing.</summary>
    private static UIElement LayoutPreview()
    {
        const double unitW = 46, unitH = 34, gap = 4;
        var canvas = new Canvas { Width = 12 * unitW + 11 * gap };
        var packed = LayoutEditor.Pack(LayoutEditor.HomeCards());
        var rows = packed.Count == 0 ? 1 : packed.Max(p => p.Row + Math.Clamp(p.Card.Rows, 1, LayoutEditor.MaxRows));
        canvas.Height = rows * unitH + (rows - 1) * gap;
        // The two rows that are visible without scrolling.
        canvas.Children.Add(new Border
        {
            Width = canvas.Width + 8, Height = 2 * unitH + gap + 8, CornerRadius = new CornerRadius(10),
            BorderBrush = (Brush)Application.Current.FindResource("DividerBrush"), BorderThickness = new Thickness(1),
            Margin = new Thickness(-4, -4, 0, 0), ToolTip = "Visible without scrolling",
        });
        foreach (var (card, col, row) in packed)
        {
            var info = LayoutEditor.Info(card.Type);
            var cols = Math.Clamp(card.Cols, 1, LayoutEditor.GridColumns);
            var rws = Math.Clamp(card.Rows, 1, LayoutEditor.MaxRows);
            var label = Text(info?.Name ?? card.Type, "Caption", 10);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.VerticalAlignment = VerticalAlignment.Center;
            var box = new Border
            {
                Width = cols * unitW + (cols - 1) * gap,
                Height = rws * unitH + (rws - 1) * gap,
                CornerRadius = new CornerRadius(6),
                Background = (Brush)Application.Current.FindResource("CardHoverBrush"),
                Child = label,
            };
            Canvas.SetLeft(box, col * (unitW + gap));
            Canvas.SetTop(box, row * (unitH + gap));
            canvas.Children.Add(box);
        }
        return new Border { Child = canvas, Padding = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Left };
    }

    // ---------------- Tabs ----------------

    private UIElement Tabs()
    {
        var stack = new StackPanel();
        void Render()
        {
            stack.Children.Clear();
            stack.Children.Add(Note("Show or hide tabs and set their order (Ctrl+1–9 follows it). A hidden tab's feature keeps running — e.g. music still shows on the pill. You can also drag tabs in the notch: click ✎ in its header."));
            stack.Children.Add(Buttons(Chip("Arrange in the notch", (_, _) => { Notch.Shell.Expand(); LayoutEditor.IsEditing = true; }, Glyphs.Edit, accent: true)));
            foreach (var m in Notch.Modules.TabCandidates)
            {
                var module = m;
                var shown = Notch.Modules.IsTabVisible(m.Id);
                var sw = new CheckBox { Style = S("Switch"), IsChecked = shown, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Show this tab" };
                sw.Click += (_, _) => { Notch.Modules.SetTabVisible(module.Id, sw.IsChecked == true); Render(); };
                var up = IconButton(Glyphs.Up, "Move left", (_, _) => { Notch.Modules.Move(module.Id, -1); Render(); });
                var down = IconButton(Glyphs.Down, "Move right", (_, _) => { Notch.Modules.Move(module.Id, 1); Render(); });
                var popout = IconButton(Glyphs.PopOut, "Pop out into its own window", (_, _) => Notch.Shell.PopOut(module.Id));
                var name = Text(m.Title, "Body");
                name.VerticalAlignment = VerticalAlignment.Center;
                var row = Columns((Icon(m.Glyph, 15), Px(30)), (name, Star()), (popout, Auto), (up, Auto), (down, Auto), (sw, Px(52)));
                var card = Card(row, new Thickness(0, 0, 0, 6));
                card.Opacity = shown ? 1 : 0.6;
                stack.Children.Add(card);
            }
            stack.Children.Add(Note("Features that are switched off (Settings › Features) don't appear here."));
        }
        Render();
        return stack;
    }

    // ---------------- Notifications ----------------

    private UIElement Notifications()
    {
        var h = S.Huds;
        void Restart() { Notch.Modules.Restart("huds"); Notch.Modules.Restart("filealerts"); }
        return Page(
            Header("System HUDs", "What appears in the notch when you change volume or brightness."),
            Toggle("Volume HUD", () => h.VolumeHud, v => h.VolumeHud = v),
            Toggle("Replace the Windows volume flyout", () => h.ReplaceWindowsVolumeFlyout, v => h.ReplaceWindowsVolumeFlyout = v, Restart,
                $"{AppInfo.Name} handles the volume keys itself so the stock overlay never appears"),
            Toggle("Brightness HUD", () => h.BrightnessHud, v => h.BrightnessHud = v),
            Choice("HUD style", new[] { "Island", "EdgeBar", "Gauge" }, () => h.HudStyle, v => h.HudStyle = v, hint: "Island in the notch, a bar on the screen edge, or a circular gauge"),
            Slide("HUD size", 0.7, 1.6, () => h.HudScale, v => h.HudScale = v, "0.00×"),
            Toggle("Caps Lock HUD", () => h.CapsLockHud, v => h.CapsLockHud = v, Restart),
            Toggle("Keystroke HUD (for presenting)", () => h.KeystrokeHud, v => h.KeystrokeHud = v, Restart, "Password fields are never shown"),
            Header("Alerts"),
            Toggle("Download finished", () => h.DownloadAlerts, v => h.DownloadAlerts = v, Restart),
            Toggle("Sound output changed (headphones on / off)", () => h.OutputSwitchIsland, v => h.OutputSwitchIsland = v),
            Toggle("Camera / microphone in use (privacy dot)", () => h.PrivacyIndicator, v => h.PrivacyIndicator = v),
            Header("Focus", "While Windows Do Not Disturb is on, low-priority islands stay quiet."),
            Toggle("Do Not Disturb island", () => h.FocusIsland, v => h.FocusIsland = v));
    }

    // ---------------- Now Playing ----------------

    private UIElement NowPlaying()
    {
        var m = S.Media;
        return Page(
            Note("Works with Spotify, Apple Music, Media Player, YouTube in any browser, and anything else that shows in Windows' media controls."),
            Toggle("Show music on the collapsed pill", () => m.ShowInPill, v => m.ShowInPill = v),
            Toggle("Live audio spectrum (real FFT of system audio)", () => m.LiveSpectrum, v => m.LiveSpectrum = v),
            Toggle("Live spectrum on the collapsed pill too", () => m.SpectrumInPill, v => m.SpectrumInPill = v, tip: "Uses a little more CPU while music plays"),
            Toggle("Synced lyrics (LRCLIB)", () => m.Lyrics, v => m.Lyrics = v),
            Toggle("Show the current lyric line on the pill", () => m.LyricsInPill, v => m.LyricsInPill = v));
    }

    // ---------------- Weather ----------------

    private UIElement Weather()
    {
        var w = S.Weather;
        return Page(
            Note("Open-Meteo, no key needed. Leave the city empty to use Windows location."),
            Field("City", () => w.City, v => { w.City = v; w.Latitude = null; w.Longitude = null; }, after: () => _ = Notch.Weather.RefreshAsync()),
            Toggle("Fahrenheit", () => w.Fahrenheit, v => w.Fahrenheit = v, () => _ = Notch.Weather.RefreshAsync()),
            Slide("Refresh every (min)", 5, 120, () => w.RefreshMinutes, v => w.RefreshMinutes = (int)v),
            Buttons(Chip("Refresh now", (_, _) => _ = Notch.Weather.RefreshAsync(), Glyphs.Refresh)));
    }

    // ---------------- Calendar ----------------

    private UIElement Calendar()
    {
        var c = S.Calendar;
        return Page(
            Note("Paste secret iCal (.ics) links, one per line. Google: Settings › your calendar › “Secret address in iCal format”. Outlook: Settings › Calendar › Shared calendars › Publish. iCloud: share as Public Calendar."),
            Field("iCal links", () => string.Join("\n", c.IcsUrls), v => c.IcsUrls = v.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                after: () => _ = Notch.Calendar.RefreshAsync(), multiline: true),
            Slide("Alert before events (min)", 0, 30, () => c.AlertMinutesBefore, v => c.AlertMinutesBefore = (int)v),
            Slide("Refresh every (min)", 5, 120, () => c.RefreshMinutes, v => c.RefreshMinutes = (int)v),
            Header("Reminders", "Type them in plain words in the Calendar tab or the Reminders card: “Call Sam tomorrow 9am”, “Standup at 9:30”, “Stretch in 20m”."));
    }

    // ---------------- Clipboard ----------------

    private UIElement ClipboardPage()
    {
        var cl = S.Clipboard;
        return Page(
            Toggle("Keep clipboard history", () => cl.Enabled, v => cl.Enabled = v),
            Slide("History size", 20, 1000, () => cl.MaxItems, v => cl.MaxItems = (int)v, hint: "Pinned items don't count"),
            Toggle("Capture images", () => cl.CaptureImages, v => cl.CaptureImages = v),
            Toggle("OCR images on-device (searchable)", () => cl.Ocr, v => cl.Ocr = v),
            Toggle("Auto-paste into the previous app", () => cl.AutoPaste, v => cl.AutoPaste = v),
            Field("Never record from", () => string.Join(", ", cl.IgnoredApps), v => cl.IgnoredApps = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(), "Process names, comma separated"),
            Buttons(Chip("Clear history (keeps pins)", (_, _) => Notch.Clipboard.Clear(), Glyphs.Delete)));
    }

    // ---------------- Quick Search ----------------

    private UIElement QuickSearch()
    {
        var se = S.Search;
        return Page(
            Choice("Default engine", se.Engines.Select(e => e.Name).ToArray(), () => se.DefaultEngine, v => se.DefaultEngine = v),
            Field("Search engines", () => string.Join("\n", se.Engines.Select(e => $"{e.Name} | {e.Url}")),
                v => se.Engines = v.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('|', 2)).Where(p => p.Length == 2)
                    .Select(p => new SearchEngine { Name = p[0].Trim(), Url = p[1].Trim() }).ToList(),
                "One per line: Name | https://example.com/?q=%s", multiline: true),
            Header("Translate"),
            Choice("Translator", new[] { ("Google (no key)", "google"), ("LibreTranslate", "libre") }, () => se.TranslateProvider, v => se.TranslateProvider = v),
            Field("Translate into", () => se.TranslateTo, v => se.TranslateTo = v, "Language code, e.g. en, hi, fr"),
            Field("LibreTranslate URL", () => se.LibreTranslateUrl, v => se.LibreTranslateUrl = v),
            Field("LibreTranslate key", () => se.LibreTranslateKey, v => se.LibreTranslateKey = v),
            Header("Save videos"),
            Field("Video downloader", () => se.VideoDownloader, v => se.VideoDownloader = v, "Path to yt-dlp (winget install yt-dlp)"));
    }

    // ---------------- Timer ----------------

    private UIElement Timer()
    {
        var t = S.Timer;
        return Page(
            Slide("Focus (min)", 5, 90, () => t.FocusMinutes, v => t.FocusMinutes = (int)v),
            Slide("Short break (min)", 1, 30, () => t.ShortBreakMinutes, v => t.ShortBreakMinutes = (int)v),
            Slide("Long break (min)", 5, 60, () => t.LongBreakMinutes, v => t.LongBreakMinutes = (int)v),
            Slide("Sessions before a long break", 2, 8, () => t.SessionsBeforeLongBreak, v => t.SessionsBeforeLongBreak = (int)v),
            Toggle("Chain sessions automatically", () => t.AutoStartNext, v => t.AutoStartNext = v),
            Toggle("Sound when a session ends", () => t.Sound, v => t.Sound = v),
            Choice("Timer mascot", new[] { "🐢", "🐇", "🐱", "🦊", "🐧", "🚀", "🍅", "" }, () => t.Mascot, v => t.Mascot = v));
    }

    // ---------------- Screen Capture ----------------

    private UIElement Capture()
    {
        var c = S.Capture;
        var folder = Text(string.IsNullOrWhiteSpace(c.Folder) ? Paths.Screenshots : c.Folder, "Caption");
        folder.TextTrimming = TextTrimming.CharacterEllipsis;
        folder.VerticalAlignment = VerticalAlignment.Center;
        folder.Margin = new Thickness(8, 0, 0, 0);
        return Page(
            Note("Capture from the notch (Home toggles, the Shelf, Tools, the command palette or your shortcut): the screen freezes, drag a box, done. Enter takes the whole screen, Esc cancels."),
            Buttons(Chip("Take a screenshot now", (_, _) => { Close(); ToolsModule.ScreenCapture(); }, Glyphs.Crop, accent: true)),
            Choice("Capture with", new[] { ($"{AppInfo.Name} (built in)", "NotchX"), ("Windows Snipping Tool", "Snipping Tool") }, () => c.Method, v => c.Method = v,
                hint: "Snipping Tool copies to the clipboard itself and opens its editor"),
            Header("After a capture"),
            Toggle("Copy to the clipboard", () => c.CopyToClipboard, v => c.CopyToClipboard = v),
            Toggle("Put it on the Shelf", () => c.AddToShelf, v => c.AddToShelf = v),
            Toggle("Save to a folder", () => c.SaveToFolder, v => c.SaveToFolder = v),
            Row("Folder", new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    Chip("Choose…", (_, _) =>
                    {
                        var dlg = new OpenFolderDialog { Title = "Save screenshots to" };
                        if (dlg.ShowDialog(this) != true) return;
                        c.Folder = dlg.FolderName;
                        folder.Text = dlg.FolderName;
                        Changed();
                    }, Glyphs.Folder),
                    folder,
                },
            }),
            Toggle("Show a preview island (drag it into any app)", () => c.ShowPreview, v => c.ShowPreview = v),
            Field("Shortcut", () => c.Hotkey, v => c.Hotkey = v, "e.g. Ctrl+Shift+X — empty = none"),
            Header("Windows screenshots"),
            Toggle("Put Win+PrtScn / Snipping Tool saves on the Shelf", () => S.Huds.ScreenshotShelf, v => S.Huds.ScreenshotShelf = v, () => Notch.Modules.Restart("filealerts")));
    }

    // ---------------- Documents ----------------

    private UIElement Documents()
    {
        var d = S.Documents;
        var word = DocumentService.HasWord;
        var lo = DocumentService.LibreOffice;
        var found = word && lo != null ? "Microsoft Word and LibreOffice found."
            : word ? "Microsoft Word found."
            : lo != null ? "LibreOffice found."
            : "No converter found. " + DocumentService.InstallHint;
        return Page(
            Note("The Documents tab converts Word ⇄ PDF and edits PDFs. Conversions run on this PC with Microsoft Word or the free LibreOffice; PDF editing is built in."),
            Header("Converter", found),
            Choice("Convert with", new[] { ("Automatic (Word if installed, else LibreOffice)", "Auto"), ("Microsoft Word", "Word"), ("LibreOffice", "LibreOffice") },
                () => d.Converter, v => d.Converter = v, hint: "Word gives the best PDF → Word results"),
            Field("LibreOffice program", () => d.LibreOfficePath, v => d.LibreOfficePath = v, "Path to soffice.exe — leave empty to find it automatically"),
            lo == null && !word
                ? Buttons(Chip("Get LibreOffice (free)", (_, _) => Ui.OpenUrl("https://www.libreoffice.org/download/download-libreoffice/"), Glyphs.Download, accent: true))
                : new Border(),
            Header("Results"),
            Choice("Save results", new[] { ("Next to the original file", "Same folder"), ("In my Documents folder", "Documents") }, () => d.SaveTo, v => d.SaveTo = v),
            Toggle("Also put results on the Shelf", () => d.AddToShelf, v => d.AddToShelf = v),
            Toggle("Open results when done", () => d.OpenWhenDone, v => d.OpenWhenDone = v),
            Buttons(Chip("Open the Documents tab", (_, _) => Notch.Shell.OpenTab("documents"), Glyphs.Document)));
    }

    // ---------------- AI Usage ----------------

    private UIElement AiUsage()
    {
        var ai = S.AiUsage;
        var token = new PasswordBox { Password = Secrets.Unprotect(ai.CopilotTokenProtected) };
        token.LostFocus += (_, _) => { ai.CopilotTokenProtected = Secrets.Protect(token.Password); Changed(() => _ = Notch.AiUsage.RefreshAsync()); };
        return Page(
            Note("Claude Code and Codex are read from local logs — nothing leaves your PC."),
            Field("Sections (order)", () => string.Join(", ", ai.Sections), v => ai.Sections = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                "Any of: claude, codex, copilot, cursor"),
            Field("Claude projects folder", () => ai.ClaudeDir, v => ai.ClaudeDir = v, "Default: %USERPROFILE%\\.claude\\projects"),
            Field("Codex home", () => ai.CodexDir, v => ai.CodexDir = v, "Default: %USERPROFILE%\\.codex"),
            Slide("Claude 5-hour token budget (millions)", 0, 200, () => ai.ClaudeWindowTokenBudget / 1_000_000.0, v => ai.ClaudeWindowTokenBudget = (long)(v * 1_000_000), "0.#",
                hint: "Used for the progress ring and alerts. 0 = don't track."),
            Slide("Alert when a window reaches", 50, 100, () => ai.AlertAtPercent, v => ai.AlertAtPercent = (int)v, "0'%'"),
            Header("Closed notch", "A small chip like “Claude 42%” — green, orange at the alert level, red at the limit. Hover it for reset times; click to open AI Usage."),
            Toggle("Show usage in the closed notch", () => ai.ShowInNotch, v => ai.ShowInNotch = v),
            Indented(Page(Notch.AiUsage.ChipProviders.Select(p => Toggle(p.Name, () => ai.NotchProviders.Contains(p.Id), v =>
            {
                ai.NotchProviders.Remove(p.Id);
                if (v) ai.NotchProviders.Add(p.Id);
            }, tip: p.Available ? null : $"{p.Name} isn't set up on this PC yet, so its chip stays hidden")).ToArray())),
            Row("GitHub token (Copilot)", token, "Stored encrypted with Windows DPAPI"),
            Header("Prices", "Per-million-token prices used for cost estimates live in settings.json → AiUsage.Prices."));
    }

    // ---------------- Terminal ----------------

    private UIElement Terminal()
    {
        var b = S.Behavior;
        return Page(
            Note("Opens Windows Terminal from the notch (Tools tab, or “terminal” in the command palette). While it's open a Terminal chip shows on the pill."),
            Choice("Open as", new[] { ("Drop-down from the top edge", "Dropdown"), ("Normal window", "Window") }, () => b.TerminalStyle, v => b.TerminalStyle = v,
                hint: "A normal window has the usual minimise and close buttons"),
            Buttons(
                Chip("Show / hide terminal", (_, _) => Services.Terminal.Toggle(), Glyphs.Terminal, accent: true),
                Chip("Close terminal", (_, _) => Services.Terminal.Close(), Glyphs.Close)));
    }

    // ---------------- Cuely ----------------

    private UIElement Cuely()
    {
        var c = S.Cuely;
        return Page(
            Note("A teleprompter that scrolls your script right under the webcam, so your eyes stay on the camera. Paste the script in the Cuely tab."),
            Buttons(Chip("Open Cuely", (_, _) => Notch.Shell.OpenTab("cuely"), Glyphs.Reading, accent: true)),
            Slide("Scroll speed", 5, 120, () => c.Speed, v => c.Speed = v),
            Slide("Text size", 14, 54, () => c.TextSize, v => c.TextSize = v),
            Toggle("Mirror the text (for beam-splitter rigs)", () => c.Mirror, v => c.Mirror = v),
            Note("Changes apply the next time Cuely opens."));
    }

    // ---------------- Windows & Spaces ----------------

    private UIElement WindowsAndSpaces()
    {
        var b = S.Behavior;
        var current = Text($"You're on {Notch.Spaces.Name} ({Notch.Spaces.Position}).", "Caption");
        return Page(
            Header("Window snapping", "Drag a window up to the notch and drop it on a zone. Off by default because it overlaps Windows 11's own snap layouts."),
            Toggle("Window snapping zones in the notch", () => b.WindowSnapping, v => { b.WindowSnapping = v; Notch.Modules.SetEnabled("snap", v); }),
            Header("Spaces (virtual desktops)", "Add the Spaces card to Home to switch desktops from the notch."),
            current,
            Toggle("Show the desktop's name when you switch", () => S.Spaces.ShowIsland, v => S.Spaces.ShowIsland = v),
            Buttons(
                Chip("New desktop", (_, _) => Notch.Spaces.New(), Glyphs.Add),
                Chip("Task view", (_, _) => { Close(); Notch.Spaces.TaskView(); }, Glyphs.Apps),
                Chip("Add Spaces card to Home", (_, _) => { LayoutEditor.Add("spaces"); PreviewHome(); }, Glyphs.Home)));
    }

    // ---------------- Displays ----------------

    private UIElement Displays()
    {
        var a = S.Appearance;
        var screens = System.Windows.Forms.Screen.AllScreens;
        var options = new[] { ("Main display", "-1") }
            .Concat(screens.Select((s, i) => ($"Display {i + 1} — {s.Bounds.Width}×{s.Bounds.Height}{(s.Primary ? " (main)" : "")}", i.ToString())))
            .ToArray();
        var list = new StackPanel();
        foreach (var (s, i) in screens.Select((s, i) => (s, i)))
            list.Children.Add(Text($"Display {i + 1}: {s.Bounds.Width}×{s.Bounds.Height} at ({s.Bounds.Left}, {s.Bounds.Top}){(s.Primary ? " · main" : "")}", "Caption"));
        return Page(
            Header("Where the notch lives"),
            Choice("Display", options, () => a.DisplayIndex.ToString(), v => a.DisplayIndex = int.Parse(v)),
            list,
            Slide("Offset from top", 0, 60, () => a.TopOffset, v => a.TopOffset = v, hint: "Push the pill below a taskbar or bezel"),
            Toggle("Hide the pill while an app is fullscreen", () => S.Behavior.HideInFullscreen, v => S.Behavior.HideInFullscreen = v),
            Header("Brightness", Notch.Brightness.HasInternalPanel ? "Built-in screen found." : Notch.Brightness.HasExternal ? "Your monitor answers over DDC/CI." : "No adjustable display found — turn on DDC/CI in your monitor's on-screen menu."),
            Toggle("Also control external monitors over DDC/CI", () => S.Huds.ExternalMonitorBrightness, v => S.Huds.ExternalMonitorBrightness = v,
                tip: "Desktops without a built-in screen always use DDC/CI"),
            Buttons(Chip("Windows display settings", (_, _) => Ui.OpenUrl("ms-settings:display"), Glyphs.Monitor)));
    }

    // ---------------- Battery ----------------

    private UIElement BatteryPage()
    {
        var h = S.Huds;
        var b = Notch.Battery;
        return Page(
            Header(b.HasBattery ? $"Battery {b.Percent}% · {(b.Charging ? "charging" : b.PluggedIn ? "plugged in" : "on battery")}" : "This PC has no battery",
                b.HasBattery ? null : "These settings matter on laptops. Your Bluetooth devices' batteries show in the Battery and Bluetooth cards."),
            Toggle("Charging / unplugged islands", () => h.BatteryHud, v => h.BatteryHud = v),
            Slide("Full-charge alert at", 80, 100, () => h.FullChargeLevel, v => h.FullChargeLevel = (int)v, "0'%'"),
            Slide("Low-battery alert at", 5, 50, () => h.LowBatteryLevel, v => h.LowBatteryLevel = (int)v, "0'%'"),
            Toggle("Low-battery sound", () => h.LowBatterySound, v => h.LowBatterySound = v),
            Buttons(Chip("Windows power settings", (_, _) => Ui.OpenUrl("ms-settings:powersleep"), Glyphs.Bolt)));
    }

    // ---------------- Connectivity ----------------

    private UIElement Connectivity()
    {
        var h = S.Huds;
        return Page(
            Header("Bluetooth", "Windows doesn't let apps connect or disconnect audio devices, so device buttons open Windows' quick settings."),
            Toggle("AirPods popup when the case opens (scans Bluetooth LE)", () => h.AirPodsPopup, v => h.AirPodsPopup = v, () => Notch.Modules.Restart("devices")),
            Buttons(
                Chip("Bluetooth quick settings", (_, _) => BluetoothService.OpenQuickConnect(), Glyphs.Bluetooth),
                Chip("Bluetooth settings", (_, _) => BluetoothService.OpenBluetoothSettings(), Glyphs.Settings)),
            Header("Audio"),
            Toggle("Island when the sound output changes", () => h.OutputSwitchIsland, v => h.OutputSwitchIsland = v),
            Header("USB drives"),
            Toggle("Island when a drive is plugged in, with Eject", () => h.DriveAlerts, v => h.DriveAlerts = v, () => Notch.Modules.Restart("filealerts")));
    }

    // ---------------- Caffeine ----------------

    private UIElement Caffeine()
    {
        var k = S.KeepAwake;
        var status = Text("", "Title", 14);
        void Update() => status.Text = Notch.KeepAwake.IsActive ? $"Caffeine is on — {Notch.KeepAwake.Label.ToLowerInvariant()}" : "Caffeine is off";
        Update();
        Notch.KeepAwake.PropertyChanged += (_, _) => Update();
        return Page(
            Note("Keeps your PC (and screen) awake — for downloads, presentations or long builds."),
            status,
            Buttons(
                Chip("On", (_, _) => Notch.KeepAwake.Enable(null), Glyphs.Bolt, accent: true),
                Chip("30 min", (_, _) => Notch.KeepAwake.Enable(TimeSpan.FromMinutes(30))),
                Chip("1 hour", (_, _) => Notch.KeepAwake.Enable(TimeSpan.FromHours(1))),
                Chip("3 hours", (_, _) => Notch.KeepAwake.Enable(TimeSpan.FromHours(3))),
                Chip("Off", (_, _) => Notch.KeepAwake.Disable())),
            Header("Options"),
            Toggle("Keep the screen on too", () => k.KeepDisplayOn, v => k.KeepDisplayOn = v, tip: "Off = the PC stays awake but the screen may turn off"),
            Toggle("Turn off automatically below 10% battery", () => k.AutoOffOnLowBattery, v => k.AutoOffOnLowBattery = v),
            Note("Tip: middle-click the tray icon to toggle Caffeine."));
    }

    // ---------------- Permissions ----------------

    private UIElement Permissions()
    {
        UIElement Status(string label, string state, bool ok, string? settingsUri, string? hint = null)
        {
            var badge = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 2, 8, 2),
                Background = ok ? new SolidColorBrush(Color.FromArgb(0x33, 0x30, 0xD1, 0x58)) : new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0x45, 0x3A)),
                Child = new TextBlock { Text = state, FontSize = 11, Foreground = ok ? Ui.Green : Ui.Red },
                VerticalAlignment = VerticalAlignment.Center,
            };
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { Text(label, "Body") } };
            if (hint != null) { var t = Text(hint, "Caption", 10); t.TextWrapping = TextWrapping.Wrap; info.Children.Add(t); }
            var open = settingsUri == null ? (UIElement)new Border() : Chip("Open", (_, _) => Ui.OpenUrl(settingsUri), Glyphs.Settings);
            return Card(Columns((info, Star()), (badge, Auto), (new Border { Width = 8 }, Auto), (open, Auto)), new Thickness(0, 0, 0, 6));
        }
        (string, bool) Consent(string capability)
        {
            try
            {
                var basePath = $@"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\{capability}";
                using var all = Registry.CurrentUser.OpenSubKey(basePath);
                using var desktop = Registry.CurrentUser.OpenSubKey(basePath + @"\NonPackaged");
                using var machine = Registry.LocalMachine.OpenSubKey(basePath);
                var denied = new[] { all, desktop, machine }.Any(k => (k?.GetValue("Value") as string) == "Deny");
                return denied ? ("Blocked", false) : ("Allowed", true);
            }
            catch { return ("Unknown", false); }
        }
        var (cam, camOk) = Consent("webcam");
        var (mic, micOk) = Consent("microphone");
        var (loc, locOk) = Consent("location");
        var api = Notch.DeveloperApi.IsRunning;
        return Page(
            Note($"{AppInfo.Name} is a normal desktop app: it only asks Windows for what a feature needs, and nothing leaves your PC unless you turn it on (weather, lyrics, Copilot usage)."),
            Status("Camera", cam, camOk, "ms-settings:privacy-webcam", "Camera card and Cuely's mirror. “Let desktop apps access your camera” must be on."),
            Status("Microphone", mic, micOk, "ms-settings:privacy-microphone", "Only used to show which apps are using the mic; the mute button works either way."),
            Status("Location", loc, locOk, "ms-settings:privacy-location", "Weather without a city set. Setting a city in Settings › Weather avoids this."),
            Status("Bluetooth", "Windows settings", true, "ms-settings:bluetooth", "Device list and battery levels; AirPods popup scans nearby Bluetooth LE when turned on."),
            Status("Start with Windows", S.Behavior.StartWithWindows ? "On" : "Off", S.Behavior.StartWithWindows, "ms-settings:startupapps"),
            Status("Hidden from screen capture", S.Behavior.HideFromScreenCapture ? "Hidden" : "Visible", true, null, "Settings › Behaviour"),
            Status("Local developer API", api ? "Listening on 127.0.0.1" : "Off", true, null, "Settings › Developer"));
    }

    // ---------------- Developer ----------------

    private UIElement Developer()
    {
        var d = S.Developer;
        var tokenText = Text(d.Token, "Body");
        tokenText.FontFamily = (FontFamily)FindResource("MonoFont");
        void Restart() => Notch.Modules.Restart("devapi");
        var status = Text(Notch.DeveloperApi.IsRunning ? "Listening" : Notch.DeveloperApi.Error ?? "Off", "Caption");
        return Page(
            Header("Local API", "Listens on 127.0.0.1 only, with a per-install token. Off by default."),
            Toggle("Enable the developer API", () => d.ApiEnabled, v => d.ApiEnabled = v, Restart),
            status,
            Field("Port", () => d.Port.ToString(), v => { if (int.TryParse(v, out var port) && port is > 1023 and < 65536) d.Port = port; }, after: Restart),
            Row("Token", Buttons(
                Chip("Copy", (_, _) => Clipboard.SetText(d.Token), Glyphs.Copy),
                Chip("Regenerate", (_, _) =>
                {
                    d.Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
                    tokenText.Text = d.Token;
                    Changed();
                }, Glyphs.Refresh))),
            tokenText,
            Header("Agents", $"Claude Code calls {AppInfo.Name} directly via “http” hooks. Paste the config below into ~/.claude/settings.json."),
            Toggle("Approve / deny Claude Code permission prompts from the notch", () => d.AgentApprovals, v => d.AgentApprovals = v),
            Toggle("Show agent turns and per-turn cost", () => d.AgentActivity, v => d.AgentActivity = v),
            Buttons(
                Chip("Copy Claude Code hooks config", (_, _) =>
                {
                    Clipboard.SetText(DeveloperApiService.ClaudeHookConfig());
                    Notch.Hub.Notify(Glyphs.Copy, "Hooks config copied", "Merge it into ~/.claude/settings.json", Ui.Green);
                }, Glyphs.Copy, accent: true),
                Chip("Docs", (_, _) => Ui.OpenUrl(AppInfo.RepoUrl + "/blob/main/docs/DEVELOPER_API.md"), Glyphs.Info)),
            Header("Shell activity", "Long-running PowerShell commands become live islands. Add hooks\\notchify-shell.ps1 to your $PROFILE (see docs)."),
            Header("Log"),
            Buttons(Chip("View log", (_, _) => Ui.OpenUrl(Log.FilePath), Glyphs.Document)));
    }

    // ---------------- About ----------------

    private UIElement About()
    {
        var logo = new System.Windows.Controls.Image { Width = 72, Height = 72, Margin = new Thickness(0, 0, 16, 0) };
        try { logo.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/NotchX-256.png")); } catch { }
        var title = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                Text(AppInfo.Name, "Big", 26),
                Text($"Version {AppInfo.Version}", "Caption"),
                Text($"Developed by {AppInfo.Developer}", "Body"),
            },
        };
        return Page(
            new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 8), Children = { logo, title } },
            Note("An open-source, fully customisable Dynamic-Island-style notch for Windows. MIT licensed."),
            Buttons(
                Chip("Project page", (_, _) => Ui.OpenUrl(AppInfo.RepoUrl), Glyphs.Globe),
                Chip("Open data folder", (_, _) => Ui.OpenUrl(Paths.Root), Glyphs.Folder),
                Chip("View log", (_, _) => Ui.OpenUrl(Log.FilePath), Glyphs.Document)),
            Header("Credits", "Lyrics by LRCLIB · Weather by Open-Meteo · Audio via NAudio. Inspired by Notchy for macOS."),
            Note($"© 2026 {AppInfo.Developer}."));
    }

    // ---------------- Quit ----------------

    private UIElement Quit()
    {
        return Page(
            Note($"Quitting removes the notch until you start {AppInfo.Name} again (from the Start menu, or automatically at sign-in if “Start with Windows” is on)."),
            Buttons(
                Chip($"Quit {AppInfo.Name}", (_, _) => Application.Current.Shutdown(), Glyphs.Close, accent: true),
                Chip($"Restart {AppInfo.Name}", (_, _) => App.Restart(), Glyphs.Refresh)));
    }
}
