using System.Security.Cryptography;

namespace Notchify.Core;

/// <summary>
/// Every user-tweakable option lives here and is serialised to %APPDATA%\Notchify\settings.json.
/// The file is human-editable; unknown fields are ignored and missing ones get defaults.
/// </summary>
public sealed class AppSettings
{
    public AppearanceSettings Appearance { get; set; } = new();
    public BehaviorSettings Behavior { get; set; } = new();
    public List<ModuleEntry> Modules { get; set; } = new();
    public HudSettings Huds { get; set; } = new();
    public MediaSettings Media { get; set; } = new();
    public WeatherSettings Weather { get; set; } = new();
    public SearchSettings Search { get; set; } = new();
    public ClipboardSettings Clipboard { get; set; } = new();
    public TimerSettings Timer { get; set; } = new();
    public CalendarSettings Calendar { get; set; } = new();
    public AiUsageSettings AiUsage { get; set; } = new();
    public DeveloperSettings Developer { get; set; } = new();
    public CaptureSettings Capture { get; set; } = new();
    public SpacesSettings Spaces { get; set; } = new();
    public KeepAwakeSettings KeepAwake { get; set; } = new();
    public CuelySettings Cuely { get; set; } = new();
    public DocumentSettings Documents { get; set; } = new();
    public ClockSettings Clock { get; set; } = new();
    public NotificationSettings Notifications { get; set; } = new();
}

/// <summary>Other apps' notifications mirrored in the notch (Microsoft Store build only).</summary>
public sealed class NotificationSettings
{
    /// <summary>Slide new notifications out of the notch.</summary>
    public bool ShowIsland { get; set; } = true;
    /// <summary>False shows only the app name and "New notification", for privacy.</summary>
    public bool ShowText { get; set; } = true;
    /// <summary>Unread count on the closed notch.</summary>
    public bool UnreadOnPill { get; set; } = true;
    /// <summary>No islands while Windows Do Not Disturb is on (they still count as unread).</summary>
    public bool RespectDoNotDisturb { get; set; } = true;
    public double Seconds { get; set; } = 6;
    /// <summary>App names (as Windows shows them) that never appear in the notch.</summary>
    public List<string> MutedApps { get; set; } = new();
}

public sealed class ClockSettings
{
    /// <summary>Clock at the top right of the open notch.</summary>
    public bool ShowInHeader { get; set; } = true;
    /// <summary>"System" (follow Windows), "12h" or "24h".</summary>
    public string TimeFormat { get; set; } = "System";
    public bool ShowSeconds { get; set; } = false;
    /// <summary>AM/PM after a 12-hour time.</summary>
    public bool ShowAmPm { get; set; } = true;
    /// <summary>Date next to the header clock: "None", "Day" (Sat), "Short" (Sat 4 Oct), "Long" (Saturday, 4 October) or "Numeric" (Windows' short date).</summary>
    public string DateStyle { get; set; } = "Short";
}

public sealed class DocumentSettings
{
    /// <summary>"Auto" (Word if installed, else LibreOffice), "Word" or "LibreOffice".</summary>
    public string Converter { get; set; } = "Auto";
    /// <summary>Path to soffice.exe; empty = look in Program Files.</summary>
    public string LibreOfficePath { get; set; } = "";
    /// <summary>"Same folder" as the original, or "Documents".</summary>
    public string SaveTo { get; set; } = "Same folder";
    public bool AddToShelf { get; set; } = false;
    public bool OpenWhenDone { get; set; } = false;
}

/// <summary>A card on the Home page. Size is in grid units: 12 columns across, rows fill the notch height two at a time.</summary>
public sealed class HomeCard
{
    public string Type { get; set; } = "";
    public int Cols { get; set; } = 3;
    public int Rows { get; set; } = 2;
}

public sealed class CaptureSettings
{
    /// <summary>"NotchX" = built-in region capture; "Snipping Tool" = Windows' own (Win+Shift+S).</summary>
    public string Method { get; set; } = "NotchX";
    public bool CopyToClipboard { get; set; } = true;
    public bool AddToShelf { get; set; } = true;
    public bool SaveToFolder { get; set; } = false;
    /// <summary>Empty = Pictures\Screenshots.</summary>
    public string Folder { get; set; } = "";
    public bool ShowPreview { get; set; } = true;
    /// <summary>Global shortcut for a capture, e.g. "Ctrl+Shift+X". Empty = none.</summary>
    public string Hotkey { get; set; } = "";
}

