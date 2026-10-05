using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Threading;
using Notchify.Core;

namespace Notchify.Services;

/// <summary>A top-level app window, as Alt+Tab would show it.</summary>
public sealed record OpenWindow(IntPtr Handle, string Title, string App, string? ExePath, Guid DesktopId, bool Minimized)
{
    public ImageSource? Icon => WindowListService.IconFor(this);
}

/// <summary>One virtual desktop and the windows on it.</summary>
public sealed record DesktopWindows(Guid Id, int Index, string Name, bool IsCurrent, IReadOnlyList<OpenWindow> Windows);

/// <summary>
/// Lists open app windows grouped by virtual desktop. Which desktop a window is on comes from the documented
/// IVirtualDesktopManager; the list of desktops and their names from the registry (see <see cref="SpacesService"/>).
/// Only polls while something on screen has called <see cref="Acquire"/>.
/// </summary>
public sealed class WindowListService
{
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private IVirtualDesktopManager? _vdm;
    private int _users;
    private string _signature = "";

    public IReadOnlyList<DesktopWindows> Desktops { get; private set; } = Array.Empty<DesktopWindows>();
    public event Action? Changed;

    public WindowListService() => _poll.Tick += (_, _) => Refresh();

    public void Acquire()
    {
        _users++;
        Refresh();
        _poll.Start();
    }

    public void Release()
    {
        _users = Math.Max(0, _users - 1);
        if (_users == 0) _poll.Stop();
    }

    public DesktopWindows? Current => Desktops.FirstOrDefault(d => d.IsCurrent);
    public int WindowCount => Desktops.Sum(d => d.Windows.Count);

