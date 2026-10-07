using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Notchify.Core;

namespace Notchify.Services;

/// <summary>Something you can pin: an installed app, a folder, a drive, or a file.</summary>
public sealed record ShortcutTarget(string Name, string Path, string Kind)
{
    public bool IsApp => Path.StartsWith("shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Everything the "type to add" box can find: every installed app from Windows' own All apps list
/// (shell:AppsFolder — Store apps like Armoury Crate included, unlike Start-menu shortcuts), well-known
/// folders, drives, and any path you type.
/// </summary>
public static class ShortcutCatalog
{
    private static Task<List<ShortcutTarget>>? _apps;

    /// <summary>Installed apps, read once (about a second) on a background STA thread and cached.</summary>
    public static Task<List<ShortcutTarget>> AppsAsync() => _apps ??= LoadApps();

    private static Task<List<ShortcutTarget>> LoadApps()
    {
        var done = new TaskCompletionSource<List<ShortcutTarget>>();
        var thread = new Thread(() =>
        {
            try { done.SetResult(ReadAppsFolder()); }
            catch (Exception ex)
            {
                Log.Error("apps list", ex);
                _apps = null; // try again next time
                done.SetResult(new List<ShortcutTarget>());
            }
        }) { IsBackground = true, Name = "NotchX apps list" };
        thread.SetApartmentState(ApartmentState.STA); // Shell.Application is an STA COM object
        thread.Start();
        return done.Task;
    }

    private static List<ShortcutTarget> ReadAppsFolder()
    {
        var type = Type.GetTypeFromProgID("Shell.Application") ?? throw new InvalidOperationException("Shell.Application unavailable");
        dynamic shell = Activator.CreateInstance(type)!;
        var list = new List<ShortcutTarget>();
        try
        {
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            foreach (dynamic item in folder.Items())
            {
                string name = item.Name;
                string id = item.Path; // AppUserModelID, or a known-folder path for classic apps
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;
                if (name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new ShortcutTarget(name, "shell:AppsFolder\\" + id, "App"));
            }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
        return list.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Downloads, Documents and friends, then every ready drive.</summary>
    public static List<ShortcutTarget> Places()
    {
        var places = new List<ShortcutTarget>();
        void Add(string name, string? path)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) places.Add(new ShortcutTarget(name, path, "Folder"));
        }
        Add("Downloads", KnownFolder(new Guid("374DE290-123F-4565-9164-39C4925E467B")));
        Add("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Add("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        Add("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        Add("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
        Add("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));
        Add(Environment.UserName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        try
        {
            foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
            {
                var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? (d.DriveType == DriveType.Removable ? "USB drive" : "Local disk") : d.VolumeLabel;
                places.Add(new ShortcutTarget($"{label} ({d.Name.TrimEnd('\\')})", d.Name, "Drive"));
            }
        }
        catch { }
        return places;
    }

    /// <summary>
    /// What the search box shows: a path you typed (if it exists) first, then places and apps whose name starts with
    /// the text, then ones that contain it anywhere.
    /// </summary>
    public static List<ShortcutTarget> Search(IReadOnlyList<ShortcutTarget> apps, string query)
    {
        var results = new List<ShortcutTarget>();
        var q = query.Trim().Trim('"');
        if (q.Length > 0)
        {
            var path = Environment.ExpandEnvironmentVariables(q);
            if (path.Length > 2 && (path.Contains('\\') || path.Contains('/')))
            {
                try
                {
                    if (Directory.Exists(path))
                        results.Add(new ShortcutTarget(Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : path, Path.GetFullPath(path), "Folder"));
                    else if (File.Exists(path))
                        results.Add(new ShortcutTarget(Path.GetFileNameWithoutExtension(path), Path.GetFullPath(path), "File"));
                }
                catch { }
            }
        }
        var pool = Places().Concat(apps).ToList();
        if (q.Length == 0) return results.Concat(pool).ToList();
        results.AddRange(pool.Where(t => t.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase)));
        results.AddRange(pool.Where(t => !t.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) &&
                                         (t.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                          t.Name.Split(' ', '-', '.').Any(w => w.StartsWith(q, StringComparison.OrdinalIgnoreCase)))));
        return results;
    }

    // ---------- icons ----------

    private static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The icon for a target. Apps use the shell's own image, the same one Start shows.</summary>
    public static ImageSource? Icon(ShortcutTarget t)
    {
        if (!t.IsApp) return Ui.FileIcon(t.Path);
        if (IconCache.TryGetValue(t.Path, out var cached)) return cached;
        var icon = ShellImage(t.Path, 64);
        IconCache[t.Path] = icon;
        return icon;
    }

    /// <summary>Save an app's icon as a PNG in NotchX's icon folder (for Launcher and Shelf items), or null.</summary>
    public static string? SaveIcon(ShortcutTarget t)
    {
        if (Icon(t) is not BitmapSource bmp) return null;
        try
        {
            var file = Path.Combine(Paths.Icons, $"{Guid.NewGuid():N}.png");
            ClipboardService.SavePng(bmp, file);
            return file;
        }
        catch (Exception ex) { Log.Error("save app icon", ex); return null; }
    }

    /// <summary>
    /// A Launcher-style shortcut file for an app, so it can sit on the Shelf (which holds files) and be dragged out.
    /// Store apps have no exe to point at, so the shortcut opens them through Explorer's apps folder.
    /// </summary>
    public static string? CreateShortcutFile(ShortcutTarget t, string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var safe = string.Concat(t.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            var file = Path.Combine(folder, safe + ".lnk");
            var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell unavailable");
            dynamic shell = Activator.CreateInstance(type)!;
            try
            {
                dynamic link = shell.CreateShortcut(file);
                link.TargetPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                link.Arguments = t.Path;
                link.Description = t.Name;
                link.Save();
                Marshal.FinalReleaseComObject(link);
            }
            finally { Marshal.FinalReleaseComObject(shell); }
            return file;
        }
        catch (Exception ex) { Log.Error("create shortcut", ex); return null; }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    private static string? KnownFolder(Guid id)
    {
        if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var p) != 0) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Marshal.FreeCoTaskMem(p); }
    }

    // IShellItemImageFactory: the shell renders the app's icon with real transparency.
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP { public int Type, Width, Height, WidthBytes; public ushort Planes, BitsPixel; public IntPtr Bits; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);

    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int size, out BITMAP bmp);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);

