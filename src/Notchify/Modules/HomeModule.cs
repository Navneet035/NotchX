using System.Windows;
using Notchify.Core;
using Notchify.Services;
using Notchify.Views;

namespace Notchify.Modules;

/// <summary>Home tab: clock, weather, a compact player and quick toggles.</summary>
public sealed class HomeModule : NotchModule
{
    public override string Id => "home";
    public override string Title => "Home";
    public override string Glyph => Glyphs.Home;
    public override string Description => "Clock, weather, mini player, volume, brightness, sound output, Bluetooth devices, lock and quick toggles — arrange them how you like.";

    protected override void Start() => Notch.Weather.Start();

    public override FrameworkElement CreateView() => new HomeView();

    public override IEnumerable<PaletteCommand> Commands => new[]
    {
        new PaletteCommand("Lock screen", Glyphs.Lock, () => { Notch.Shell.Collapse(); Native.LockWorkStation(); }, "Win+L", "lock pc computer away"),
        new PaletteCommand("Edit notch layout", Glyphs.Edit, () => { Notch.Shell.OpenTab("home"); LayoutEditor.IsEditing = true; }, null, "arrange reorder hide tabs widgets customise customize"),
        new PaletteCommand(Notch.KeepAwake.IsActive ? "Keep awake: turn off" : "Keep awake: turn on", Glyphs.Bolt, Notch.KeepAwake.Toggle, null, "caffeine coffee amphetamine awake sleep"),
        new PaletteCommand("Keep awake for 1 hour", Glyphs.Bolt, () => Notch.KeepAwake.Enable(TimeSpan.FromHours(1)), null, "caffeine"),
        new PaletteCommand("Keep awake for 3 hours", Glyphs.Bolt, () => Notch.KeepAwake.Enable(TimeSpan.FromHours(3)), null, "caffeine"),
        new PaletteCommand(Notch.Audio.MicMuted ? "Unmute microphone" : "Mute microphone", Glyphs.MicOff, () => Notch.Audio.MicMuted = !Notch.Audio.MicMuted, null, "mic mute zoom teams meet"),
        new PaletteCommand("Refresh weather", Glyphs.Sun, () => _ = Notch.Weather.RefreshAsync(), null, "weather"),
        new PaletteCommand("Toggle hide from screen capture", Glyphs.Shield, () =>
        {
            SettingsStore.Current.Behavior.HideFromScreenCapture = !SettingsStore.Current.Behavior.HideFromScreenCapture;
            SettingsStore.NotifyChanged();
        }, null, "privacy obs zoom recording share"),
    };

    // Exposed for bindings in HomeView.
    public MediaService Media => Notch.Media;
    public WeatherService Weather => Notch.Weather;
    public AudioService Audio => Notch.Audio;
    public BrightnessService Brightness => Notch.Brightness;
    public KeepAwakeService KeepAwake => Notch.KeepAwake;
}
