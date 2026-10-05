using System.Windows;

namespace Notchify.Core;

/// <summary>An action shown in the command palette.</summary>
public sealed record PaletteCommand(string Title, string Glyph, Action Run, string? Subtitle = null, string? Keywords = null);

/// <summary>
/// A feature of Notchify. To add your own: subclass this, then register it in <see cref="ModuleRegistry.CreateAll"/>.
/// Modules with <see cref="HasTab"/> get a tab in the expanded notch and can be popped out into their own window.
/// Background-only modules (HUDs, watchers) just implement Start/Stop.
/// </summary>
public abstract class NotchModule
{
    public abstract string Id { get; }
    public abstract string Title { get; }
    public abstract string Glyph { get; }
    /// <summary>Label under the tab icon; keep it to one short word.</summary>
    public virtual string ShortTitle => Title;
    public virtual string Description => "";
    public virtual bool HasTab => true;
    public virtual bool DefaultEnabled => true;

    public bool IsRunning { get; private set; }

    internal void StartInternal()
    {
        if (IsRunning) return;
        try { Start(); IsRunning = true; }
        catch (Exception ex) { Log.Error($"Module {Id} failed to start", ex); }
    }

    internal void StopInternal()
    {
        if (!IsRunning) return;
        try { Stop(); }
        catch (Exception ex) { Log.Error($"Module {Id} failed to stop", ex); }
        IsRunning = false;
    }

    protected virtual void Start() { }
    protected virtual void Stop() { }

    /// <summary>Builds the tab content. May be called more than once (pop-out windows get their own instance).</summary>
    public virtual FrameworkElement? CreateView() => null;

    public virtual IEnumerable<PaletteCommand> Commands => Array.Empty<PaletteCommand>();
}

/// <summary>What modules can ask of the notch window.</summary>
public interface INotchShell
{
    void OpenTab(string moduleId);
    void Expand();
    void Collapse();
    void Toggle();
    bool IsExpanded { get; }
    /// <summary>Keep the expanded panel open even when the mouse leaves (teleprompter, pinned).</summary>
    bool KeepOpen { get; set; }
    /// <summary>The app that had focus before the user interacted with the notch (target for paste).</summary>
    IntPtr LastExternalWindow { get; }
    void PopOut(string moduleId);
    /// <summary>Open Settings, optionally on a page ("Weather", "Home"…).</summary>
    void ShowSettings(string? page = null);
    void ApplySettings();
}