public sealed class SpacesSettings
{
    /// <summary>Show an island with the desktop's name when switching virtual desktops.</summary>
    public bool ShowIsland { get; set; } = true;
    /// <summary>Desktops tab: one row per app ("Chrome ×3") instead of one per window.</summary>
    public bool GroupByApp { get; set; } = false;
}

public sealed class KeepAwakeSettings
{
    public bool KeepDisplayOn { get; set; } = true;
    public bool AutoOffOnLowBattery { get; set; } = true;
}

public sealed class CuelySettings
{
    public double Speed { get; set; } = 35;
    public double TextSize { get; set; } = 26;
    public bool Mirror { get; set; } = false;
}

public sealed class AppearanceSettings
{
    /// <summary>"Notch" hugs the top edge with square top corners; "Pill" floats with fully rounded corners.</summary>
    public string Style { get; set; } = "Notch";
    public double CollapsedWidth { get; set; } = 190;
    public double CollapsedHeight { get; set; } = 32;
    public double ExpandedWidth { get; set; } = 620;
    public double ExpandedHeight { get; set; } = 300;
    public double CornerRadius { get; set; } = 18;
    public double TopOffset { get; set; } = 0;
    public string AccentColor { get; set; } = "#FF0A84FF";
    public string BackgroundColor { get; set; } = "#F2000000";
    /// <summary>Glass sheen intensity, 0 (flat black) to 1.</summary>
    public double GlassIntensity { get; set; } = 0.6;
    /// <summary>Blur whatever is behind the open notch (frosted glass). Falls back to a solid fill when Windows transparency effects are off.</summary>
    public bool Glass { get; set; } = false;
    /// <summary>How much of the background colour covers the blur, 0 (clear glass) to 1 (solid).</summary>
    public double TintOpacity { get; set; } = 0.55;
    /// <summary>Fill of the open notch and islands: "Solid", "Gradient" or "Image". The collapsed pill always uses BackgroundColor.</summary>
    public string BackgroundType { get; set; } = "Solid";
    public string GradientColor { get; set; } = "#FF1C1C3A";
    /// <summary>Gradient direction in degrees (0 = left to right, 90 = top to bottom).</summary>
    public double GradientAngle { get; set; } = 90;
    public string BackgroundImage { get; set; } = "";
    public double BackgroundImageOpacity { get; set; } = 0.5;
    /// <summary>Scales text and controls inside the notch (0.8–1.4).</summary>
    public double UiScale { get; set; } = 1.0;
    /// <summary>Text under the tab icons: "Always", "Selected" (only the open tab) or "Never".</summary>
    public string TabLabels { get; set; } = "Always";
    /// <summary>Tabs that don't fit beside the clock: "Wrap" onto more rows (the notch grows to make room) or "Menu" (More ▾).</summary>
    public string TabOverflow { get; set; } = "Wrap";
    /// <summary>Colour of the selected tab's capsule (also the Settings sidebar). Empty = the accent colour.</summary>
    public string TabTintColor { get; set; } = "";
    /// <summary>How strongly the selected tab is tinted, 0.05 (a whisper) to 1 (solid).</summary>
    public double TabTintStrength { get; set; } = 0.16;
    /// <summary>-1 = primary display, otherwise index into the screen list.</summary>
    public int DisplayIndex { get; set; } = -1;
    public bool AnimationsEnabled { get; set; } = true;
    public double AnimationSpeed { get; set; } = 1.0;
}

