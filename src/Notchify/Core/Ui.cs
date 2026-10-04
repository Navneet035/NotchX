using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Notchify.Core;

public static class Ui
{
    public static Dispatcher Dispatcher => Application.Current.Dispatcher;

    /// <summary>Run on the UI thread (async, fire-and-forget).</summary>
    public static void Post(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null) return;
        if (d.CheckAccess()) action();
        else d.BeginInvoke(action);
    }

    public static SolidColorBrush Brush(string hex)
    {
        try
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }
        catch { return Brushes.White; }
    }

    public static Color Color(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return Colors.White; }
    }

    public static Brush Accent => (Application.Current?.TryFindResource("AccentBrush") as Brush) ?? Brushes.DodgerBlue;

    public static readonly Brush Green = Brush("#FF30D158");
    public static readonly Brush Orange = Brush("#FFFF9F0A");
    public static readonly Brush Red = Brush("#FFFF453A");
    public static readonly Brush Yellow = Brush("#FFFFD60A");
    public static readonly Brush Purple = Brush("#FFBF5AF2");
    public static readonly Brush Teal = Brush("#FF64D2FF");
    public static readonly Brush Gray = Brush("#FF8E8E93");

    public static BitmapImage? LoadImage(string path, int decodeWidth = 0)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (decodeWidth > 0) img.DecodePixelWidth = decodeWidth;
            img.UriSource = new Uri(path);
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    public static BitmapImage? LoadImage(Stream stream, int decodeWidth = 0)
    {
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            if (decodeWidth > 0) img.DecodePixelWidth = decodeWidth;
            img.StreamSource = stream;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }

    private static readonly Dictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Shell icon for an exe, shortcut, or any file.</summary>
    public static ImageSource? FileIcon(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (IconCache.TryGetValue(path, out var cached)) return cached;
        ImageSource? result = null;
        try
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (icon != null)
                {
                    var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    src.Freeze();
                    result = src;
                }
            }
        }
        catch { }
        IconCache[path] = result;
        return result;
    }

    public static string FormatBytes(double bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var i = 0;
        while (bytes >= 1024 && i < units.Length - 1) { bytes /= 1024; i++; }
        return $"{bytes:0.#} {units[i]}";
    }

    public static string FormatTokens(double n) =>
        n >= 1_000_000_000 ? $"{n / 1_000_000_000:0.##}B" :
        n >= 1_000_000 ? $"{n / 1_000_000:0.##}M" :
        n >= 1_000 ? $"{n / 1_000:0.#}K" : $"{n:0}";

    public static string FormatSpan(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";

    public static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Error("open " + url, ex); }
    }

    public static void RevealInExplorer(string path)
    {
        try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (Exception ex) { Log.Error("reveal", ex); }
    }
}

/// <summary>Segoe Fluent Icons / Segoe MDL2 Assets code points used throughout the UI.</summary>
public static class Glyphs
{
    public const string Home = "";
    public const string Music = "";
    public const string Play = "";
    public const string Pause = "";
    public const string Next = "";
    public const string Previous = "";
    public const string Shuffle = "";
    public const string RepeatAll = "";
    public const string RepeatOne = "";
    public const string Volume = "";
    public const string Mute = "";
    public const string Mic = "";
    public const string MicOff = "";
    public const string Clipboard = "";
    public const string Pin = "";
    public const string Unpin = "";
    public const string Folder = "";
    public const string Document = "";
    public const string Stopwatch = "";
    public const string Clock = "";
    public const string Bolt = "";
    public const string Search = "";
    public const string Translate = "";
    public const string Calculator = "";
    public const string Settings = "";
    public const string Close = "";
    public const string Add = "";
    public const string Delete = "";
    public const string Copy = "";
    public const string Link = "";
    public const string Mail = "";
    public const string Color = "";
    public const string Calendar = "";
    public const string Sun = "";
    public const string Moon = "";
    public const string Bluetooth = "";
    public const string Headphones = "";
    public const string Keyboard = "";
    public const string Mouse = "";
    public const string Camera = "";
    public const string Download = "";
    public const string Crop = "";
    public const string Usb = "";
    public const string Apps = "";
    public const string Note = "";
    public const string Edit = "";
    public const string Lock = "";
    public const string Unlock = "";
    public const string Shield = "";
    public const string PopOut = "";
    public const string Chart = "";
    public const string Robot = "";
    public const string Check = "";
    public const string Bell = "";
    public const string Code = "";
    public const string Terminal = "";
    public const string Package = "";
    public const string Photo = "";
    public const string Speed = "";
    public const string Reading = "";
    public const string Monitor = "";
    public const string Network = "";
    public const string Warning = "";
    public const string Info = "";
    public const string Refresh = "";
    public const string Globe = "";
    public const string Snap = "";
    public const string Up = "";
    public const string Down = "";
    public const string Save = "";
    public const string Text = "";
    public const string Star = "";
    public const string Video = "";

    public const string View = "";
    public const string Hide = "";
    public const string Left = "";
    public const string Right = "";
    public const string Move = "";
    public const string Accept = "";

    /// <summary>Battery glyph for a 0-100 level (EBA0..EBAA, charging EBAB..EBB5).</summary>
    public static string Battery(int percent, bool charging)
    {
        var step = Math.Clamp((int)Math.Round(percent / 10.0), 0, 10);
        return ((char)((charging ? 0xEBAB : 0xEBA0) + step)).ToString();
    }
}
