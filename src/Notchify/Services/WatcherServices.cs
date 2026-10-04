using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;
using Notchify.Core;

namespace Notchify.Services;

/// <summary>Pops an island when a download finishes landing in Downloads. Works with every browser, no extension.</summary>
public sealed class DownloadWatcher : IDisposable
{
    private static readonly HashSet<string> Partial = new(StringComparer.OrdinalIgnoreCase)
        { ".crdownload", ".part", ".partial", ".download", ".tmp", ".opdownload", ".!ut" };

    private FileSystemWatcher? _watcher;
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

    public void Start()
    {
        var dir = Paths.Downloads;
        if (!Directory.Exists(dir)) return;
        _watcher = new FileSystemWatcher(dir) { IncludeSubdirectories = false, EnableRaisingEvents = true };
        _watcher.Created += (_, e) => Consider(e.FullPath);
        _watcher.Renamed += (_, e) => Consider(e.FullPath);
    }

    private async void Consider(string path)
    {
        if (Partial.Contains(Path.GetExtension(path)) || Path.GetFileName(path).StartsWith("~$")) return;
        lock (_seen) { if (!_seen.Add(path)) return; }
        // Wait until the file size is stable and it can be opened.
        long last = -1;
        for (var i = 0; i < 60; i++)
        {
            await Task.Delay(500);
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return;
                if (info.Length == last && info.Length > 0)
                {
                    using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { }
                    break;
                }
                last = info.Length;
            }
            catch { }
        }
        Ui.Post(() => Announce(path));
    }

    private static void Announce(string path)
    {
        var name = Path.GetFileName(path);
        var size = File.Exists(path) ? Ui.FormatBytes(new FileInfo(path).Length) : "";
        Notch.Hub.Show(new Island
        {
            Glyph = Glyphs.Download,
            Title = name,
            Message = $"Downloaded · {size}",
            Accent = Ui.Green,
            Image = ShelfService.ImageExtensions.Contains(Path.GetExtension(path)) ? Ui.LoadImage(path, 120) : Ui.FileIcon(path),
            Duration = TimeSpan.FromSeconds(6),
            DragFiles = new[] { path },
            Actions =
            {
                new IslandAction("Show in folder", () => Ui.RevealInExplorer(path), true, Glyphs.Folder),
                new IslandAction("Open", () => Ui.OpenUrl(path)),
                new IslandAction("Shelf", () => Notch.Shelf.Add(new[] { path }), false, Glyphs.Add),
            },
        });
    }

    public void Dispose() => _watcher?.Dispose();
}

/// <summary>Every screenshot you take (Win+PrtScn, Snipping Tool auto-save) is staged on the shelf.</summary>
public sealed class ScreenshotWatcher : IDisposable
{
    private FileSystemWatcher? _watcher;

    public void Start()
    {
        var dir = Paths.Screenshots;
        Directory.CreateDirectory(dir);
        _watcher = new FileSystemWatcher(dir) { EnableRaisingEvents = true };
        _watcher.Created += async (_, e) =>
        {
            if (!ShelfService.ImageExtensions.Contains(Path.GetExtension(e.FullPath))) return;
            // Captures started from NotchX already reach the shelf through the clipboard.
            if ((DateTime.Now - Modules.ToolsModule.LastCapture).TotalSeconds < 20) return;
            await Task.Delay(400);
            Ui.Post(() =>
            {
                Notch.Shelf.Add(new[] { e.FullPath });
                Notch.Hub.Show(new Island
                {
                    Glyph = Glyphs.Crop,
                    Title = "Screenshot",
                    Message = "Drag it anywhere, or find it on the Shelf",
                    Image = Ui.LoadImage(e.FullPath, 200),
                    Accent = Ui.Teal,
                    DragFiles = new[] { e.FullPath },
                    Duration = TimeSpan.FromSeconds(5),
                    OpenTab = "shelf",
                    Actions = { new IslandAction("Open", () => Ui.OpenUrl(e.FullPath)) },
                });
            });
        };
    }

    public void Dispose() => _watcher?.Dispose();
}

/// <summary>USB drive plugged in → island with one-tap Eject, then "Safe to disconnect".</summary>
public sealed class DriveWatcher
{
    private HashSet<string> _known = new();
    private string? _ejecting;

    public void Start() => _known = Current();

    private static HashSet<string> Current() =>
        DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Removable && d.IsReady).Select(d => d.Name).ToHashSet();

    /// <summary>Called from WM_DEVICECHANGE.</summary>
    public async void OnDeviceChange()
    {
        await Task.Delay(800);
        var now = Current();
        foreach (var added in now.Except(_known)) Announce(added);
        foreach (var removed in _known.Except(now))
        {
            if (removed == _ejecting)
            {
                Notch.Hub.Notify(Glyphs.Check, "Safe to disconnect", removed.TrimEnd('\\'), Ui.Green, IslandPriority.Normal, 3, "drive-" + removed);
                _ejecting = null;
            }
            else Notch.Hub.DismissByKey("drive-" + removed);
        }
        _known = now;
    }

    private void Announce(string root)
    {
        var info = new DriveInfo(root);
        string label;
        try { label = string.IsNullOrEmpty(info.VolumeLabel) ? "USB Drive" : info.VolumeLabel; } catch { label = "USB Drive"; }
        string free;
        try { free = $"{Ui.FormatBytes(info.AvailableFreeSpace)} free"; } catch { free = ""; }
        Notch.Hub.Show(new Island
        {
            Key = "drive-" + root,
            Glyph = Glyphs.Usb,
            Title = $"{label} ({root.TrimEnd('\\')})",
            Message = free,
            Accent = Ui.Teal,
            Duration = TimeSpan.FromSeconds(8),
            Actions =
            {
                new IslandAction("Open", () => Ui.OpenUrl(root)),
                new IslandAction("Eject", () => Eject(root), true, Glyphs.Usb),
            },
        });
    }

    public void Eject(string root)
    {
        _ejecting = root;
        try
        {
            // Same "Eject" verb Explorer uses; Windows handles flushing and the safe-removal request.
            var type = Type.GetTypeFromProgID("Shell.Application")!;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic folder = shell.NameSpace(17); // ssfDRIVES
            dynamic item = folder.ParseName(root.TrimEnd('\\'));
            item.InvokeVerb("Eject");
            Marshal.FinalReleaseComObject(shell);
            Notch.Hub.Notify(Glyphs.Usb, "Ejecting…", root.TrimEnd('\\'), Ui.Gray, IslandPriority.Normal, 3, "drive-" + root);
        }
        catch (Exception ex)
        {
            _ejecting = null;
            Notch.Hub.Notify(Glyphs.Warning, "Couldn't eject", ex.Message, Ui.Red);
        }
    }
}