public sealed class BehaviorSettings
{
    public bool HoverToOpen { get; set; } = true;
    public int HoverDelayMs { get; set; } = 180;
    public bool AutoCollapse { get; set; } = true;
    public int AutoCollapseDelayMs { get; set; } = 450;
    public bool ScrollToSwitchTabs { get; set; } = true;
    public bool ScrollOnPillChangesVolume { get; set; } = true;
    public bool HideFromScreenCapture { get; set; } = false;
    public bool HideInFullscreen { get; set; } = true;
    public bool ShowTrayIcon { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;
    public string PaletteHotkey { get; set; } = "Ctrl+Shift+Space";
    public string ToggleHotkey { get; set; } = "Ctrl+Shift+N";
    public bool WindowSnapping { get; set; } = false;
    public bool PeekOnHover { get; set; } = false;
    public string LastTab { get; set; } = "home";
    /// <summary>Tab shown when the notch opens: a module id, "last" (where you left off) or "smart" (music while playing, timer while running, else Home).</summary>
    public string DefaultTab { get; set; } = "home";
    /// <summary>Older widget list ("!" = hidden). Read once and converted to <see cref="HomeCards"/>.</summary>
    public List<string>? HomeWidgets { get; set; }
    /// <summary>Home page cards in order, with their sizes.</summary>
    public List<HomeCard> HomeCards { get; set; } = new();
    /// <summary>Count time per app for the Screen time card. Stays on this PC.</summary>
    public bool TrackScreenTime { get; set; } = true;
    /// <summary>"Dropdown" = Windows Terminal quake window under the notch; "Window" = a normal Terminal window.</summary>
    public string TerminalStyle { get; set; } = "Dropdown";
}

public sealed class ModuleEntry
{
    public string Id { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>False hides the tab but keeps the feature running (e.g. music still shows on the pill).</summary>
    public bool ShowTab { get; set; } = true;
}

public sealed class HudSettings
{
    public bool VolumeHud { get; set; } = true;
    /// <summary>Intercept the volume keys so the stock Windows flyout never appears.</summary>
    public bool ReplaceWindowsVolumeFlyout { get; set; } = false;
    public bool BrightnessHud { get; set; } = true;
    public bool CapsLockHud { get; set; } = true;
    public bool KeystrokeHud { get; set; } = false;
    public bool BatteryHud { get; set; } = true;
    public int FullChargeLevel { get; set; } = 100;
    public int LowBatteryLevel { get; set; } = 20;
    public bool LowBatterySound { get; set; } = true;
    public bool DownloadAlerts { get; set; } = true;
    public bool ScreenshotShelf { get; set; } = true;
    public bool DriveAlerts { get; set; } = true;
    public bool PrivacyIndicator { get; set; } = true;
    public bool FocusIsland { get; set; } = true;
    public bool AirPodsPopup { get; set; } = false;
    /// <summary>Style used for volume/brightness: "Island", "EdgeBar", "Gauge".</summary>
    public string HudStyle { get; set; } = "Island";
    public double HudScale { get; set; } = 1.0;
    public bool ExternalMonitorBrightness { get; set; } = false;
    /// <summary>Island when the sound output changes (headphones connected, etc.).</summary>
    public bool OutputSwitchIsland { get; set; } = true;
}

public sealed class MediaSettings
{
    public bool ShowInPill { get; set; } = true;
    public bool Lyrics { get; set; } = true;
    public bool LyricsInPill { get; set; } = false;
    public bool LiveSpectrum { get; set; } = true;
    public bool SpectrumInPill { get; set; } = false;
}

public sealed class WeatherSettings
{
    /// <summary>Older single-city setting; moved into <see cref="Places"/> on first run.</summary>
    public string City { get; set; } = "";
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    /// <summary>Your places, in order. The first one is your main weather; all of them show on the World clocks card.</summary>
    public List<WeatherPlace> Places { get; set; } = new();
    public bool Fahrenheit { get; set; } = false;
    public int RefreshMinutes { get; set; } = 30;
}

/// <summary>A city picked from search, with its exact location and time zone.</summary>
public sealed class WeatherPlace
{
    public string Name { get; set; } = "";
    /// <summary>State / province, e.g. "Alberta".</summary>
    public string Region { get; set; } = "";
    public string Country { get; set; } = "";
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    /// <summary>IANA time zone, e.g. "America/Edmonton". Empty until the first weather fetch fills it in.</summary>
    public string TimeZone { get; set; } = "";