    private static ImageSource? ShellImage(string parsingName, int size)
    {
        IShellItemImageFactory? factory = null;
        var hbm = IntPtr.Zero;
        try
        {
            SHCreateItemFromParsingName(parsingName, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out factory);
            const int biggerSizeOk = 0x1, iconOnly = 0x4;
            if (factory.GetImage(new NativeSize { Width = size, Height = size }, biggerSizeOk | iconOnly, out hbm) != 0 || hbm == IntPtr.Zero) return null;
            if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), out var bmp) == 0 || bmp.Bits == IntPtr.Zero || bmp.BitsPixel != 32) return null;
            // Copy the 32-bit premultiplied pixels across ourselves (CreateBitmapSourceFromHBitmap would drop the alpha).
            // The bitmap is bottom-up, so the rows go in reverse.
            var stride = bmp.WidthBytes;
            var pixels = new byte[stride * bmp.Height];
            for (var y = 0; y < bmp.Height; y++)
                Marshal.Copy(bmp.Bits + (bmp.Height - 1 - y) * stride, pixels, y * stride, stride);
            var src = BitmapSource.Create(bmp.Width, bmp.Height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
            src.Freeze();
            return src;
        }
        catch { return null; }
        finally
        {
            if (hbm != IntPtr.Zero) DeleteObject(hbm);
            if (factory != null) Marshal.ReleaseComObject(factory);
        }
    }
}
