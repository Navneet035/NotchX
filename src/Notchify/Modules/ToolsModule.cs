using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Notchify.Core;
using Notchify.Services;
using static Notchify.Views.ViewKit;

namespace Notchify.Modules;

/// <summary>Utility belt: color eyedropper, screen capture, keyboard-cleaning lock, terminal, keystroke HUD.</summary>
public sealed class ToolsModule : NotchModule
{
    public override string Id => "tools";
    public override string Title => "Tools";
    public override string Glyph => Glyphs.Settings;
    public override string Description => "Color eyedropper, screen capture with OCR, keyboard cleaning lock, drop-down terminal and more.";

    public static DateTime LastCapture { get; internal set; }

    /// <summary>Region capture; the shot goes to the clipboard (and shelf / folder) per Settings › Screen capture.</summary>
    public static void ScreenCapture() => ScreenCaptureService.Start();

    public static void Eyedropper() => new EyedropperSession().Start();

    // ---------------- Keyboard cleaning lock ----------------

    private static IDisposable? _lock;

    public static void LockKeyboard(int seconds = 60)
    {
        if (_lock != null) return;
        Notch.Shell.Collapse();
        var ctrl = false;
        var ends = DateTime.Now.AddSeconds(seconds);
        var island = new Island
        {
            Key = "kb-lock",
            Glyph = Glyphs.Lock,
            Title = "Keyboard locked for cleaning",
            Message = "Press Ctrl+Esc to unlock",
            Accent = Ui.Teal,
            Sticky = true,
            Priority = IslandPriority.Critical,
            Progress = 1,
            Actions = { new IslandAction("Unlock", UnlockKeyboard, true, Glyphs.Unlock) },
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            var left = ends - DateTime.Now;
            if (left <= TimeSpan.Zero || _lock == null) { timer.Stop(); UnlockKeyboard(); return; }
            island.Progress = left.TotalSeconds / seconds;
            island.Message = $"Ctrl+Esc to unlock · {left.Seconds + (int)left.TotalMinutes * 60}s";
        };
        _lock = Notch.Input.Subscribe(k =>
        {
            if (k.VirtualKey is 0xA2 or 0xA3 or 0x11) ctrl = k.IsDown;
            if (k.IsDown && ctrl && k.VirtualKey == Native.VK_ESCAPE) Ui.Post(UnlockKeyboard);
            return true; // swallow every key
        });
        Notch.Hub.Show(island);
        timer.Start();
    }

    public static void UnlockKeyboard()
    {
        if (_lock == null) return;
        _lock.Dispose();
        _lock = null;
        Notch.Hub.DismissByKey("kb-lock");
        Notch.Hub.Notify(Glyphs.Unlock, "Keyboard unlocked", null, Ui.Green, IslandPriority.High, 1.5);
    }

    public static void OpenTerminal() => Terminal.Toggle();

    public override FrameworkElement CreateView()
    {
        UIElement Tile(string glyph, string title, string desc, Action run)
        {
            var b = new Button { Style = S("ChipButton"), HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 8, 8) };
            var desc1 = Text(desc, "Caption");
            desc1.TextWrapping = TextWrapping.Wrap;
            b.Content = Columns((Icon(glyph, 18), Px(34)), (new StackPanel { Children = { Text(title, "Title", 13), desc1 } }, Star()));
            b.Click += (_, _) => run();
            return b;
        }

        var grid = new UniformGrid { Columns = 2 };
        grid.Children.Add(Tile(Glyphs.Color, "Color eyedropper", "Click any pixel — the hex is copied", Eyedropper));
        grid.Children.Add(Tile(Glyphs.Crop, "Screen capture", "Region → clipboard, OCR and the shelf", ScreenCapture));
        grid.Children.Add(Tile(Glyphs.Lock, "Clean keyboard", "Lock all keys for 60 s (Ctrl+Esc unlocks)", () => LockKeyboard()));
        grid.Children.Add(TerminalTile());

