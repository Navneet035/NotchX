using Notchify.Modules;

namespace Notchify.Core;

/// <summary>
/// Owns every module, their user-chosen order and on/off state (persisted in settings.json → Modules).
/// </summary>
public sealed class ModuleRegistry
{
    private readonly List<NotchModule> _all = new();

    public event Action? Changed;

    /// <summary>All modules in the user's order.</summary>
    public IReadOnlyList<NotchModule> All => _all;

    public IEnumerable<NotchModule> Enabled => _all.Where(IsEnabled);

    /// <summary>Tabs shown in the notch, in the user's order.</summary>
    public IEnumerable<NotchModule> Tabs => Enabled.Where(m => m.HasTab && IsTabVisible(m.Id));

    /// <summary>Every enabled module that can have a tab, including ones the user hid (for layout editing).</summary>
    public IEnumerable<NotchModule> TabCandidates => Enabled.Where(m => m.HasTab);

    public bool IsTabVisible(string id) => SettingsStore.Current.Modules.FirstOrDefault(e => e.Id == id)?.ShowTab ?? true;

    public void SetTabVisible(string id, bool visible)
    {
        var entry = SettingsStore.Current.Modules.FirstOrDefault(e => e.Id == id);
        if (entry == null || entry.ShowTab == visible) return;
        // Never hide the last visible tab, or the open notch would be empty.
        if (!visible && Tabs.Count() <= 1) return;
        entry.ShowTab = visible;
        SettingsStore.Save();
        Changed?.Invoke();
    }

    /// <summary>Move a module so it sits where <paramref name="targetId"/> is now (drag-to-reorder).</summary>
    public void MoveTo(string id, string targetId)
    {
        if (id == targetId) return;
        var entries = SettingsStore.Current.Modules;
        var from = entries.FindIndex(e => e.Id == id);
        var to = entries.FindIndex(e => e.Id == targetId);
        if (from < 0 || to < 0) return;
        var entry = entries[from];
        entries.RemoveAt(from);
        entries.Insert(to, entry);
        var m = _all.First(x => x.Id == id);
        _all.Remove(m);
        _all.Insert(_all.FindIndex(x => x.Id == targetId) + (from < to ? 1 : 0), m);
        SettingsStore.Save();
        Changed?.Invoke();
    }

    public NotchModule? Get(string id) => _all.FirstOrDefault(m => m.Id == id);

    /// <summary>The built-in feature set. Add your own module here.</summary>
    private static IEnumerable<NotchModule> CreateAll() => new NotchModule[]
    {
        new HomeModule(),
        new NowPlayingModule(),
        new ClipboardModule(),
        new ShelfModule(),
        new DocumentsModule(),
        new TimerModule(),
        new NotesModule(),
        new CalendarModule(),
        new LauncherModule(),
        new SearchModule(),
        new SnippetsModule(),
        new SoundModule(),
        new DevicesModule(),
        new AiUsageModule(),
        new StatsModule(),
        new CuelyModule(),
        new ToolsModule(),
        // Background features (no tab)
        new HudModule(),
        new FileAlertsModule(),
        new PrivacyModule(),
        new FocusModule(),
        new WindowSnapModule(),
        new DeveloperApiModule(),
    };

    public void Initialize()
    {
        var defaults = CreateAll().ToList();
        var created = defaults.ToDictionary(m => m.Id);
        var entries = SettingsStore.Current.Modules;

        // Keep the user's order.
        foreach (var e in entries.ToList())
        {
            if (created.Remove(e.Id, out var m)) _all.Add(m);
            else entries.Remove(e);
        }
        // Modules added in newer versions go right after their neighbour in the default order
        // (e.g. Documents next to Shelf), or at the end.
        foreach (var m in defaults.Where(d => created.ContainsKey(d.Id)))
        {
            var before = defaults.Take(defaults.IndexOf(m)).Select(d => d.Id).LastOrDefault(id => entries.Any(e => e.Id == id));
            var at = before == null ? entries.Count : entries.FindIndex(e => e.Id == before) + 1;
            entries.Insert(at, new ModuleEntry { Id = m.Id, Enabled = m.DefaultEnabled });
            _all.Insert(Math.Min(at, _all.Count), m);
        }
        SettingsStore.Save();
    }

    public bool IsEnabled(NotchModule m) =>
        SettingsStore.Current.Modules.FirstOrDefault(e => e.Id == m.Id)?.Enabled ?? m.DefaultEnabled;

    public void StartEnabled()
    {
        foreach (var m in Enabled) m.StartInternal();
    }

    public void StopAll()
    {
        foreach (var m in _all) m.StopInternal();
    }

    public void SetEnabled(string id, bool enabled)
    {
        var entry = SettingsStore.Current.Modules.FirstOrDefault(e => e.Id == id);
        var module = Get(id);
        if (entry == null || module == null) return;
        entry.Enabled = enabled;
        if (enabled) module.StartInternal(); else module.StopInternal();
        SettingsStore.Save();
        Changed?.Invoke();
    }

    public void Move(string id, int delta)
    {
        var entries = SettingsStore.Current.Modules;
        var i = entries.FindIndex(e => e.Id == id);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= entries.Count) return;
        (entries[i], entries[j]) = (entries[j], entries[i]);
        var mi = _all.FindIndex(m => m.Id == id);
        var mj = mi + delta;
        if (mi >= 0 && mj >= 0 && mj < _all.Count) (_all[mi], _all[mj]) = (_all[mj], _all[mi]);
        SettingsStore.Save();
        Changed?.Invoke();
    }

    /// <summary>Restart a running module so it picks up changed settings.</summary>
    public void Restart(string id)
    {
        var m = Get(id);
        if (m == null || !IsEnabled(m)) return;
        m.StopInternal();
        m.StartInternal();
    }

    public IEnumerable<PaletteCommand> AllCommands()
    {
        var index = 1;
        foreach (var tab in Tabs)
        {
            var t = tab;
            yield return new PaletteCommand($"Open {t.Title}", t.Glyph, () => Notch.Shell.OpenTab(t.Id),
                index <= 9 ? $"Ctrl+{index}" : "Tab", "tab go " + t.Id);
            index++;
        }
        foreach (var m in Enabled)
        {
            IEnumerable<PaletteCommand> cmds;
            try { cmds = m.Commands.ToList(); }
            catch (Exception ex) { Log.Error($"commands for {m.Id}", ex); continue; }
            foreach (var c in cmds) yield return c;
        }
    }
}
