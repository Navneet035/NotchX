using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Notchify.Core;

namespace Notchify.Shell;

/// <summary>
/// The frosted glass behind the open notch. The notch window itself is a big transparent (layered) window,
/// so a system blur applied to it would frost the whole invisible rectangle. Instead this small, click-through
/// window sits directly beneath the pill, is clipped to the pill's rounded shape with a window region, and
/// asks DWM to blur whatever is behind it. The pill then paints a translucent tint on top.
/// </summary>
internal sealed class GlassBackdrop
{
    private readonly Window _window;
    private IntPtr _hwnd;
    private bool _shown;
    private Native.RECT _lastRect;
    private CornerRadius _lastRadius;

    public GlassBackdrop()
    {
        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            Width = 1, Height = 1, Left = -10000, Top = -10000,
            Title = "NotchX glass",
        };
        _window.SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(_window).Handle;
            var ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT);
            EnableBlur(_hwnd);
        };
    }

    /// <summary>True when Windows "Transparency effects" are on (Settings › Personalisation › Colours).</summary>
    public static bool SystemAllowsBlur
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("EnableTransparency") is not int v || v != 0;
            }
            catch { return true; }
        }
    }

    public void SetExcludedFromCapture(bool exclude)
    {
        EnsureCreated();
        Native.SetWindowDisplayAffinity(_hwnd, exclude ? Native.WDA_EXCLUDEFROMCAPTURE : Native.WDA_NONE);
    }

    /// <summary>Place the blur exactly under <paramref name="pill"/> (device pixels), just below the notch window.</summary>
    public void Show(IntPtr notch, FrameworkElement pill, CornerRadius radius)
    {
        EnsureCreated();
        if (pill.ActualWidth < 2 || pill.ActualHeight < 2 || PresentationSource.FromVisual(pill) == null) { Hide(); return; }
        Point tl, br;
        try
        {
            tl = pill.PointToScreen(new Point(0, 0));
            br = pill.PointToScreen(new Point(pill.ActualWidth, pill.ActualHeight));
        }
        catch { Hide(); return; }
        var rect = new Native.RECT { Left = (int)Math.Round(tl.X), Top = (int)Math.Round(tl.Y), Right = (int)Math.Round(br.X), Bottom = (int)Math.Round(br.Y) };
        var scale = rect.Width / Math.Max(1, pill.ActualWidth);

        Native.SetWindowPos(_hwnd, notch, rect.Left, rect.Top, rect.Width, rect.Height,
            Native.SWP_NOACTIVATE | (_shown ? 0 : Native.SWP_SHOWWINDOW));
        if (!_shown || rect.Width != _lastRect.Width || rect.Height != _lastRect.Height || radius != _lastRadius)
            ApplyRegion(rect.Width, rect.Height, radius, scale);
        _lastRect = rect;
        _lastRadius = radius;
        _shown = true;
    }

    /// <summary>Re-assert the z-order (just under the notch) after something else went top-most.</summary>
    public void KeepBelow(IntPtr notch)
    {
        if (_shown && _hwnd != IntPtr.Zero)
            Native.SetWindowPos(_hwnd, notch, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    public void Hide()
    {
        if (!_shown || _hwnd == IntPtr.Zero) return;
        Native.ShowWindow(_hwnd, 0); // SW_HIDE
        _shown = false;
    }

    public void Close()
    {
        try { _window.Close(); } catch { }
    }

    private void EnsureCreated()
    {
        if (_hwnd != IntPtr.Zero) return;
        _window.Show();
        Native.ShowWindow(_hwnd, 0);
    }

    /// <summary>Rounded window region. Each corner can differ (the "Notch" style has square top corners).</summary>
    private void ApplyRegion(int w, int h, CornerRadius r, double scale)
    {
        int Px(double v) => (int)Math.Round(Math.Min(v * scale, Math.Min(w, h) / 2.0));
        int tl = Px(r.TopLeft), tr = Px(r.TopRight), br = Px(r.BottomRight), bl = Px(r.BottomLeft);
        var rounded = Math.Max(Math.Max(tl, tr), Math.Max(br, bl));
        var region = CreateRoundRectRgn(0, 0, w + 1, h + 1, rounded * 2, rounded * 2);
        // Square off any corner that shouldn't be rounded by OR-ing a plain rectangle over it.
        void Square(int x1, int y1, int x2, int y2)
        {
            var sq = CreateRectRgn(x1, y1, x2, y2);
            CombineRgn(region, region, sq, RGN_OR);
            DeleteObject(sq);
        }
        if (tl == 0) Square(0, 0, w / 2 + 1, h / 2 + 1);
        if (tr == 0) Square(w / 2, 0, w + 1, h / 2 + 1);
        if (bl == 0) Square(0, h / 2, w / 2 + 1, h + 1);
        if (br == 0) Square(w / 2, h / 2, w + 1, h + 1);
        // The system owns the region after this call.
        if (SetWindowRgn(_hwnd, region, true) == 0) DeleteObject(region);
    }

    private static void EnableBlur(IntPtr hwnd)
    {
        // ACCENT_ENABLE_BLURBEHIND: a plain Gaussian blur that keeps up with per-frame resizing
        // (the acrylic variant lags while a window is being resized). The pill supplies the tint.
        var accent = new AccentPolicy { AccentState = 3, AccentFlags = 0, GradientColor = 0 };
        var size = Marshal.SizeOf(accent);
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WindowCompositionAttributeData { Attribute = 19 /* WCA_ACCENT_POLICY */, Data = ptr, SizeOfData = size };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        catch (Exception ex) { Log.Info("glass blur unavailable: " + ex.Message); }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy { public int AccentState; public int AccentFlags; public uint GradientColor; public int AnimationId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData { public int Attribute; public IntPtr Data; public int SizeOfData; }

    private const int RGN_OR = 2;
    [DllImport("user32.dll")] private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool redraw);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr dest, IntPtr src1, IntPtr src2, int mode);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}
