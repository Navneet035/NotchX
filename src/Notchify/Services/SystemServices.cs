using System.Net.NetworkInformation;
using System.Text.Json;
using System.Windows.Threading;
using Notchify.Core;

namespace Notchify.Services;

/// <summary>Keep Awake ("Caffeine"): indefinitely, for a duration, or until a clock time.</summary>
public sealed class KeepAwakeService : ObservableObject
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool _active;
    private DateTime? _until;

    public KeepAwakeService() => _timer.Tick += (_, _) => Tick();

    public bool IsActive { get => _active; private set { if (Set(ref _active, value)) Raise(nameof(Label)); } }
    public DateTime? Until { get => _until; private set { if (Set(ref _until, value)) Raise(nameof(Label)); } }
    public string Label => !_active ? "Off" : _until is { } u ? $"Until {u:t}" : "On";

    public void Toggle()
    {
        if (IsActive) Disable(); else Enable(null);
    }

    public void Enable(TimeSpan? duration) => EnableUntil(duration.HasValue ? DateTime.Now + duration.Value : null);

    public void EnableUntil(DateTime? until)
    {
        Native.SetThreadExecutionState(Native.ES_CONTINUOUS | Native.ES_SYSTEM_REQUIRED |
            (SettingsStore.Current.KeepAwake.KeepDisplayOn ? Native.ES_DISPLAY_REQUIRED : 0));
        IsActive = true;
        Until = until;
        _timer.Start();
        Update();
        Notch.Hub.Notify(Glyphs.Bolt, "Keep awake on", until is { } u ? $"Until {u:t}" : "Until you turn it off",
            Ui.Orange, IslandPriority.Low, 2, "caffeine");
    }

    public void Disable()
    {
        Native.SetThreadExecutionState(Native.ES_CONTINUOUS);
        IsActive = false;
        Until = null;
        _timer.Stop();
        Update();
        Notch.Hub.Notify(Glyphs.Bolt, "Keep awake off", null, Ui.Gray, IslandPriority.Low, 1.5, "caffeine");
    }

    private void Tick()
    {
        // Auto-disable so you never forget it.
        if (_until is { } u && DateTime.Now >= u) Disable();
        else if (SettingsStore.Current.KeepAwake.AutoOffOnLowBattery && Notch.Battery.Percent is > 0 and < 10 && !Notch.Battery.Charging) Disable();
        else Update();
    }

    private void Update()
    {
        if (!IsActive) { Notch.Hub.Remove("caffeine"); return; }
        Notch.Hub.Upsert("caffeine", a =>
        {
            a.Glyph = Glyphs.Bolt;
            a.Accent = Ui.Orange;
            a.Text = _until is { } u ? $"{u:t}" : "";
            a.Priority = 10;
        });
    }
}

public sealed class BatteryService : ObservableObject
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(20) };
    private int _percent = -1;
    private bool _charging, _pluggedIn, _hasBattery;
    private bool _fullNotified, _lowNotified;

    public event Action? PowerSourceChanged;

    public int Percent { get => _percent; private set { if (Set(ref _percent, value)) Raise(nameof(Glyph)); } }
    public bool Charging { get => _charging; private set { if (Set(ref _charging, value)) Raise(nameof(Glyph)); } }
    public bool PluggedIn { get => _pluggedIn; private set => Set(ref _pluggedIn, value); }
    public bool HasBattery { get => _hasBattery; private set => Set(ref _hasBattery, value); }
    public string Glyph => Glyphs.Battery(Math.Max(0, _percent), _charging);
    public string Text => _hasBattery ? $"{_percent}%" : "";

    public void Start()
    {
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh(initial: true);
    }

    /// <summary>Also called from WM_POWERBROADCAST for instant plug/unplug feedback.</summary>
    public void Refresh(bool initial = false)
    {
        if (!Native.GetSystemPowerStatus(out var s)) return;
        HasBattery = s.BatteryFlag != 128 && s.BatteryLifePercent != 255;
        var wasPlugged = PluggedIn;
        PluggedIn = s.ACLineStatus == 1;
        Charging = PluggedIn && HasBattery && s.BatteryLifePercent < 100;
        Percent = HasBattery ? s.BatteryLifePercent : -1;
        Raise(nameof(Text));
        if (!HasBattery || initial) return;

        var hud = SettingsStore.Current.Huds;
        if (wasPlugged != PluggedIn)
        {
            PowerSourceChanged?.Invoke();
            if (hud.BatteryHud)
                Notch.Hub.Show(new Island
                {
                    Key = "battery",
                    Glyph = Glyphs.Battery(Percent, PluggedIn),
                    Title = PluggedIn ? "Charging" : "On battery",
                    Message = $"{Percent}%",
                    Accent = PluggedIn ? Ui.Green : Ui.Gray,
                    Progress = Percent / 100.0,
                    Priority = IslandPriority.Low,
                    Duration = TimeSpan.FromSeconds(2.5),
                });
        }

        // Full-charge island: the cue to unplug.
        if (PluggedIn && Percent >= hud.FullChargeLevel && !_fullNotified)
        {
            _fullNotified = true;
            Notch.Hub.Notify(Glyphs.Battery(100, false), "Charged", $"Battery at {Percent}% — you can unplug", Ui.Green, IslandPriority.Normal, 5);
        }
        if (!PluggedIn) _fullNotified = false;

        if (!PluggedIn && Percent <= hud.LowBatteryLevel && !_lowNotified)
        {
            _lowNotified = true;
            if (hud.LowBatterySound) System.Media.SystemSounds.Exclamation.Play();
            Notch.Hub.Notify(Glyphs.Battery(Percent, false), "Low battery", $"{Percent}% remaining", Ui.Red, IslandPriority.High, 6);
        }
        if (PluggedIn || Percent > hud.LowBatteryLevel + 5) _lowNotified = false;
    }
}

