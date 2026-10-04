using System.Management;
using System.Runtime.InteropServices;
using Notchify.Core;

namespace Notchify.Services;

/// <summary>
/// Brightness for the built-in panel (WMI) and, optionally, DDC/CI external monitors (dxva2 VCP code 0x10).
/// Also watches WMI for brightness-key changes to drive the brightness HUD.
/// </summary>
public sealed class BrightnessService : ObservableObject, IDisposable
{
    private ManagementEventWatcher? _watcher;
    private int _level = -1;

    public event Action<int>? Changed;

    public bool HasInternalPanel { get; private set; }

    /// <summary>An external monitor answered over DDC/CI.</summary>
    public bool HasExternal { get; private set; }

    /// <summary>There is something whose brightness we can change (drives the Home slider).</summary>
    public bool CanControl => HasInternalPanel || HasExternal;

    /// <summary>External monitors are driven when the user opted in, or when there's no built-in panel at all (desktops).</summary>
    private bool UseExternal => HasExternal && (SettingsStore.Current.Huds.ExternalMonitorBrightness || !HasInternalPanel);

    public int Level
    {
        get => _level;
        set
        {
            value = Math.Clamp(value, 0, 100);
            if (HasInternalPanel) SetInternal(value);
            if (UseExternal) SetExternal(value);
            Set(ref _level, value);
        }
    }

    public void Start()
    {
        _level = ReadInternal();
        HasInternalPanel = _level >= 0;
        Raise(nameof(Level));
        Raise(nameof(HasInternalPanel));
        Raise(nameof(CanControl));

        // DDC/CI is slow (tens of ms per monitor), so probe external monitors off the UI thread.
        Task.Run(() =>
        {
            var ext = ReadExternal();
            Ui.Post(() =>
            {
                HasExternal = ext >= 0;
                if (!HasInternalPanel && HasExternal) _level = ext;
                Raise(nameof(HasExternal));
                Raise(nameof(CanControl));
                Raise(nameof(Level));
            });
        });

        if (!HasInternalPanel) return;
        try
        {
            _watcher = new ManagementEventWatcher(new ManagementScope(@"root\wmi"),
                new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            _watcher.EventArrived += (_, e) =>
            {
                var v = Convert.ToInt32(e.NewEvent.Properties["Brightness"].Value);
                Ui.Post(() =>
                {
                    if (v == _level) return;
                    _level = v;
                    Raise(nameof(Level));
                    Changed?.Invoke(v);
                });
            };
            _watcher.Start();
        }
        catch (Exception ex) { Log.Info("brightness watcher: " + ex.Message); }
    }

    public void Step(int delta) => Level = Math.Max(0, _level) + delta;

    private static int ReadInternal()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject o in s.Get()) return Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch { }
        return -1;
    }

    private static void SetInternal(int value)
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods");
            foreach (ManagementObject o in s.Get())
                o.InvokeMethod("WmiSetBrightness", new object[] { 1u, (byte)value });
        }
        catch { }
    }

    // ---------- DDC/CI ----------
    private static void ForEachPhysicalMonitor(Action<IntPtr> action)
    {
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMon, _, _, _) =>
        {
            if (GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out var count) && count > 0)
            {
                var arr = new PHYSICAL_MONITOR[count];
                if (GetPhysicalMonitorsFromHMONITOR(hMon, count, arr))
                {
                    foreach (var pm in arr)
                    {
                        try { action(pm.hPhysicalMonitor); } catch { }
                    }
                    DestroyPhysicalMonitors(count, arr);
                }
            }
            return true;
        }, IntPtr.Zero);
    }

    private static int ReadExternal()
    {
        var result = -1;
        ForEachPhysicalMonitor(h =>
        {
            if (result < 0 && GetVCPFeatureAndVCPFeatureReply(h, 0x10, IntPtr.Zero, out var cur, out var max) && max > 0)
                result = (int)(cur * 100 / max);
        });
        return result;
    }

    private int _pendingExternal = -1;
    private int _externalBusy;

    private void SetExternal(int percent)
    {
        // DDC is slow (~50 ms per monitor); keep it off the UI thread and only send the latest value,
        // so dragging the slider doesn't queue up hundreds of writes.
        Interlocked.Exchange(ref _pendingExternal, percent);
        if (Interlocked.Exchange(ref _externalBusy, 1) == 1) return;
        Task.Run(() =>
        {
            try
            {
                int v;
                while ((v = Interlocked.Exchange(ref _pendingExternal, -1)) >= 0)
                {
                    var value = v;
                    ForEachPhysicalMonitor(h =>
                    {
                        if (GetVCPFeatureAndVCPFeatureReply(h, 0x10, IntPtr.Zero, out _, out var max) && max > 0)
                            SetVCPFeature(h, 0x10, (uint)(value * max / 100));
                    });
                }
            }
            finally
            {
                Interlocked.Exchange(ref _externalBusy, 0);
                if (Volatile.Read(ref _pendingExternal) >= 0) SetExternal(Volatile.Read(ref _pendingExternal));
            }
        });
    }

    public void Dispose() { try { _watcher?.Stop(); _watcher?.Dispose(); } catch { } }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string description;
    }

    [DllImport("dxva2.dll")] private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMon, out uint count);
    [DllImport("dxva2.dll")] private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMon, uint count, [Out] PHYSICAL_MONITOR[] arr);
    [DllImport("dxva2.dll")] private static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] arr);
    [DllImport("dxva2.dll")] private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte code, IntPtr type, out uint current, out uint max);
    [DllImport("dxva2.dll")] private static extern bool SetVCPFeature(IntPtr h, byte code, uint value);
}
