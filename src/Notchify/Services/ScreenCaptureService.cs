using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Rectangle = System.Windows.Shapes.Rectangle;
using ShapePath = System.Windows.Shapes.Path;
using System.Windows.Threading;
using Notchify.Core;
using Notchify.Modules;

namespace Notchify.Services;

/// <summary>
/// Built-in region capture: the screen freezes, you drag a rectangle, and the shot goes straight to the
/// clipboard (and the shelf / a folder, as set in Settings › Screen capture). Works on every monitor.
/// "Snipping Tool" mode hands over to Windows' own capture instead.
/// </summary>
public static class ScreenCaptureService
{
    private static readonly List<CaptureOverlay> Overlays = new();
    private static bool _active;
    private static DateTime _endedAt;

    /// <summary>
    /// True while the capture screen is up and for a few seconds after. Windows takes the full-screen overlay
    /// for a full-screen app and switches on automatic Do Not Disturb, which shouldn't be announced.
    /// </summary>
    public static bool IsCapturing => _active || DateTime.Now - _endedAt < TimeSpan.FromSeconds(8);

    public static void Start()
    {
        var s = SettingsStore.Current.Capture;
        Notch.Shell.Collapse();
        ToolsModule.LastCapture = DateTime.Now;
        if (s.Method == "Snipping Tool")
        {
            // Snipping Tool copies to the clipboard itself; the clipboard listener stages it on the shelf.
            Notch.Clipboard.NextImageToShelf = s.AddToShelf;
            Ui.OpenUrl("ms-screenclip:");
            return;
        }
        if (_active) return;
        _active = true;
        // Let the notch finish collapsing before the screen is frozen.
        var wait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
        wait.Tick += (_, _) => { wait.Stop(); Freeze(); };
        wait.Start();
    }