        var keystroke = Switch("Keystroke HUD (show keypresses for demos)", SettingsStore.Current.Huds.KeystrokeHud, v =>
        {
            SettingsStore.Current.Huds.KeystrokeHud = v;
            SettingsStore.NotifyChanged();
            Notch.Modules.Restart("huds");
        }, "Password fields are never captured");
        var snap = Switch("Window snapping zones in the notch", SettingsStore.Current.Behavior.WindowSnapping, v =>
        {
            SettingsStore.Current.Behavior.WindowSnapping = v;
            Notch.Modules.SetEnabled("snap", v);
            SettingsStore.NotifyChanged();
        }, "Drag a window to the notch and drop it on a zone");

        var terminalWindow = Switch("Open the terminal as a normal window (instead of drop-down)",
            SettingsStore.Current.Behavior.TerminalStyle == "Window", v =>
            {
                SettingsStore.Current.Behavior.TerminalStyle = v ? "Window" : "Dropdown";
                SettingsStore.NotifyChanged();
            }, "A normal window has the usual minimise and close buttons");

        var stack = new StackPanel();
        stack.Children.Add(grid);
        stack.Children.Add(keystroke);
        stack.Children.Add(snap);
        stack.Children.Add(terminalWindow);
        return Scroll(stack);
    }

    /// <summary>Terminal tile: the tile toggles show/hide; a close button appears while it's running.</summary>
    private static UIElement TerminalTile()
    {
        var title = Text("Terminal", "Title", 13);
        var desc = Text("", "Caption");
        desc.TextWrapping = TextWrapping.Wrap;
        var close = new Button { Style = S("IconButton"), Content = Glyphs.Close, ToolTip = "Close the terminal", VerticalAlignment = VerticalAlignment.Top };
        close.Click += (_, e) => { Terminal.Close(); e.Handled = true; };

        var b = new Button { Style = S("ChipButton"), HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(12, 10, 8, 10), Margin = new Thickness(0, 0, 8, 8) };
        b.Content = Columns((Icon(Glyphs.Terminal, 18), Px(34)), (new StackPanel { Children = { title, desc } }, Star()), (close, Auto));
        b.Click += (_, _) => Terminal.Toggle();

        void Refresh()
        {
            var running = Terminal.IsRunning;
            close.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            title.Text = !running ? "Terminal" : Terminal.IsShown ? "Hide terminal" : "Show terminal";
            desc.Text = !running
                ? (SettingsStore.Current.Behavior.TerminalStyle == "Window" ? "Open Windows Terminal" : "Drop-down Windows Terminal from the top edge")
                : "Click to " + (Terminal.IsShown ? "hide" : "bring it back") + " · ✕ closes it";
        }
        Terminal.Changed += () => Ui.Post(Refresh);
        SettingsStore.Changed += Refresh;
        b.IsVisibleChanged += (_, _) => { if (b.IsVisible) Refresh(); };
        Refresh();
        return b;
    }

    public override IEnumerable<PaletteCommand> Commands => new[]
    {
        new PaletteCommand("Pick a colour from the screen", Glyphs.Color, Eyedropper, null, "eyedropper color picker hex"),
        new PaletteCommand("Capture screen region", Glyphs.Crop, ScreenCapture, null, "screenshot snip ocr"),
        new PaletteCommand("Lock keyboard for cleaning", Glyphs.Lock, () => LockKeyboard(), null, "clean wipe"),
        new PaletteCommand("Show / hide terminal", Glyphs.Terminal, OpenTerminal, null, "shell console powershell drop-down quake"),
        new PaletteCommand("Close terminal", Glyphs.Close, Terminal.Close, null, "shell console powershell quit exit"),
    };
}

/// <summary>
/// Eyedropper: a magnifier loupe follows the cursor; click to copy the hex. Uses plain GDI pixel reads,
/// so no screen-recording permission is involved. Esc or right-click cancels.
/// </summary>
internal sealed class EyedropperSession
{
    private Window? _loupe;
    private Image? _zoom;
    private TextBlock? _label;
    private Border? _swatch;
    private DispatcherTimer? _timer;
    private IntPtr _mouseHook;
    private Native.HookProc? _proc;
    private IDisposable? _keys;