    /// <summary>"Calgary, Alberta, Canada".</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string FullName => string.Join(", ", new[] { Name, Region, Country }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
    /// <summary>"Alberta, Canada".</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Subtitle => string.Join(", ", new[] { Region, Country }.Where(x => !string.IsNullOrWhiteSpace(x) && x != Name).Distinct());
}

public sealed class SearchEngine
{
    public string Name { get; set; } = "";
    /// <summary>URL with %s where the query goes.</summary>
    public string Url { get; set; } = "";
}

public sealed class SearchSettings
{
    public string DefaultEngine { get; set; } = "Google";
    public List<SearchEngine> Engines { get; set; } = new()
    {
        new() { Name = "Google", Url = "https://www.google.com/search?q=%s" },
        new() { Name = "Bing", Url = "https://www.bing.com/search?q=%s" },
        new() { Name = "DuckDuckGo", Url = "https://duckduckgo.com/?q=%s" },
        new() { Name = "Brave", Url = "https://search.brave.com/search?q=%s" },
        new() { Name = "Perplexity", Url = "https://www.perplexity.ai/search?q=%s" },
        new() { Name = "Claude", Url = "https://claude.ai/new?q=%s" },
        new() { Name = "ChatGPT", Url = "https://chatgpt.com/?q=%s" },
        new() { Name = "YouTube", Url = "https://www.youtube.com/results?search_query=%s" },
        new() { Name = "Wikipedia", Url = "https://en.wikipedia.org/w/index.php?search=%s" },
    };
    public string TranslateTo { get; set; } = "en";
    /// <summary>"google" (keyless public endpoint) or "libre" (LibreTranslate instance below).</summary>
    public string TranslateProvider { get; set; } = "google";
    public string LibreTranslateUrl { get; set; } = "https://libretranslate.com/translate";
    public string LibreTranslateKey { get; set; } = "";
    public string VideoDownloader { get; set; } = "yt-dlp";
}

public sealed class ClipboardSettings
{
    public bool Enabled { get; set; } = true;
    public int MaxItems { get; set; } = 200;
    public bool CaptureImages { get; set; } = true;
    public bool Ocr { get; set; } = true;
    public bool AutoPaste { get; set; } = true;
    public List<string> IgnoredApps { get; set; } = new() { "KeePass", "KeePassXC", "1Password", "Bitwarden" };
}

public sealed class TimerSettings
{
    public int FocusMinutes { get; set; } = 25;
    public int ShortBreakMinutes { get; set; } = 5;
    public int LongBreakMinutes { get; set; } = 15;
    public int SessionsBeforeLongBreak { get; set; } = 4;
    public bool AutoStartNext { get; set; } = true;
    public bool Sound { get; set; } = true;
    public string Mascot { get; set; } = "🐢";
}

public sealed class CalendarSettings
{
    /// <summary>Secret iCal (.ics) links from Google, Outlook/Exchange, iCloud, etc.</summary>
    public List<string> IcsUrls { get; set; } = new();
    public int RefreshMinutes { get; set; } = 15;
    public int AlertMinutesBefore { get; set; } = 5;
}

public sealed class AiUsageSettings
{
    /// <summary>Sections shown in the tab, in order. Remove an id to hide it.</summary>
    public List<string> Sections { get; set; } = new() { "claude", "codex", "copilot", "cursor" };
    public string ClaudeDir { get; set; } = "";
    public string CodexDir { get; set; } = "";
    /// <summary>Token budget per 5-hour Claude window used for the progress ring and alerts (0 = no alert).</summary>
    public long ClaudeWindowTokenBudget { get; set; } = 0;
    public int AlertAtPercent { get; set; } = 85;
    /// <summary>Show a usage chip ("Claude 42%") on the closed notch.</summary>
    public bool ShowInNotch { get; set; } = true;
    /// <summary>Which apps get a chip on the closed notch: any of claude, codex, copilot.</summary>
    public List<string> NotchProviders { get; set; } = new() { "claude" };
    /// <summary>GitHub token for Copilot quota, encrypted with Windows DPAPI.</summary>
    public string CopilotTokenProtected { get; set; } = "";
    public string CursorTokenProtected { get; set; } = "";
    /// <summary>
    /// USD per million tokens, keyed by a substring of the model id. The longest matching key wins,
    /// so "opus-5-5" beats "opus". Edit freely when prices change.
    /// </summary>
    public Dictionary<string, ModelPrice> Prices { get; set; } = new()
    {
        ["fable"] = new() { Input = 10, Output = 50, CacheWrite = 12.5, CacheRead = 1.0 },
        ["opus-5-5"] = new() { Input = 4, Output = 20, CacheWrite = 5, CacheRead = 0.2 },
        ["opus"] = new() { Input = 5, Output = 25, CacheWrite = 6.25, CacheRead = 0.5 },
        ["sonnet-5"] = new() { Input = 2, Output = 10, CacheWrite = 2.5, CacheRead = 0.2 },
        ["sonnet"] = new() { Input = 3, Output = 15, CacheWrite = 3.75, CacheRead = 0.3 },
        ["haiku"] = new() { Input = 1, Output = 5, CacheWrite = 1.25, CacheRead = 0.1 },
    };
}

public sealed class ModelPrice
{
    public double Input { get; set; }
    public double Output { get; set; }
    public double CacheWrite { get; set; }
    public double CacheRead { get; set; }
}

public sealed class DeveloperSettings
{
    public bool ApiEnabled { get; set; } = false;
    public int Port { get; set; } = 9999;
    public string Token { get; set; } = "";
    public bool AgentApprovals { get; set; } = true;
    public bool AgentActivity { get; set; } = true;
}

public static class SettingsStore
{
    public static AppSettings Current { get; private set; } = new();

