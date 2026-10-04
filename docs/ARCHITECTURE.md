# Architecture & customising

NotchX is a single WPF app on .NET 8 (the code keeps its original `Notchify` namespace and folder name). Everything is a **module**, so features can be switched off,
reordered, popped out, or added without touching the shell.

```
src/Notchify/
├── App.xaml(.cs)          startup, tray icon, global hotkeys, single instance
├── Core/
│   ├── Notch.cs           service locator: Notch.Hub, Notch.Media, Notch.Audio, …
│   ├── ActivityHub.cs     live activities (pill) + islands (notifications/HUDs)
│   ├── Module.cs          NotchModule base class, INotchShell, PaletteCommand
│   ├── ModuleRegistry.cs  the list of modules, order + on/off state
│   ├── Settings.cs        every option → %APPDATA%\NotchX\settings.json
│   ├── JsonStore.cs       per-feature data files in %APPDATA%\NotchX\data
│   └── Native.cs          Win32 interop
├── Services/              platform plumbing (media, audio, clipboard, bluetooth, …)
├── Modules/               features (one file each, most with their own tab)
├── Views/                 XAML views for the bigger tabs
├── Controls/              spectrum bars, sparkline, ring, bar chart
└── Shell/                 the notch window, command palette, settings, pop-outs, theme
```

## The notch window

`Shell/NotchWindow` is one transparent, top-most, click-through-where-empty window. The black pill inside morphs between:

| State | Shows | Triggered by |
|---|---|---|
| **Collapsed** | live activities side by side (`Notch.Hub.Activities`) | default |
| **Island** | one transient notification / HUD (`Notch.Hub.Current`) | `Notch.Hub.Show(...)` |
| **Expanded** | tab bar + the selected module's view | hover, click, hotkey, `Notch.Shell.OpenTab(id)` |

Tab views are created once and cached, so switching tabs never kills running work.

## Putting things on the notch

```csharp
// A transient island
Notch.Hub.Notify(Glyphs.Download, "Export finished", "report.pdf", Ui.Green);

// A richer island with buttons
Notch.Hub.Show(new Island
{
    Glyph = Glyphs.Shield, Title = "Deploy to prod?", Sticky = true, Priority = IslandPriority.High,
    Actions = { new IslandAction("Cancel", () => { }), new IslandAction("Deploy", Deploy, Primary: true) },
});

// A live activity on the collapsed pill (update it by calling Upsert again with the same id)
Notch.Hub.Upsert("build", a => { a.Glyph = Glyphs.Code; a.Text = "62%"; a.Progress = 0.62; a.Priority = 40; });
Notch.Hub.Remove("build");
```

Islands with the same `Key` update in place (that's how the volume HUD avoids stacking up).
Low-priority islands are suppressed while Windows Do Not Disturb is on.

## Writing a module

```csharp
public sealed class HelloModule : NotchModule
{
    public override string Id => "hello";
    public override string Title => "Hello";
    public override string Glyph => Glyphs.Star;
    public override string Description => "Says hello.";

    protected override void Start() { /* subscribe to services, start timers */ }
    protected override void Stop()  { /* undo everything Start did */ }

    public override FrameworkElement CreateView() =>
        new TextBlock { Text = "Hello from the notch!", Style = Views.ViewKit.S("Title") };

    public override IEnumerable<PaletteCommand> Commands => new[]
    {
        new PaletteCommand("Say hello", Glyphs.Star, () => Notch.Hub.Notify(Glyphs.Star, "Hello!")),
    };
}
```

Then add `new HelloModule()` to `ModuleRegistry.CreateAll()`. It automatically gets:
a tab (with `Ctrl+number`), a switch + ordering in Settings › Features, a pop-out window, and its commands in the palette.
Set `HasTab => false` for background-only features.

Guidelines:

* **Idle cost matters.** Start timers / capture only while something is visible (`IsVisibleChanged`), like the Stats and spectrum code.
* Services raise events on background threads — marshal with `Ui.Post(...)` before touching UI or `Notch.Hub`.
* Persist feature data with `JsonStore.Load/Save`, options in `AppSettings`.
* Never block the UI thread; never let a feature crash the app (exceptions are logged to `%APPDATA%\NotchX\notchx.log`).

## Theming

All colours and control styles live in `Shell/Theme.xaml`. `AccentBrush` and `NotchBrush` are replaced at runtime from
Settings › Appearance. Icons use the Segoe Fluent Icons font (`Glyphs.*`).
