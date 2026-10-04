using System.Diagnostics;
using System.Text;
using System.Windows.Threading;
using Notchify.Core;

namespace Notchify.Services;

/// <summary>
/// Opens Windows Terminal (drop-down "quake" window or a normal window) and keeps hold of its window,
/// so the notch can hide, bring back and close it. The quake window has no title bar of its own,
/// and running "wt -w _quake" again only adds a tab — hence this little controller.
/// </summary>
public static class Terminal
{
    private const string WtClass = "CASCADIA_HOSTING_WINDOW_CLASS";
    private const string ActivityId = "terminal";

    private static IntPtr _hwnd;
    private static bool _lastShown;
    private static DispatcherTimer? _watch;
    private static DispatcherTimer? _find;

    /// <summary>Raised when the terminal opens, closes, hides or reappears.</summary>
    public static event Action? Changed;

    public static bool IsRunning => _hwnd != IntPtr.Zero && Native.IsWindow(_hwnd);
    public static bool IsShown => IsRunning && Native.IsWindowVisible(_hwnd) && !Native.IsIconic(_hwnd);

    private static bool Dropdown => SettingsStore.Current.Behavior.TerminalStyle != "Window";

    /// <summary>Open it if it isn't running, otherwise hide or bring it back.</summary>
    public static void Toggle()
    {
        if (!IsRunning) Adopt(FindQuake());
        if (!IsRunning) { Launch(); return; }
        if (IsShown) Hide(); else Show();
    }

    public static void Show()
    {
        if (!IsRunning) { Launch(); return; }
        Native.ShowWindow(_hwnd, Native.SW_RESTORE);
        Native.SetForegroundWindow(_hwnd);
        Notch.Shell.Collapse();
        Update();
    }

    public static void Hide()
    {
        if (!IsRunning) return;
        // For the quake window, Windows Terminal turns "minimise" into "slide away".
        Native.ShowWindow(_hwnd, Native.SW_MINIMIZE);
        Update();
    }

    public static void Close()
    {
        if (!IsRunning) Adopt(FindQuake());
        if (!IsRunning) return;
        // Terminal may ask to confirm if several tabs are open; the watcher notices when it's really gone.
        Native.PostMessage(_hwnd, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    private static void Launch()
    {
        var before = TerminalWindows().ToHashSet();
        try
        {
            Process.Start(new ProcessStartInfo("wt.exe", Dropdown ? "-w _quake" : "-w new") { UseShellExecute = true });
        }
        catch
        {
            try { Process.Start(new ProcessStartInfo("powershell.exe") { UseShellExecute = true }); } catch { }
            return;
        }
        Notch.Shell.Collapse();

        // Terminal takes a moment to create its window; look for the new one for a few seconds.
        var started = DateTime.Now;
        _find?.Stop();
        _find = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _find.Tick += (_, _) =>
        {
            var fresh = TerminalWindows().FirstOrDefault(h => !before.Contains(h) && Native.IsWindowVisible(h));
            if (fresh == IntPtr.Zero && Dropdown) fresh = FindQuake();
            if (fresh != IntPtr.Zero || DateTime.Now - started > TimeSpan.FromSeconds(6))
            {
                _find!.Stop();
                Adopt(fresh);
            }
        };
        _find.Start();
    }

    private static void Adopt(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        _hwnd = hwnd;
        _lastShown = IsShown;
        if (_watch == null)
        {
            _watch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _watch.Tick += (_, _) => Watch();
        }
        _watch.Start();
        Update();
    }

    private static void Watch()
    {
        if (!IsRunning)
        {
            _hwnd = IntPtr.Zero;
            _watch?.Stop();
            Update();
            return;
        }
        if (IsShown != _lastShown) Update();
    }

    /// <summary>Keep the pill chip and any open Tools view in sync.</summary>
    private static void Update()
    {
        _lastShown = IsShown;
        if (IsRunning)
            Notch.Hub.Upsert(ActivityId, a =>
            {
                a.Glyph = Glyphs.Terminal;
                a.Text = _lastShown ? "Terminal" : "Terminal (hidden)";
                a.Accent = Ui.Gray;
                a.OpenTab = "tools";
                a.Priority = 1;
            });
        else Notch.Hub.Remove(ActivityId);
        Changed?.Invoke();
    }

    private static IEnumerable<IntPtr> TerminalWindows()
    {
        var list = new List<IntPtr>();
        var sb = new StringBuilder(64);
        Native.EnumWindows((h, _) =>
        {
            sb.Clear();
            if (Native.GetClassName(h, sb, sb.Capacity) > 0 && sb.ToString() == WtClass) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>
    /// The quake window is the Terminal window docked to the top edge and spanning its monitor's width.
    /// Used when Notchify didn't open it itself (e.g. after a restart, or opened with Win+`).
    /// </summary>
    private static IntPtr FindQuake()
    {
        foreach (var h in TerminalWindows())
        {
            if (!Native.GetWindowRect(h, out var r)) continue;
            var info = Native.MONITORINFO.Create();
            if (!Native.GetMonitorInfo(Native.MonitorFromWindow(h, Native.MONITOR_DEFAULTTONEAREST), ref info)) continue;
            var m = info.rcMonitor;
            var nearTop = Math.Abs(r.Top - m.Top) <= 16;
            var fullWidth = (r.Right - r.Left) >= (m.Right - m.Left) - 32;
            if (nearTop && fullWidth && !Native.IsZoomed(h)) return h;
        }
        return IntPtr.Zero;
    }
}