    /// <summary>Raised (on the UI thread) whenever settings change, so views can live-preview.</summary>
    public static event Action? Changed;

    private static System.Threading.Timer? _saveTimer;

    public static void Load()
    {
        Current = JsonStore.LoadFile<AppSettings>(Paths.SettingsFile);
        if (string.IsNullOrEmpty(Current.Developer.Token))
            Current.Developer.Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        MigrateHome(Current.Behavior);
        SaveNow();
    }

    public static void NotifyChanged()
    {
        Changed?.Invoke();
        Save();
    }

    /// <summary>Debounced save.</summary>
    public static void Save()
    {
        _saveTimer?.Dispose();
        _saveTimer = new System.Threading.Timer(_ => SaveNow(), null, 400, Timeout.Infinite);
    }

    public static void SaveNow() => JsonStore.SaveFile(Paths.SettingsFile, Current);

    /// <summary>Default Home layout, or the older widget list converted to cards.</summary>
    private static void MigrateHome(BehaviorSettings b)
    {
        if (b.HomeCards.Count > 0) { b.HomeWidgets = null; return; }
        if (b.HomeWidgets is { Count: > 0 } old)
        {
            foreach (var raw in old.Where(w => !w.StartsWith('!')))
            {
                var card = raw switch
                {
                    "clock" => new HomeCard { Type = "clock", Cols = 3, Rows = 2 },
                    "player" => new HomeCard { Type = "nowplaying", Cols = 4, Rows = 2 },
                    "controls" => new HomeCard { Type = "controls", Cols = 3, Rows = 2 },
                    "bluetooth" => new HomeCard { Type = "bluetooth", Cols = 2, Rows = 2 },
                    _ => null,
                };
                if (card != null) b.HomeCards.Add(card);
            }
        }
        if (b.HomeCards.Count == 0) b.HomeCards = DefaultHomeCards();
        b.HomeWidgets = null;
    }

    public static List<HomeCard> DefaultHomeCards() => new()
    {
        new() { Type = "clock", Cols = 3, Rows = 2 },
        new() { Type = "nowplaying", Cols = 4, Rows = 2 },
        new() { Type = "volume", Cols = 3, Rows = 1 },
        new() { Type = "brightness", Cols = 3, Rows = 1 },
        new() { Type = "bluetooth", Cols = 2, Rows = 2 },
    };
}

/// <summary>Windows DPAPI wrapper — the Windows counterpart to the macOS Keychain.</summary>
public static class Secrets
{
    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var bytes = ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string Unprotect(string protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), null, DataProtectionScope.CurrentUser);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch { return ""; }
    }
}