    public void Start()
    {
        Notch.Shell.Collapse();
        _zoom = new Image { Width = 110, Height = 110, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(_zoom, BitmapScalingMode.NearestNeighbor);
        _label = new TextBlock { Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _swatch = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), BorderBrush = Brushes.White, BorderThickness = new Thickness(1) };
        var crosshair = new Border { Width = 10, Height = 10, BorderBrush = Brushes.White, BorderThickness = new Thickness(1.5), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var stack = new StackPanel();
        stack.Children.Add(new Border { CornerRadius = new CornerRadius(55), ClipToBounds = true, Child = new Grid { Children = { _zoom, crosshair } }, Width = 110, Height = 110 });
        stack.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0), Children = { _swatch, _label } });
        _loupe = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, Topmost = true,
            ShowInTaskbar = false, ShowActivated = false, SizeToContent = SizeToContent.WidthAndHeight, IsHitTestVisible = false,
            Content = new Border { Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x10, 0x10, 0x12)), CornerRadius = new CornerRadius(16), Padding = new Thickness(8), Child = stack },
        };
        _loupe.SourceInitialized += (_, _) =>
        {
            var h = new System.Windows.Interop.WindowInteropHelper(_loupe).Handle;
            Native.SetWindowLong(h, Native.GWL_EXSTYLE, Native.GetWindowLong(h, Native.GWL_EXSTYLE) | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            Native.SetWindowDisplayAffinity(h, Native.WDA_EXCLUDEFROMCAPTURE); // keep the loupe out of its own sample
        };
        _loupe.Show();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _timer.Tick += (_, _) => Update();
        _timer.Start();

        _proc = MouseProc;
        _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _proc, Native.GetModuleHandle(null), 0);
        _keys = Notch.Input.Subscribe(k =>
        {
            if (k.VirtualKey != Native.VK_ESCAPE || !k.IsDown) return false;
            Ui.Post(() => Finish(null));
            return true;
        });
    }

    private Color _current;

    private void Update()
    {
        if (!Native.GetCursorPos(out var p) || _loupe == null) return;
        const int r = 5; // 11×11 pixels magnified
        using var bmp = new System.Drawing.Bitmap(r * 2 + 1, r * 2 + 1);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
            g.CopyFromScreen(p.X - r, p.Y - r, 0, 0, bmp.Size);
        var c = bmp.GetPixel(r, r);
        _current = Color.FromRgb(c.R, c.G, c.B);
        var hbmp = bmp.GetHbitmap();
        try
        {
            var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            _zoom!.Source = src;
        }
        finally { DeleteObject(hbmp); }
        _swatch!.Background = new SolidColorBrush(_current);
        _label!.Text = Hex(_current);

        var source = PresentationSource.FromVisual(_loupe);
        var scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        _loupe.Left = p.X / scale + 24;
        _loupe.Top = p.Y / scale + 24;
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var msg = (int)wParam;
            if (msg == Native.WM_LBUTTONDOWN) { Ui.Post(() => Finish(_current)); return (IntPtr)1; }
            if (msg == Native.WM_RBUTTONDOWN) { Ui.Post(() => Finish(null)); return (IntPtr)1; }
            if (msg == Native.WM_LBUTTONUP) return (IntPtr)1;
        }
        return Native.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private void Finish(Color? picked)
    {
        _timer?.Stop();
        if (_mouseHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        _keys?.Dispose();
        _loupe?.Close();
        _loupe = null;
        if (picked is not { } c) return;
        var hex = Hex(c);
        Notch.Clipboard.SetTextSilently(hex);
        Notch.Hub.Show(new Island
        {
            Key = "eyedropper",
            Glyph = Glyphs.Color,
            Title = hex,
            Message = $"rgb({c.R}, {c.G}, {c.B}) · copied",
            Accent = new SolidColorBrush(c),
            Duration = TimeSpan.FromSeconds(4),
            Actions = { new IslandAction("Copy RGB", () => Notch.Clipboard.SetTextSilently($"rgb({c.R}, {c.G}, {c.B})")) },
        });
    }

    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
}