public sealed class WeatherService : ObservableObject
{
    private readonly DispatcherTimer _timer = new();
    private string _temperature = "";
    private string _condition = "";
    private string _icon = "";
    private string _place = "";
    private string _range = "";

    public string Temperature { get => _temperature; private set => Set(ref _temperature, value); }
    public string Condition { get => _condition; private set => Set(ref _condition, value); }
    public string Icon { get => _icon; private set => Set(ref _icon, value); }
    public string Place { get => _place; private set => Set(ref _place, value); }
    public string Range { get => _range; private set => Set(ref _range, value); }
    public bool HasData => !string.IsNullOrEmpty(_temperature);

    public WeatherService() => _timer.Tick += (_, _) => _ = RefreshAsync();

    public void Start()
    {
        _timer.Interval = TimeSpan.FromMinutes(Math.Max(5, SettingsStore.Current.Weather.RefreshMinutes));
        _timer.Start();
        _ = MigrateCityAsync();
    }

    /// <summary>Older settings had one free-text city; turn it into the first place, with exact coordinates.</summary>
    private async Task MigrateCityAsync()
    {
        var s = SettingsStore.Current.Weather;
        if (s.Places.Count == 0 && !string.IsNullOrWhiteSpace(s.City))
        {
            try
            {
                var match = (await SearchPlacesAsync(s.City)).FirstOrDefault();
                if (match != null)
                {
                    // Keep the coordinates the user already had, if any; the search only fills in the rest.
                    if (s.Latitude is { } lat && s.Longitude is { } lon) { match.Latitude = lat; match.Longitude = lon; }
                    s.Places.Add(match);
                    SettingsStore.Save();
                }
            }
            catch (Exception ex) { Log.Info("weather migrate: " + ex.Message); }
        }
        await RefreshAsync();
    }

    /// <summary>Cities matching what's been typed so far ("Cal" → Calgary, Cali, California City…), from Open-Meteo's geocoder.</summary>
    public static async Task<List<WeatherPlace>> SearchPlacesAsync(string query, CancellationToken cancel = default)
    {
        var list = new List<WeatherPlace>();
        if (query.Trim().Length < 2) return list;
        var json = await Http.Client.GetStringAsync(
            $"https://geocoding-api.open-meteo.com/v1/search?count=8&language=en&format=json&name={Uri.EscapeDataString(query.Trim())}", cancel);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("results", out var results)) return list;
        foreach (var r in results.EnumerateArray())
        {
            string Str(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            list.Add(new WeatherPlace
            {
                Name = Str("name"),
                Region = Str("admin1"),
                Country = Str("country"),
                Latitude = r.GetProperty("latitude").GetDouble(),
                Longitude = r.GetProperty("longitude").GetDouble(),
                TimeZone = Str("timezone"),
            });
        }
        return list;
    }

    // ---------------- World clocks: every place's weather in one request ----------------

