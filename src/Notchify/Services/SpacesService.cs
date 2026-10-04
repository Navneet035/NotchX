using Microsoft.Win32;
using System.Windows.Threading;
using Notchify.Core;

namespace Notchify.Services;

/// <summary>
/// Spaces = Windows virtual desktops. Windows has no public API for them, so we read the list and the
/// current desktop from the registry (where Explorer keeps them) and switch with the standard shortcuts
/// (Win+Ctrl+←/→, Win+Ctrl+D, Win+Ctrl+F4, Win+Tab).
/// </summary>
public sealed class SpacesService : ObservableObject
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";
    private const ushort VK_LEFT = 0x25, VK_RIGHT = 0x27, VK_D = 0x44, VK_F4 = 0x73, VK_TAB = 0x09;

    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private int _users;
    private int _index;
    private int _count = 1;
    private string _name = "Desktop 1";

    public SpacesService() => _poll.Tick += (_, _) => Refresh(announce: true);

    public int Index { get => _index; private set => Set(ref _index, value); }
    public int Count { get => _count; private set => Set(ref _count, value); }
    public string Name { get => _name; private set => Set(ref _name, value); }
    public string Position => $"{Index + 1} of {Count}";

    public void Start()
    {
        Refresh(announce: false);
        Apply();
        SettingsStore.Changed += Apply;
    }

    /// <summary>Poll while something shows the current desktop, or while the switch island is on.</summary>
    public void Acquire() { _users++; Apply(); Refresh(false); }
    public void Release() { _users = Math.Max(0, _users - 1); Apply(); }

    private void Apply()
    {
        if (_users > 0 || SettingsStore.Current.Spaces.ShowIsland) _poll.Start();
        else _poll.Stop();
    }

    public void Refresh(bool announce)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            var ids = ReadGuids(key?.GetValue("VirtualDesktopIDs") as byte[]);
            var current = ReadGuids(key?.GetValue("CurrentVirtualDesktop") as byte[]).FirstOrDefault();
            if (current == Guid.Empty)
            {
                // Older builds keep the current desktop per session.
                using var session = Registry.CurrentUser.OpenSubKey(
                    $@"Software\Microsoft\Windows\CurrentVersion\Explorer\SessionInfo\{System.Diagnostics.Process.GetCurrentProcess().SessionId}\VirtualDesktops");
                current = ReadGuids(session?.GetValue("CurrentVirtualDesktop") as byte[]).FirstOrDefault();
            }
            // With a single desktop Windows doesn't write the list at all.
            var count = Math.Max(1, ids.Count);
            var index = Math.Max(0, ids.IndexOf(current));
            string? name = null;
            if (current != Guid.Empty)
            {
                using var d = Registry.CurrentUser.OpenSubKey($@"{Key}\Desktops\{current:B}");
                name = d?.GetValue("Name") as string;
            }
            name = string.IsNullOrWhiteSpace(name) ? $"Desktop {index + 1}" : name;

            var changed = index != Index || count != Count || name != Name;
            var switched = index != Index || name != Name;
            Index = index;
            Count = count;
            Name = name;
            if (changed) Raise(nameof(Position));
            if (announce && switched && SettingsStore.Current.Spaces.ShowIsland)
                Notch.Hub.Show(new Island
                {
                    Key = "spaces",
                    Glyph = Glyphs.Apps,
                    Title = Name,
                    Message = $"Desktop {Position}",
                    Accent = Ui.Teal,
                    Priority = IslandPriority.Low,
                    Duration = TimeSpan.FromSeconds(1.6),
                });
        }
        catch (Exception ex) { Log.Info("spaces: " + ex.Message); }
    }

    private static List<Guid> ReadGuids(byte[]? bytes)
    {
        var list = new List<Guid>();
        if (bytes == null) return list;
        for (var i = 0; i + 16 <= bytes.Length; i += 16) list.Add(new Guid(bytes.AsSpan(i, 16)));
        return list;
    }

    public void Next() => Native.SendChord(Native.VK_LWIN, Native.VK_CONTROL, VK_RIGHT);
    public void Previous() => Native.SendChord(Native.VK_LWIN, Native.VK_CONTROL, VK_LEFT);
    public void New() => Native.SendChord(Native.VK_LWIN, Native.VK_CONTROL, VK_D);
    public void CloseCurrent() => Native.SendChord(Native.VK_LWIN, Native.VK_CONTROL, VK_F4);
    public void TaskView() => Native.SendChord(Native.VK_LWIN, VK_TAB);
}
