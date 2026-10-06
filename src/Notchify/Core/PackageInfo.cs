using System.Runtime.InteropServices;

namespace Notchify.Core;

/// <summary>
/// Whether NotchX runs as an MSIX package (Microsoft Store) or as the plain exe from the zip.
/// A few things work differently when packaged — e.g. "Start with Windows" uses a StartupTask, not the Run key.
/// </summary>
public static class PackageInfo
{
    private const int AppModelErrorNoPackage = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int length, System.Text.StringBuilder? name);

    public static bool IsPackaged { get; } = Detect();

    /// <summary>Id of the StartupTask declared in packaging/AppxManifest.xml.</summary>
    public const string StartupTaskId = "NotchXStartup";

    private static bool Detect()
    {
        try
        {
            var length = 0;
            return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
        }
        catch { return false; } // Windows 7-era APIs missing: certainly not packaged
    }
}