/// <summary>
/// Camera / microphone privacy indicator. Windows records live capability use in the
/// CapabilityAccessManager consent store: an entry with LastUsedTimeStop == 0 is in use right now.
/// </summary>
public sealed class PrivacyMonitor
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private const string Root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\";

    public List<string> CameraApps { get; private set; } = new();
    public List<string> MicApps { get; private set; } = new();

    public PrivacyMonitor() => _timer.Tick += (_, _) => Poll();

    public void Start() => _timer.Start();

    public void Stop()
    {
        _timer.Stop();
        CameraApps = new(); MicApps = new();
        Notch.Hub.Remove("privacy");
    }

    private void Poll()
    {
        var cam = InUse("webcam");
        var mic = InUse("microphone").Where(a => !a.Equals("NotchX", StringComparison.OrdinalIgnoreCase)).ToList();
        var changed = !cam.SequenceEqual(CameraApps) || !mic.SequenceEqual(MicApps);
        CameraApps = cam; MicApps = mic;
        if (!changed) return;

        if (cam.Count == 0 && mic.Count == 0) { Notch.Hub.Remove("privacy"); return; }
        var apps = cam.Concat(mic).Distinct().ToList();
        Notch.Hub.Upsert("privacy", a =>
        {
            a.Glyph = cam.Count > 0 ? Glyphs.Camera : Glyphs.Mic;
            a.Accent = cam.Count > 0 ? Ui.Green : Ui.Orange;
            a.Text = "●";
            a.Detail = (cam.Count > 0 ? "Camera" : "Microphone") + " in use by " + string.Join(", ", apps);
            a.Priority = 90;
        });
    }

    private static List<string> InUse(string capability)
    {
        var result = new List<string>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Root + capability);
            if (key == null) return result;
            Scan(key, result, packaged: true);
            using var np = key.OpenSubKey("NonPackaged");
            if (np != null) Scan(np, result, packaged: false);
        }
        catch { }
        return result;
    }

    private static void Scan(RegistryKey parent, List<string> result, bool packaged)
    {
        foreach (var name in parent.GetSubKeyNames())
        {
            if (name == "NonPackaged") continue;
            using var k = parent.OpenSubKey(name);
            if (k == null) continue;
            var start = k.GetValue("LastUsedTimeStart") is long s ? s : 0;
            var stop = k.GetValue("LastUsedTimeStop") is long e ? e : -1;
            if (start > 0 && stop == 0)
            {
                var display = packaged ? name.Split('_')[0].Split('.').Last() : Path.GetFileNameWithoutExtension(name.Replace('#', '\\'));
                result.Add(display);
            }
        }
    }
}

/// <summary>
/// Detects Windows Focus / Do-Not-Disturb via the shell's WNF state (the same signal the
/// notification center uses) and mirrors it as a compact island; low-priority islands stay quiet.
/// </summary>
public sealed class FocusMonitor
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private int _last = -1;

    public bool IsQuiet => _last > 0;

    public FocusMonitor() => _timer.Tick += (_, _) => Poll();

    public void Start()
    {
        _last = -1;
        _timer.Start();
        Poll(initial: true);
    }

    public void Stop()
    {
        _timer.Stop();
        Notch.Hub.QuietMode = false;
        Notch.Hub.Remove("focus");
    }

    private void Poll(bool initial = false)
    {
        var state = Query();
        if (state < 0 || state == _last) return;
        _last = state;
        Notch.Hub.QuietMode = state > 0;
        if (state > 0)
            Notch.Hub.Upsert("focus", a => { a.Glyph = Glyphs.Moon; a.Accent = Ui.Purple; a.Text = ""; a.Priority = 5; a.Detail = "Do not disturb"; });
        else Notch.Hub.Remove("focus");
        if (!initial && SettingsStore.Current.Huds.FocusIsland)
            Notch.Hub.Notify(Glyphs.Moon, state > 0 ? "Do not disturb" : "Focus off",
                state == 1 ? "Priority notifications only" : state == 2 ? "Alarms only" : null, Ui.Purple, IslandPriority.High, 2.5, "focus");
    }

    // WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED
    private static readonly ulong StateName = 0x0D83063EA3BF1C75;

    private static int Query()
    {
        try
        {
            var name = StateName;
            var buffer = new byte[4];
            var size = (uint)buffer.Length;
            var status = NtQueryWnfStateData(ref name, IntPtr.Zero, IntPtr.Zero, out _, buffer, ref size);
            return status == 0 && size >= 4 ? BitConverter.ToInt32(buffer, 0) : -1;
        }
        catch { return -1; }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryWnfStateData(ref ulong stateName, IntPtr typeId, IntPtr explicitScope,
        out uint changeStamp, byte[] buffer, ref uint bufferSize);
}