    public void Refresh()
    {
        try
        {
            var vdm = _vdm ??= (IVirtualDesktopManager)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a"))!)!;
            var (ids, current) = SpacesService.ReadState();
            if (ids.Count == 0) ids = new List<Guid> { current }; // a single desktop isn't written to the registry
            if (current == Guid.Empty) current = ids[0];

            var byDesktop = ids.ToDictionary(id => id, _ => new List<OpenWindow>());
            var self = (uint)Environment.ProcessId;
            Native.EnumWindows((h, _) =>
            {
                try
                {
                    if (Describe(h, self) is not { } w) return true;
                    if (vdm.GetWindowDesktopId(h, out var desk) != 0) return true;
                    Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out var cloaked, sizeof(int));
                    // Windows pinned to all desktops (or otherwise unknown) count as "here" when they're showing.
                    if (!byDesktop.ContainsKey(desk)) desk = current;
                    // On this desktop the window must really be showing; on others the shell cloaks it (2) on purpose.
                    // Anything else cloaked is a suspended Store app frame or a hidden helper.
                    if (desk == current ? cloaked != 0 : cloaked is not (0 or DWM_CLOAKED_SHELL)) return true;
                    byDesktop[desk].Add(w with { DesktopId = desk });
                }
                catch { }
                return true;
            }, IntPtr.Zero);

            var list = ids.Select((id, i) => new DesktopWindows(id, i, SpacesService.DesktopName(id, i), id == current, byDesktop[id])).ToList();
            var signature = string.Join("|", list.Select(d => $"{d.Id}{d.IsCurrent}{d.Name}:" +
                string.Join(",", d.Windows.Select(w => $"{w.Handle}{w.Title}{w.Minimized}"))));
            if (signature == _signature) return;
            _signature = signature;
            Desktops = list;
            Changed?.Invoke();
        }
        catch (Exception ex) { Log.Info("window list: " + ex.Message); }
    }

    private const int DWM_CLOAKED_SHELL = 2;

    /// <summary>The window's title and owning app, or null if it isn't something Alt+Tab would show.</summary>
    private static OpenWindow? Describe(IntPtr h, uint self)
    {
        if (!Native.IsWindowVisible(h)) return null;
        var ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
        var appWindow = (ex & Native.WS_EX_APPWINDOW) != 0;
        if (!appWindow && ((ex & Native.WS_EX_TOOLWINDOW) != 0 || (ex & Native.WS_EX_NOACTIVATE) != 0)) return null;
        if (!appWindow && Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return null;
        var title = Native.GetWindowTitle(h);
        if (string.IsNullOrWhiteSpace(title)) return null;
        var cls = Native.GetClassName(h);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow") return null;

        Native.GetWindowThreadProcessId(h, out var pid);
        if (pid == self) return null;
        if (cls == "ApplicationFrameWindow")
        {
            // Store apps live in a frame owned by ApplicationFrameHost; the real app is the child CoreWindow's process.
            var frame = pid;
            uint inner = 0;
            Native.EnumChildWindows(h, (c, _) =>
            {
                Native.GetWindowThreadProcessId(c, out var p);
                if (p != frame) { inner = p; return false; }
                return true;
            }, IntPtr.Zero);
            if (inner == 0) return new OpenWindow(h, title, title, null, Guid.Empty, Native.IsIconic(h));
            pid = inner;
        }
        var exe = Native.GetProcessPath(pid);
        return new OpenWindow(h, title, AppName(exe, pid), exe, Guid.Empty, Native.IsIconic(h));
    }

    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase);

    private static string AppName(string? exe, uint pid)
    {
        if (exe == null)
        {
            try { return Process.GetProcessById((int)pid).ProcessName; } catch { return "App"; }
        }
        if (Names.TryGetValue(exe, out var cached)) return cached;
        string name;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exe);
            name = !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription.Trim()
                : !string.IsNullOrWhiteSpace(info.ProductName) ? info.ProductName.Trim()
                : Path.GetFileNameWithoutExtension(exe);
        }
        catch { name = Path.GetFileNameWithoutExtension(exe); }
        if (name == "Windows Explorer") name = "File Explorer";
        return Names[exe] = name;
    }

    internal static ImageSource? IconFor(OpenWindow w)
    {
        if (w.ExePath != null && Ui.FileIcon(w.ExePath) is { } icon) return icon;
        // Elevated apps don't tell us their exe; ask the window for its own icon instead.
        try
        {
            Native.SendMessageTimeout(w.Handle, Native.WM_GETICON, (IntPtr)Native.ICON_BIG, IntPtr.Zero, 0x2 /* abort if hung */, 100, out var h);
            if (h == IntPtr.Zero) h = Native.GetClassLongPtr(w.Handle, Native.GCLP_HICON);
            return h == IntPtr.Zero ? null : Ui.FromHIcon(h);
        }
        catch { return null; }
    }

    // ---------------- actions ----------------

    /// <summary>Bring a window to the front. If it's on another desktop, Windows switches there.</summary>
    public static void Activate(OpenWindow w) => Activate(w.Handle);

    /// <summary>Bring a window to the front (restoring it if minimised) and close the notch.</summary>
    public static void Activate(IntPtr handle)
    {
        Notch.Shell.Collapse();
        if (Native.IsIconic(handle)) Native.ShowWindow(handle, Native.SW_RESTORE);
        if (!Native.SetForegroundWindow(handle)) Native.SwitchToThisWindow(handle, true);
    }

    public void Close(OpenWindow w)
    {
        Native.PostMessage(w.Handle, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        Ui.Dispatcher.BeginInvoke(Refresh, DispatcherPriority.Background);
    }

    public void Minimize(OpenWindow w)
    {
        Native.ShowWindow(w.Handle, Native.SW_MINIMIZE);
        Refresh();
    }

    /// <summary>Whether "Move to this desktop" works on this Windows build.</summary>
    public static bool CanMoveWindows => DesktopMover.Available;

    public void MoveHere(OpenWindow w)
    {
        if (!DesktopMover.MoveToCurrent(w.Handle))
            Notch.Hub.Notify(Glyphs.Warning, "Couldn't move that window", "This Windows version doesn't allow it.", Ui.Orange);
        Refresh();
    }

    /// <summary>Whether windows can be sent to any desktop (drag between desktops, "Move to ▸").</summary>
    public static bool CanMoveBetweenDesktops => DesktopMover.CanTargetAnyDesktop;

    /// <summary>Send windows to another desktop without switching there.</summary>
    public void MoveTo(IReadOnlyCollection<OpenWindow> windows, DesktopWindows target)
    {
        var failed = windows.Count(w => w.DesktopId != target.Id && !(target.IsCurrent
            ? DesktopMover.MoveToCurrent(w.Handle)
            : DesktopMover.MoveToDesktop(w.Handle, target.Id)));
        if (failed > 0)
            Notch.Hub.Notify(Glyphs.Warning, failed == 1 ? "Couldn't move a window" : $"Couldn't move {failed} windows",
                "Some apps (like ones running as administrator) can't be moved by other apps.", Ui.Orange);
        Refresh();
    }

    [ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr hwnd, out int onCurrent);
        [PreserveSig] int GetWindowDesktopId(IntPtr hwnd, out Guid desktopId);
        [PreserveSig] int MoveWindowToDesktop(IntPtr hwnd, ref Guid desktopId);
    }
}

