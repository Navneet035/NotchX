using Notchify.Services;

namespace Notchify.Core;

/// <summary>
/// Service locator for the shared, app-wide services. Kept deliberately simple so contributors
/// can reach any service from any module: <c>Notch.Media.PlayPause()</c>, <c>Notch.Hub.Notify(...)</c>.
/// </summary>
public static class Notch
{
    public static ActivityHub Hub { get; } = new();
    public static INotchShell Shell { get; set; } = null!;
    public static ModuleRegistry Modules { get; } = new();

    public static MediaService Media { get; } = new();
    public static LyricsService Lyrics { get; } = new();
    public static SpectrumService Spectrum { get; } = new();
    public static AudioService Audio { get; } = new();
    public static BrightnessService Brightness { get; } = new();
    public static InputHookService Input { get; } = new();
    public static HotkeyService Hotkeys { get; } = new();
    public static ClipboardService Clipboard { get; } = new();
    public static ShelfService Shelf { get; } = new();
    public static KeepAwakeService KeepAwake { get; } = new();
    public static BatteryService Battery { get; } = new();
    public static WeatherService Weather { get; } = new();
    public static ForegroundTracker Foreground { get; } = new();
    public static SystemStatsService Stats { get; } = new();
    public static DriveWatcher Drives { get; } = new();
    public static BluetoothService Bluetooth { get; } = new();
    public static CalendarService Calendar { get; } = new();
    public static AiUsageService AiUsage { get; } = new();
    public static DeveloperApiService DeveloperApi { get; } = new();
    public static ScreenTimeService ScreenTime { get; } = new();
    public static SpacesService Spaces { get; } = new();
}