    private static void Freeze()
    {
        BitmapSource full;
        System.Drawing.Rectangle vs;
        var hidden = HideNotchFromCapture();
        try
        {
            vs = System.Windows.Forms.SystemInformation.VirtualScreen;
            using var bmp = new System.Drawing.Bitmap(vs.Width, vs.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
                g.CopyFromScreen(vs.Left, vs.Top, 0, 0, bmp.Size);
            var hbmp = bmp.GetHbitmap();
            try { full = Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); }
            finally { DeleteObject(hbmp); }
            full.Freeze();
        }
        catch (Exception ex)
        {
            Log.Error("screen capture", ex);
            _active = false;
            _endedAt = DateTime.Now;
            return;
        }
        finally { RestoreCapture(hidden); }

        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            var b = screen.Bounds;
            var crop = new CroppedBitmap(full, new Int32Rect(b.Left - vs.Left, b.Top - vs.Top, b.Width, b.Height));
            crop.Freeze();
            var overlay = new CaptureOverlay(b, crop, Finish);
            Overlays.Add(overlay);
            overlay.Show();
        }
        // Keyboard (Esc / Enter) goes to the screen under the mouse.
        if (Native.GetCursorPos(out var p))
            Overlays.FirstOrDefault(o => o.Bounds.Contains(p.X, p.Y))?.Activate();
    }

    /// <summary>Keep the notch (and its glass) out of the frozen frame.</summary>
    private static List<IntPtr> HideNotchFromCapture()
    {
        var list = new List<IntPtr>();
        foreach (Window w in Application.Current.Windows)
        {
            if (w is not Shell.NotchWindow && w.Title != "NotchX glass") continue;
            var h = new WindowInteropHelper(w).Handle;
            if (h == IntPtr.Zero) continue;
            Native.SetWindowDisplayAffinity(h, Native.WDA_EXCLUDEFROMCAPTURE);
            list.Add(h);
        }
        return list;
    }

    private static void RestoreCapture(List<IntPtr> windows)
    {
        var keep = SettingsStore.Current.Behavior.HideFromScreenCapture;
        foreach (var h in windows) Native.SetWindowDisplayAffinity(h, keep ? Native.WDA_EXCLUDEFROMCAPTURE : Native.WDA_NONE);
    }

    private static void Finish(BitmapSource? shot)
    {
        foreach (var o in Overlays.ToList()) o.Close();
        Overlays.Clear();
        _active = false;
        _endedAt = DateTime.Now;
        if (shot == null) return;

        var s = SettingsStore.Current.Capture;
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH.mm.ss");
        string? saved = null, shelf = null;
        try
        {
            if (s.SaveToFolder)
            {
                var dir = string.IsNullOrWhiteSpace(s.Folder) ? Paths.Screenshots : s.Folder;
                Directory.CreateDirectory(dir);
                saved = System.IO.Path.Combine(dir, $"Screenshot {stamp}.png");
                ClipboardService.SavePng(shot, saved);
            }
            if (s.AddToShelf)
            {
                shelf = System.IO.Path.Combine(Paths.Shelf, $"Capture {stamp}.png");
                ClipboardService.SavePng(shot, shelf);
                Notch.Shelf.Add(new[] { shelf });
            }
        }
        catch (Exception ex) { Log.Error("save screenshot", ex); }

        var copied = s.CopyToClipboard && CopyToClipboard(shot);

        if (!s.ShowPreview) return;
        var file = shelf ?? saved;
        if (file == null)
        {
            // Something to drag out or open even when nothing was kept.
            file = System.IO.Path.Combine(Paths.Cache, "last-capture.png");
            try { ClipboardService.SavePng(shot, file); } catch { file = null; }
        }
        var where = new List<string>();
        if (copied) where.Add("copied");
        if (shelf != null) where.Add("on the shelf");
        if (saved != null) where.Add("saved");
        var island = new Island
        {
            Key = "capture",
            Glyph = Glyphs.Crop,
            Title = copied ? "Screenshot copied" : "Screenshot",
            Message = $"{shot.PixelWidth} × {shot.PixelHeight} · " + (where.Count > 0 ? string.Join(", ", where) : "not kept"),
            Image = shot,
            Accent = Ui.Teal,
            Duration = TimeSpan.FromSeconds(5),
            DragFiles = file != null ? new[] { file } : null,
            OpenTab = shelf != null ? "shelf" : null,
        };
        if (file != null) island.Actions.Add(new IslandAction("Open", () => Ui.OpenUrl(file)));
        if (saved != null) island.Actions.Add(new IslandAction("Show in folder", () => Ui.RevealInExplorer(saved), false, Glyphs.Folder));
        Notch.Hub.Show(island);
    }

    /// <summary>The clipboard is sometimes briefly locked by another app; retry a few times.</summary>
    private static bool CopyToClipboard(BitmapSource shot)
    {
        for (var i = 0; i < 5; i++)
        {
            try { Clipboard.SetImage(shot); return true; }
            catch { Thread.Sleep(60); }
        }
        Notch.Hub.Notify(Glyphs.Warning, "Couldn't copy the screenshot", "Another app is holding the clipboard", Ui.Red);
        return false;
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);

    /// <summary>One frozen monitor: drag to select, Esc or right-click cancels, Enter takes the whole screen.</summary>
    private sealed class CaptureOverlay : Window
    {
        private readonly BitmapSource _image;
        private readonly Action<BitmapSource?> _done;
        private readonly Canvas _canvas = new();
        private readonly ShapePath _dim;
        private readonly Rectangle _frame;
        private readonly Border _label;
        private readonly TextBlock _labelText;
        private Point? _start;
        private Rect _selection;
        private bool _finished;

        public System.Drawing.Rectangle Bounds { get; }

        public CaptureOverlay(System.Drawing.Rectangle bounds, BitmapSource image, Action<BitmapSource?> done)
        {
            Bounds = bounds;
            _image = image;
            _done = done;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            Background = Brushes.Black;
            Cursor = Cursors.Cross;
            Left = -32000; Top = -32000; Width = 10; Height = 10;
            Title = "NotchX capture";

            _dim = new ShapePath { Fill = new SolidColorBrush(Color.FromArgb(0x88, 0, 0, 0)), IsHitTestVisible = false };
            _frame = new Rectangle { Stroke = Ui.Accent, StrokeThickness = 1.5, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
            _labelText = new TextBlock { Foreground = Brushes.White, FontSize = 11 };
            _label = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x10, 0x10, 0x12)),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 2, 6, 2),
                Child = _labelText,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
            };
            var hint = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x10, 0x10, 0x12)),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(14, 7, 14, 7),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 60, 0, 0),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Foreground = Brushes.White, FontSize = 13,
                    Text = "Drag to capture  ·  Enter = whole screen  ·  Esc = cancel",
                },
            };
            Content = new Grid
            {
                Children =
                {
                    new Image { Source = image, Stretch = Stretch.Fill },
                    _dim,
                    _canvas,
                    hint,
                },
            };
            _canvas.Children.Add(_frame);
            _canvas.Children.Add(_label);

            SourceInitialized += (_, _) => Place();
            // A move onto a monitor with a different DPI makes WPF rescale the window; place it again once shown.
            ContentRendered += (_, _) => { Place(); UpdateDim(); };
            SizeChanged += (_, _) => UpdateDim();
            MouseLeftButtonDown += (_, e) => { _start = e.GetPosition(this); CaptureMouse(); };
            MouseMove += (_, e) => { if (_start is { } s) Select(s, e.GetPosition(this)); };
            MouseLeftButtonUp += (_, _) =>
            {
                ReleaseMouseCapture();
                if (_start == null) return;
                _start = null;
                if (_selection.Width >= 4 && _selection.Height >= 4) Complete(Crop(_selection));
                else { _selection = Rect.Empty; _frame.Visibility = Visibility.Collapsed; _label.Visibility = Visibility.Collapsed; UpdateDim(); }
            };
            MouseRightButtonUp += (_, _) => Complete(null);
            KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) Complete(null);
                else if (e.Key == Key.Enter) Complete(_image);
            };
            Deactivated += (_, _) => { /* stay until Esc / a selection, even if focus moves to another monitor's overlay */ };
        }

        private void Place()
        {
            var h = new WindowInteropHelper(this).Handle;
            Native.SetWindowPos(h, Native.HWND_TOPMOST, Bounds.Left, Bounds.Top, Bounds.Width, Bounds.Height, Native.SWP_SHOWWINDOW);
        }

        private void Select(Point a, Point b)
        {
            _selection = new Rect(a, b);
            Canvas.SetLeft(_frame, _selection.X);
            Canvas.SetTop(_frame, _selection.Y);
            _frame.Width = _selection.Width;
            _frame.Height = _selection.Height;
            _frame.Visibility = Visibility.Visible;
            var px = ToPixels(_selection);
            _labelText.Text = $"{px.Width} × {px.Height}";
            Canvas.SetLeft(_label, _selection.X);
            Canvas.SetTop(_label, Math.Max(0, _selection.Y - 24));
            _label.Visibility = Visibility.Visible;
            UpdateDim();
        }

        private void UpdateDim()
        {
            var all = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
            _dim.Data = _selection.IsEmpty || _selection.Width < 1
                ? all
                : new CombinedGeometry(GeometryCombineMode.Exclude, all, new RectangleGeometry(_selection));
        }

        private Int32Rect ToPixels(Rect r)
        {
            var sx = _image.PixelWidth / Math.Max(1, ActualWidth);
            var sy = _image.PixelHeight / Math.Max(1, ActualHeight);
            var x = (int)Math.Round(r.X * sx);
            var y = (int)Math.Round(r.Y * sy);
            var w = (int)Math.Round(r.Width * sx);
            var hgt = (int)Math.Round(r.Height * sy);
            x = Math.Clamp(x, 0, _image.PixelWidth - 1);
            y = Math.Clamp(y, 0, _image.PixelHeight - 1);
            return new Int32Rect(x, y, Math.Clamp(w, 1, _image.PixelWidth - x), Math.Clamp(hgt, 1, _image.PixelHeight - y));
        }

        private BitmapSource Crop(Rect r)
        {
            var crop = new CroppedBitmap(_image, ToPixels(r));
            crop.Freeze();
            return crop;
        }

        private void Complete(BitmapSource? shot)
        {
            if (_finished) return;
            _finished = true;
            _done(shot);
        }
    }
}
