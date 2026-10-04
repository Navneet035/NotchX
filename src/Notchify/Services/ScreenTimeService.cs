using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Notchify.Core;

namespace Notchify.Services;

/// <summary>
/// Screen time: every few seconds, credit the app in the foreground — unless you've been away from the
/// keyboard and mouse for two minutes or the PC is locked. Kept per day for two weeks in data\screentime.json.
/// Nothing leaves the PC.
/// </summary>
public sealed class ScreenTimeService
{
    private const int StepSeconds = 5;
    private const string Store = "screentime";

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(StepSeconds) };
    private readonly Dictionary<string, string> _friendly = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Dictionary<string, double>> _days = new();
    private DateTime _lastSave = DateTime.Now;

    public event Action? Updated;

    public ScreenTimeService() => _timer.Tick += (_, _) => Tick();

    public void Start()
    {
        _days = JsonStore.Load<Dictionary<string, Dictionary<string, double>>>(Store);
        var oldest = DateTime.Today.AddDays(-14).ToString("yyyy-MM-dd");
        foreach (var key in _days.Keys.Where(k => string.CompareOrdinal(k, oldest) < 0).ToList()) _days.Remove(key);
        Apply();
        SettingsStore.Changed += Apply;
    }

    private void Apply()
    {
        if (SettingsStore.Current.Behavior.TrackScreenTime) _timer.Start();
        else _timer.Stop();
    }

    public void Save() => JsonStore.Save(Store, _days);

    private static string TodayKey => DateTime.Today.ToString("yyyy-MM-dd");

    private void Tick()
    {
        try
        {
            if (IdleSeconds() > 120) return;
            var hwnd = Native.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return;
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || pid == Environment.ProcessId) return;
            using var p = Process.GetProcessById((int)pid);
            var name = p.ProcessName;
            if (name is "LockApp" or "Idle" or "ScreenClippingHost") return;
            var display = Friendly(p);
            if (!_days.TryGetValue(TodayKey, out var today)) _days[TodayKey] = today = new();
            today[display] = today.GetValueOrDefault(display) + StepSeconds;
            if ((DateTime.Now - _lastSave).TotalSeconds >= 60) { Save(); _lastSave = DateTime.Now; }
            Updated?.Invoke();
        }
        catch { /* the process may have just exited */ }
    }

    /// <summary>"Google Chrome" rather than "chrome".</summary>
    private string Friendly(Process p)
    {
        if (_friendly.TryGetValue(p.ProcessName, out var cached)) return cached;
        var name = p.ProcessName;
        try
        {
            var desc = p.MainModule?.FileVersionInfo.FileDescription;
            if (!string.IsNullOrWhiteSpace(desc) && desc.Length <= 40) name = desc.Trim();
        }
        catch { /* elevated processes don't let us read their module */ }
        if (p.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase)) name = "File Explorer & desktop";
        _friendly[p.ProcessName] = name;
        return name;
    }

    public TimeSpan Today => TimeSpan.FromSeconds(_days.GetValueOrDefault(TodayKey)?.Values.Sum() ?? 0);

    public List<(string App, TimeSpan Time)> TopToday(int count) =>
        (_days.GetValueOrDefault(TodayKey) ?? new())
            .OrderByDescending(kv => kv.Value).Take(count)
            .Select(kv => (kv.Key, TimeSpan.FromSeconds(kv.Value))).ToList();

    public static string Format(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{Math.Max(0, t.Minutes)}m";

    private static double IdleSeconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return 0;
        return (Environment.TickCount - (int)info.dwTime) / 1000.0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
}
