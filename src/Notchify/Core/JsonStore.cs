using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Notchify.Core;

public static class Paths
{
    public static string Root { get; } = Ensure(MigrateRoot());

    /// <summary>
    /// The app was renamed from Notchify to NotchX. On first run, carry the old data folder over
    /// (settings, clipboard history, shelf, notes…) so nothing is lost. The old folder is left as a backup.
    /// </summary>
    private static string MigrateRoot()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var root = Path.Combine(appData, "NotchX");
        var old = Path.Combine(appData, "Notchify");
        if (Directory.Exists(root) || !Directory.Exists(old)) return root;
        try
        {
            CopyDirectory(old, root);
            // Clipboard images and shelf captures are stored with full paths; point them at the new folder.
            foreach (var file in Directory.GetFiles(Path.Combine(root, "data"), "*.json"))
            {
                var text = File.ReadAllText(file);
                var moved = text.Replace(old.Replace("\\", "\\\\"), root.Replace("\\", "\\\\"), StringComparison.OrdinalIgnoreCase);
                if (moved != text) File.WriteAllText(file, moved);
            }
        }
        catch { /* start fresh rather than fail to start */ }
        return root;
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(from))
            CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    public static string Data { get; } = Ensure(Path.Combine(Root, "data"));
    public static string Clips { get; } = Ensure(Path.Combine(Root, "clips"));
    public static string Shelf { get; } = Ensure(Path.Combine(Root, "shelf"));
    public static string Cache { get; } = Ensure(Path.Combine(Root, "cache"));
    public static string Icons { get; } = Ensure(Path.Combine(Root, "icons"));
    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string Downloads
    {
        get
        {
            // FOLDERID_Downloads
            if (Native.SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var p) == 0)
                return p;
            return Path.Combine(UserProfile, "Downloads");
        }
    }

    public static string Screenshots =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}

/// <summary>
/// Tiny JSON persistence helper. Each feature stores its own file in %APPDATA%\NotchX\data
/// so data is easy to inspect, back up, or sync.
/// </summary>
public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly object Gate = new();

    public static T Load<T>(string name) where T : new() => LoadFile<T>(Path.Combine(Paths.Data, name + ".json"));

    public static void Save<T>(string name, T value) => SaveFile(Path.Combine(Paths.Data, name + ".json"), value);

    public static T LoadFile<T>(string path) where T : new()
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T();
        }
        catch (Exception ex)
        {
            Log.Error($"Could not read {path}", ex);
            try { File.Copy(path, path + ".broken", true); } catch { }
        }
        return new T();
    }

    public static void SaveFile<T>(string path, T value)
    {
        try
        {
            var json = JsonSerializer.Serialize(value, Options);
            lock (Gate)
            {
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, path, true);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Could not write {path}", ex);
        }
    }
}

public static class Log
{
    public static readonly string FilePath = Path.Combine(Paths.Root, "notchx.log");
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}";
        System.Diagnostics.Debug.Write(line);
        lock (Gate)
        {
            try
            {
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > 2_000_000) info.Delete();
                File.AppendAllText(FilePath, line);
            }
            catch { }
        }
    }
}
