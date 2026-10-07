namespace Notchify.Core;

public static class AppInfo
{
    public const string Name = "NotchX";
    public const string Version = "0.4.0";
    public const string Developer = "Navneet";

    /// <summary>Set this to your fork's URL; it's used for the docs links and the HTTP User-Agent.</summary>
    public const string RepoUrl = "https://github.com/Navneet035/NotchX";

    /// <summary>The app icon, for window title bars.</summary>
    public static System.Windows.Media.ImageSource? Icon
    {
        get
        {
            try { return System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/NotchX.ico")); }
            catch { return null; }
        }
    }
}