    /// <summary>Latest conditions per place (same order as Settings › Weather › Places).</summary>
    public IReadOnlyList<PlaceWeather> World { get; private set; } = Array.Empty<PlaceWeather>();
    public event Action? WorldUpdated;
    private DateTime _worldFetched = DateTime.MinValue;
    private string _worldKey = "";

    /// <summary>Fetch weather for all places, unless it's fresh enough (or <paramref name="force"/>).</summary>
    public async Task RefreshWorldAsync(bool force = false)
    {
        var s = SettingsStore.Current.Weather;
        var places = s.Places.ToList();
        var key = string.Join("|", places.Select(p => FormattableString.Invariant($"{p.Latitude},{p.Longitude}"))) + s.Fahrenheit;
        if (!force && key == _worldKey && DateTime.Now - _worldFetched < TimeSpan.FromMinutes(Math.Max(5, s.RefreshMinutes))) return;
        if (places.Count == 0)
        {
            World = Array.Empty<PlaceWeather>();
            _worldKey = key;
            WorldUpdated?.Invoke();
            return;
        }
        try
        {
            var lats = string.Join(",", places.Select(p => p.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            var lons = string.Join(",", places.Select(p => p.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            var unit = s.Fahrenheit ? "&temperature_unit=fahrenheit" : "";
            var json = await Http.Client.GetStringAsync(
                $"https://api.open-meteo.com/v1/forecast?latitude={lats}&longitude={lons}&current=temperature_2m,weather_code,is_day&timezone=auto&forecast_days=1{unit}");
            using var doc = JsonDocument.Parse(json);
            // One place comes back as an object, several as an array.
            var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : new List<JsonElement> { doc.RootElement };
            var result = new List<PlaceWeather>();
            var filledZone = false;
            for (var i = 0; i < places.Count && i < items.Count; i++)
            {
                var it = items[i];
                var cur = it.GetProperty("current");
                var code = cur.GetProperty("weather_code").GetInt32();
                var isDay = cur.GetProperty("is_day").GetInt32() == 1;
                var (icon, condition) = Describe(code, isDay);
                var offset = it.TryGetProperty("utc_offset_seconds", out var o) ? o.GetInt32() : 0;
                if (string.IsNullOrEmpty(places[i].TimeZone) && it.TryGetProperty("timezone", out var tz) && tz.GetString() is { Length: > 0 } zone)
                {
                    places[i].TimeZone = zone;
                    filledZone = true;
                }
                result.Add(new PlaceWeather(places[i], $"{Math.Round(cur.GetProperty("temperature_2m").GetDouble())}°", icon, condition, isDay, offset));
            }
            if (filledZone) SettingsStore.Save();
            World = result;
            _worldKey = key;
            _worldFetched = DateTime.Now;
            WorldUpdated?.Invoke();
        }
        catch (Exception ex) { Log.Info("world weather: " + ex.Message); }
    }

    public async Task RefreshAsync()
    {
        try
        {
            var s = SettingsStore.Current.Weather;
            double? lat = s.Latitude, lon = s.Longitude;
            var place = s.City;
            // The first place in the list is the main weather.
            if (s.Places.FirstOrDefault() is { } main)
            {
                lat = main.Latitude;
                lon = main.Longitude;
                place = main.Name;
            }

            if (!string.IsNullOrWhiteSpace(place) && (lat == null || lon == null))
            {
                var geo = await Http.Client.GetStringAsync(
                    $"https://geocoding-api.open-meteo.com/v1/search?count=1&name={Uri.EscapeDataString(place)}");
                using var gd = JsonDocument.Parse(geo);
                if (gd.RootElement.TryGetProperty("results", out var r) && r.GetArrayLength() > 0)
                {
                    lat = r[0].GetProperty("latitude").GetDouble();
                    lon = r[0].GetProperty("longitude").GetDouble();
                    place = r[0].GetProperty("name").GetString() ?? s.City;
                    s.Latitude = lat; s.Longitude = lon;
                    SettingsStore.Save();
                }
            }

            if (lat == null || lon == null)
            {
                // Windows location (only if the user allowed location for desktop apps).
                try
                {
                    var locator = new Windows.Devices.Geolocation.Geolocator { DesiredAccuracy = Windows.Devices.Geolocation.PositionAccuracy.Default };
                    var pos = await locator.GetGeopositionAsync(TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(10));
                    lat = pos.Coordinate.Point.Position.Latitude;
                    lon = pos.Coordinate.Point.Position.Longitude;
                    place = "";
                }
                catch
                {
                    Condition = "Set your city in Settings";
                    return;
                }
            }

            var unit = s.Fahrenheit ? "&temperature_unit=fahrenheit" : "";
            var json = await Http.Client.GetStringAsync(FormattableString.Invariant(
                $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current=temperature_2m,weather_code,is_day&daily=temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=1{unit}"));
            using var doc = JsonDocument.Parse(json);
            var cur = doc.RootElement.GetProperty("current");
            var temp = cur.GetProperty("temperature_2m").GetDouble();
            var code = cur.GetProperty("weather_code").GetInt32();
            var isDay = cur.GetProperty("is_day").GetInt32() == 1;
            var daily = doc.RootElement.GetProperty("daily");
            var hi = daily.GetProperty("temperature_2m_max")[0].GetDouble();
            var lo = daily.GetProperty("temperature_2m_min")[0].GetDouble();

            Temperature = $"{Math.Round(temp)}°";
            Range = $"H {Math.Round(hi)}°  L {Math.Round(lo)}°";
            (Icon, Condition) = Describe(code, isDay);
            Place = place ?? "";
            Raise(nameof(HasData));
        }
        catch (Exception ex)
        {
            Log.Info("weather: " + ex.Message);
        }
    }

    /// <summary>WMO weather interpretation codes.</summary>
    private static (string, string) Describe(int code, bool day) => code switch
    {
        0 => (day ? "☀" : "☾", "Clear"),
        1 or 2 => (day ? "⛅" : "☁", code == 1 ? "Mostly clear" : "Partly cloudy"),
        3 => ("☁", "Overcast"),
        45 or 48 => ("🌫", "Fog"),
        >= 51 and <= 57 => ("🌦", "Drizzle"),
        >= 61 and <= 67 => ("🌧", "Rain"),
        >= 71 and <= 77 => ("❄", "Snow"),
        >= 80 and <= 82 => ("🌧", "Showers"),
        85 or 86 => ("🌨", "Snow showers"),
        >= 95 => ("⛈", "Thunderstorm"),
        _ => ("☁", "—"),
    };
}

/// <summary>Current conditions at one of the user's places, and how to tell its local time.</summary>
public sealed record PlaceWeather(WeatherPlace Place, string Temperature, string Icon, string Condition, bool IsDay, int UtcOffsetSeconds)
{
    /// <summary>Local time there: by its time zone (follows daylight saving), else the offset the forecast reported.</summary>
    public DateTimeOffset Now => PlaceTime.Now(Place, UtcOffsetSeconds);
}

public static class PlaceTime
{
    private static readonly Dictionary<string, TimeZoneInfo?> Zones = new();

    public static DateTimeOffset Now(WeatherPlace place, int fallbackOffsetSeconds = 0)
    {
        var now = DateTimeOffset.Now;
        if (Zone(place.TimeZone) is { } tz) return TimeZoneInfo.ConvertTime(now, tz);
        return now.ToOffset(TimeSpan.FromSeconds(fallbackOffsetSeconds));
    }

    private static TimeZoneInfo? Zone(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        if (Zones.TryGetValue(id, out var z)) return z;
        // .NET understands IANA ids ("Asia/Kolkata") on Windows through ICU.
        try { z = TimeZoneInfo.FindSystemTimeZoneById(id); } catch { z = null; }
        return Zones[id] = z;
    }

    /// <summary>Short enough for a narrow card: "Same time", "+3h", "+11:30 · Mon", "−7h · Sat" (day only when it differs).</summary>
    public static string Difference(DateTimeOffset there)
    {
        var here = DateTimeOffset.Now;
        var diff = there.Offset - here.Offset;
        var day = there.Date != here.Date ? $" · {there:ddd}" : "";
        if (diff == TimeSpan.Zero) return "Same time" + day;
        var sign = diff > TimeSpan.Zero ? "+" : "−";
        var abs = diff.Duration();
        var text = abs.Minutes == 0 ? $"{sign}{(int)abs.TotalHours}h" : $"{sign}{(int)abs.TotalHours}:{abs.Minutes:00}";
        return text + day;
    }
}

/// <summary>Remembers the last foreground app that wasn't Notchify, and reports fullscreen apps.</summary>
public sealed class ForegroundTracker
{
    private IntPtr _hook;
    private readonly Native.WinEventProc _proc;
    private IntPtr _ownWindow;

    public ForegroundTracker() => _proc = OnEvent;

    public IntPtr LastExternal { get; private set; }
    public event Action<bool>? FullscreenChanged;
    public bool FullscreenActive { get; private set; }

    public void Start(IntPtr ownWindow)
    {
        _ownWindow = ownWindow;
        _hook = Native.SetWinEventHook(0x0003 /* EVENT_SYSTEM_FOREGROUND */, 0x0003, IntPtr.Zero, _proc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        OnEvent(IntPtr.Zero, 3, Native.GetForegroundWindow(), 0, 0, 0, 0);
    }

    private void OnEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero) return;
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == Environment.ProcessId) return;
        LastExternal = hwnd;
        var fs = IsFullscreen(hwnd);
        if (fs != FullscreenActive)
        {
            FullscreenActive = fs;
            FullscreenChanged?.Invoke(fs);
        }
    }

    private static bool IsFullscreen(IntPtr hwnd)
    {
        var cls = Native.GetClassName(hwnd);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow") return false;
        if (!Native.GetWindowRect(hwnd, out var r)) return false;
        var mon = Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST);
        var mi = Native.MONITORINFO.Create();
        if (!Native.GetMonitorInfo(mon, ref mi)) return false;
        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top &&
               r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }

    public void Stop()
    {
        if (_hook != IntPtr.Zero) Native.UnhookWinEvent(_hook);
    }
}

public sealed class SystemStatsService : ObservableObject
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private long _idle, _kernel, _user;
    private long _rx, _tx;
    private int _users;

    public double Cpu { get; private set; }
    public double Memory { get; private set; }
    public string MemoryText { get; private set; } = "";
    public string Download { get; private set; } = "";
    public string Upload { get; private set; } = "";
    public string Disk { get; private set; } = "";
    public string Uptime { get; private set; } = "";
    public List<double> CpuHistory { get; } = new();
    public List<double> MemHistory { get; } = new();
    public event Action? Updated;

    public SystemStatsService() => _timer.Tick += (_, _) => Sample();

    /// <summary>Stats are only sampled while something is showing them.</summary>
    public void Acquire()
    {
        if (++_users == 1) { Sample(); _timer.Start(); }
    }

    public void Release()
    {
        if (--_users <= 0) { _users = 0; _timer.Stop(); }
    }

    private void Sample()
    {
        if (Native.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var dIdle = idle - _idle; var dTotal = kernel - _kernel + user - _user;
            if (_kernel != 0 && dTotal > 0) Cpu = Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100);
            _idle = idle; _kernel = kernel; _user = user;
        }

        var m = Native.MEMORYSTATUSEX.Create();
        if (Native.GlobalMemoryStatusEx(ref m))
        {
            Memory = m.dwMemoryLoad;
            MemoryText = $"{Ui.FormatBytes(m.ullTotalPhys - m.ullAvailPhys)} / {Ui.FormatBytes(m.ullTotalPhys)}";
        }

        long rx = 0, tx = 0;
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var st = n.GetIPStatistics();
            rx += st.BytesReceived; tx += st.BytesSent;
        }
        if (_rx != 0)
        {
            Download = Ui.FormatBytes(Math.Max(0, rx - _rx)) + "/s";
            Upload = Ui.FormatBytes(Math.Max(0, tx - _tx)) + "/s";
        }
        _rx = rx; _tx = tx;

        try
        {
            var d = new System.IO.DriveInfo(System.IO.Path.GetPathRoot(Environment.SystemDirectory)!);
            Disk = $"{Ui.FormatBytes(d.AvailableFreeSpace)} free of {Ui.FormatBytes(d.TotalSize)}";
        }
        catch { }

        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        Uptime = up.TotalDays >= 1 ? $"{(int)up.TotalDays}d {up.Hours}h" : $"{up.Hours}h {up.Minutes}m";

        Push(CpuHistory, Cpu);
        Push(MemHistory, Memory);
        Raise(nameof(Cpu)); Raise(nameof(Memory)); Raise(nameof(MemoryText));
        Raise(nameof(Download)); Raise(nameof(Upload)); Raise(nameof(Disk)); Raise(nameof(Uptime));
        Updated?.Invoke();

        static void Push(List<double> list, double v)
        {
            list.Add(v);
            if (list.Count > 60) list.RemoveAt(0);
        }
    }
}
