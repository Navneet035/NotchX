using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Notchify.Core;

namespace Notchify.Modules;

/// <summary>
/// Window snapping: drag any window up to the notch and snap zones appear in the island;
/// release over one to tile the window (halves, thirds, full screen).
/// </summary>
public sealed class WindowSnapModule : NotchModule
{
    private static readonly (string Label, double X, double W)[] Zones =
    {
        ("◧ Left ½", 0, 0.5), ("Left ⅓", 0, 1 / 3.0), ("▣ Full", 0, 1), ("Centre ⅓", 1 / 3.0, 1 / 3.0), ("Right ⅓", 2 / 3.0, 1 / 3.0), ("Right ½ ◨", 0.5, 0.5),
    };

    private IntPtr _hook;
    private Native.WinEventProc? _proc;
    private IntPtr _dragging;
    private int _zone = -1;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private StackPanel? _strip;

    public WindowSnapModule() => _poll.Tick += (_, _) => Poll();

    public override string Id => "snap";
    public override string Title => "Window snapping";
    public override string Glyph => Glyphs.Snap;
    public override bool HasTab => false;
    public override bool DefaultEnabled => SettingsStore.Current.Behavior.WindowSnapping;
    public override string Description => "Drag a window to the notch and drop it on a zone to tile it. (Overlaps Windows 11's own snap layouts.)";

    protected override void Start()
    {
        _proc = OnWinEvent;
        _hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_MOVESIZESTART, Native.EVENT_SYSTEM_MOVESIZEEND, IntPtr.Zero, _proc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
    }

    protected override void Stop()
    {
        if (_hook != IntPtr.Zero) Native.UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
        _poll.Stop();
    }

    private void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (evt == Native.EVENT_SYSTEM_MOVESIZESTART)
        {
            _dragging = hwnd;
            _zone = -1;
            _poll.Start();
        }
        else if (evt == Native.EVENT_SYSTEM_MOVESIZEEND && hwnd == _dragging)
        {
            _poll.Stop();
            if (_zone >= 0) Apply(hwnd, Zones[_zone]);
            _zone = -1;
            _dragging = IntPtr.Zero;
            Notch.Hub.DismissByKey("snap");
        }
    }

    private void Poll()
    {
        if (!Native.GetCursorPos(out var p)) return;
        var mon = Native.MonitorFromPoint(p, Native.MONITOR_DEFAULTTONEAREST);
        var mi = Native.MONITORINFO.Create();
        Native.GetMonitorInfo(mon, ref mi);
        var m = mi.rcMonitor;
        var centre = m.Left + m.Width / 2.0;
        var stripHalf = Math.Min(m.Width * 0.22, 360);
        var near = p.Y - m.Top < 90 && Math.Abs(p.X - centre) < stripHalf + 60;
        if (!near)
        {
            if (_zone != -1 || Notch.Hub.Current?.Key == "snap") { _zone = -1; Notch.Hub.DismissByKey("snap"); }
            return;
        }
        var rel = Math.Clamp((p.X - (centre - stripHalf)) / (stripHalf * 2), 0, 0.999);
        var zone = (int)(rel * Zones.Length);
        if (zone == _zone && Notch.Hub.Current?.Key == "snap") return;
        _zone = zone;
        ShowZones();
    }

    private void ShowZones()
    {
        _strip ??= new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 8, 10, 10) };
        _strip.Children.Clear();
        for (var i = 0; i < Zones.Length; i++)
        {
            var active = i == _zone;
            var tb = new TextBlock { Text = Zones[i].Label, FontSize = 11, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var b = new Border { Width = 66, Height = 38, Margin = new Thickness(3, 0, 3, 0), CornerRadius = new CornerRadius(8), Child = tb };
            if (active) b.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            else b.Background = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
            _strip.Children.Add(b);
        }
        if (Notch.Hub.Current?.Key != "snap")
            Notch.Hub.Show(new Island { Key = "snap", CustomContent = _strip, Sticky = true, Priority = IslandPriority.Critical });
    }

    private static void Apply(IntPtr hwnd, (string Label, double X, double W) zone)
    {
        var mon = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
        var mi = Native.MONITORINFO.Create();
        if (!Native.GetMonitorInfo(mon, ref mi)) return;
        var wa = mi.rcWork;
        if (Native.IsZoomed(hwnd)) Native.ShowWindow(hwnd, 9 /* SW_RESTORE */);
        var x = wa.Left + (int)(wa.Width * zone.X);
        var w = (int)(wa.Width * zone.W);
        Native.SetWindowPos(hwnd, IntPtr.Zero, x, wa.Top, w, wa.Height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }
}