/// <summary>
/// Moves another app's window to the current desktop. Windows only exposes this through Explorer's private
/// interfaces, whose ids change between Windows versions, so we only use the ones whose layout we know
/// (Windows 11 23H2 and 24H2/25H2) and report "unavailable" everywhere else rather than guess.
/// </summary>
internal static unsafe class DesktopMover
{
    private static readonly Guid ImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    private static readonly Guid ManagerService = new("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");
    private static readonly Guid[] ManagerIids =
    {
        new("53F5CA0B-158F-4124-900C-057158060B27"), // Windows 11 24H2 / 25H2
        new("A3175F2D-239C-4BD2-8AA0-EEBA8B0B138E"), // Windows 11 23H2
    };
    private static readonly Guid ViewCollection = new("1841C6D7-4F9D-42C0-AF41-8747538F10E5");

    private static IntPtr _manager, _views;
    private static bool? _ok;
    private static bool _isLatest;

    public static bool Available => Init();

    private static bool Init()
    {
        if (_ok is { } ok) return ok;
        _ok = false;
        try
        {
            var shell = (IServiceProvider10)Activator.CreateInstance(Type.GetTypeFromCLSID(ImmersiveShell)!)!;
            foreach (var iid in ManagerIids)
            {
                var sid = ManagerService;
                var id = iid;
                if (shell.QueryService(ref sid, ref id, out var p) == 0 && p != IntPtr.Zero)
                {
                    _manager = p;
                    _isLatest = iid == ManagerIids[0];
                    break;
                }
            }
            var vs = ViewCollection;
            var vi = ViewCollection;
            if (_manager != IntPtr.Zero && shell.QueryService(ref vs, ref vi, out var v) == 0 && v != IntPtr.Zero) _views = v;
            _ok = _manager != IntPtr.Zero && _views != IntPtr.Zero;
        }
        catch (Exception ex) { Log.Info("desktop mover unavailable: " + ex.Message); }
        return _ok.Value;
    }

    /// <summary>
    /// Whether windows can be sent to any desktop, not just the current one. Needs FindDesktop, whose slot we've
    /// only confirmed on 24H2+ (it returns the same desktop as GetCurrentDesktop for the current id), so older
    /// builds get "move here" only.
    /// </summary>
    public static bool CanTargetAnyDesktop => Init() && _isLatest;

    public static bool MoveToCurrent(IntPtr hwnd) => Move(hwnd, (manager, vt, desktop) =>
        // IVirtualDesktopManagerInternal: 3 GetCount, 4 MoveViewToDesktop, 5 CanViewMoveDesktops, 6 GetCurrentDesktop
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)vt[6])(manager, desktop));

    public static bool MoveToDesktop(IntPtr hwnd, Guid target)
    {
        if (!CanTargetAnyDesktop) return false;
        return Move(hwnd, (manager, vt, desktop) =>
        {
            // … 7 GetDesktops, 8 GetAdjacentDesktop, 9 SwitchDesktop, 10 SwitchDesktopAndMoveView,
            //   11 CreateDesktop, 12 MoveDesktop, 13 RemoveDesktop, 14 FindDesktop
            var id = target;
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vt[14])(manager, &id, desktop);
        });
    }

    private delegate int DesktopGetter(IntPtr manager, IntPtr* vtable, IntPtr* desktop);

    private static bool Move(IntPtr hwnd, DesktopGetter getDesktop)
    {
        if (!Init()) return false;
        IntPtr view = IntPtr.Zero, desktop = IntPtr.Zero;
        try
        {
            // IApplicationViewCollection: 3 GetViews, 4 GetViewsByZOrder, 5 GetViewsByAppUserModelId, 6 GetViewForHwnd
            var views = *(IntPtr**)_views;
            var getViewForHwnd = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)views[6];
            if (getViewForHwnd(_views, hwnd, &view) != 0 || view == IntPtr.Zero) return false;
            var manager = *(IntPtr**)_manager;
            if (getDesktop(_manager, manager, &desktop) != 0 || desktop == IntPtr.Zero) return false;
            var move = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)manager[4];
            return move(_manager, view, desktop) == 0;
        }
        catch (Exception ex) { Log.Info("move window: " + ex.Message); return false; }
        finally
        {
            if (view != IntPtr.Zero) Marshal.Release(view);
            if (desktop != IntPtr.Zero) Marshal.Release(desktop);
        }
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider10
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid riid, out IntPtr obj);
    }
}
