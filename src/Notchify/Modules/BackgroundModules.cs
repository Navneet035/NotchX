using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Notchify.Controls;
using Notchify.Core;
using Notchify.Services;

namespace Notchify.Modules;

/// <summary>
/// System HUDs: volume, brightness, Caps Lock and (opt-in) keystrokes animate out of the notch.
/// Can also take over the volume keys so the stock Windows flyout never shows.
/// </summary>
public sealed class HudModule : NotchModule
{
    private readonly List<IDisposable> _subs = new();
    private bool _ctrl, _shift, _alt, _win;
    private EdgeHud? _edge;

    public override string Id => "huds";
    public override string Title => "System HUDs";
    public override string Glyph => Glyphs.Volume;
    public override bool HasTab => false;
    public override string Description => "Volume, brightness, Caps Lock and keystroke HUDs in the notch (island, edge bar or gauge).";

    protected override void Start()
    {
        var h = SettingsStore.Current.Huds;
        Notch.Audio.VolumeChanged += OnVolume;
        Notch.Brightness.Changed += OnBrightness;

        if (h.CapsLockHud || h.KeystrokeHud || h.ReplaceWindowsVolumeFlyout)
            _subs.Add(Notch.Input.Subscribe(OnKey));
    }

    protected override void Stop()
    {
        Notch.Audio.VolumeChanged -= OnVolume;
        Notch.Brightness.Changed -= OnBrightness;
        foreach (var s in _subs) s.Dispose();
        _subs.Clear();
        _edge?.Close();
        _edge = null;
    }

    private void OnVolume(double level, bool muted)
    {
        if (!SettingsStore.Current.Huds.VolumeHud || Notch.Shell.IsExpanded) return;
        ShowLevel("volume", muted || level < 0.01 ? Glyphs.Mute : Glyphs.Volume, muted ? "Muted" : "Volume", muted ? 0 : level);
    }

    private void OnBrightness(int level)
    {
        if (!SettingsStore.Current.Huds.BrightnessHud || Notch.Shell.IsExpanded) return;
        ShowLevel("brightness", Glyphs.Sun, "Brightness", level / 100.0);
    }

    private void ShowLevel(string key, string glyph, string title, double value)
    {
        var h = SettingsStore.Current.Huds;
        switch (h.HudStyle)
        {
            case "EdgeBar":
                _edge ??= new EdgeHud();
                _edge.Show(glyph, value);
                return;
            case "Gauge":
                var ring = new Ring { Width = 44 * h.HudScale, Height = 44 * h.HudScale, Thickness = 5, Value = value, Stroke = Ui.Accent };
                var g = new Grid { Margin = new Thickness(14, 8, 14, 8) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                var icon = new TextBlock { Text = glyph, Style = (Style)Application.Current.FindResource("Icon"), FontSize = 16 * h.HudScale };
                g.Children.Add(new Grid { Children = { ring, icon } });
                var label = new TextBlock { Text = $"{title}  {value * 100:0}%", Style = (Style)Application.Current.FindResource("Title"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
                Grid.SetColumn(label, 1);
                g.Children.Add(label);
                Notch.Hub.Show(new Island { Key = "level-gauge", CustomContent = g, Priority = IslandPriority.High, Duration = TimeSpan.FromSeconds(1.4) });
                return;
            default:
                Notch.Hub.Show(new Island
                {
                    Key = key,
                    Glyph = glyph,
                    Title = $"{title}  {value * 100:0}%",
                    Progress = value,
                    Accent = Ui.Accent,
                    Priority = IslandPriority.High,
                    Duration = TimeSpan.FromSeconds(1.4),
                });
                return;
        }
    }

    private bool OnKey(KeyEvent k)
    {
        var h = SettingsStore.Current.Huds;
        switch (k.VirtualKey)
        {
            case 0xA2 or 0xA3 or 0x11: _ctrl = k.IsDown; return false;
            case 0xA0 or 0xA1 or 0x10: _shift = k.IsDown; return false;
            case 0xA4 or 0xA5 or 0x12: _alt = k.IsDown; return false;
            case 0x5B or 0x5C: _win = k.IsDown; return false;
        }

        if (h.ReplaceWindowsVolumeFlyout && !k.Injected && k.VirtualKey is Native.VK_VOLUME_UP or Native.VK_VOLUME_DOWN or Native.VK_VOLUME_MUTE)
        {
            if (k.IsDown)
            {
                var vk = k.VirtualKey;
                Ui.Post(() =>
                {
                    if (vk == Native.VK_VOLUME_MUTE) Notch.Audio.ToggleMute();
                    else Notch.Audio.Step(vk == Native.VK_VOLUME_UP ? 0.02 : -0.02);
                });
            }
            return true; // swallow → no stock flyout
        }

        if (h.CapsLockHud && k.VirtualKey == Native.VK_CAPITAL && !k.IsDown)
        {
            Ui.Dispatcher.BeginInvoke(() =>
            {
                var on = InputHookService.CapsLockOn;
                Notch.Hub.Show(new Island
                {
                    Key = "capslock",
                    Glyph = Glyphs.Up,
                    Title = on ? "Caps Lock on" : "Caps Lock off",
                    Accent = on ? Ui.Green : Ui.Gray,
                    Priority = IslandPriority.High,
                    Duration = TimeSpan.FromSeconds(1.2),
                });
            }, DispatcherPriority.Background);
        }

        if (h.KeystrokeHud && k.IsDown && k.VirtualKey != Native.VK_CAPITAL)
        {
            var combo = (_ctrl ? "Ctrl + " : "") + (_alt ? "Alt + " : "") + (_shift ? "Shift + " : "") + (_win ? "Win + " : "") + KeyName(k);
            Ui.Post(() =>
            {
                if (InputHookService.FocusIsPassword()) return; // never show what you type into a password box
                Notch.Hub.Show(new Island { Key = "keystroke", Glyph = Glyphs.Keyboard, Title = combo, Accent = Ui.Accent, Priority = IslandPriority.High, Duration = TimeSpan.FromSeconds(1.2) });
            });
        }
        return false;
    }

    private static string KeyName(KeyEvent k) => k.Key switch
    {
        System.Windows.Input.Key.Return => "⏎ Enter",
        System.Windows.Input.Key.Space => "Space",
        System.Windows.Input.Key.Back => "⌫ Backspace",
        System.Windows.Input.Key.Tab => "⇥ Tab",
        System.Windows.Input.Key.Escape => "Esc",
        System.Windows.Input.Key.Left => "←",
        System.Windows.Input.Key.Right => "→",
        System.Windows.Input.Key.Up => "↑",
        System.Windows.Input.Key.Down => "↓",
        >= System.Windows.Input.Key.D0 and <= System.Windows.Input.Key.D9 => ((int)(k.Key - System.Windows.Input.Key.D0)).ToString(),
        var key => key.ToString(),
    };

    /// <summary>Alternative HUD style: a slim bar pinned to the left screen edge.</summary>
    private sealed class EdgeHud : Window
    {
        private readonly Border _fill = new() { CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Bottom };
        private readonly TextBlock _icon = new();
        private readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromSeconds(1.5) };

        public EdgeHud()
        {
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; Topmost = true;
            ShowInTaskbar = false; ShowActivated = false; ResizeMode = ResizeMode.NoResize;
            var scale = SettingsStore.Current.Huds.HudScale;
            Width = 44 * scale; Height = 220 * scale;
            _fill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            _icon.Style = (Style)Application.Current.FindResource("Icon");
            var track = new Border { Width = 8, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)), Child = _fill, Margin = new Thickness(0, 8, 0, 8) };
            var dock = new DockPanel();
            DockPanel.SetDock(_icon, Dock.Bottom);
            _icon.Margin = new Thickness(0, 0, 0, 8);
            dock.Children.Add(_icon);
            dock.Children.Add(track);
            Content = new Border { Background = (Brush)Application.Current.FindResource("NotchBrush"), CornerRadius = new CornerRadius(14), Child = dock };
            Left = SystemParameters.WorkArea.Left + 12;
            Top = SystemParameters.WorkArea.Top + (SystemParameters.WorkArea.Height - Height) / 2;
            _hide.Tick += (_, _) => { _hide.Stop(); Hide(); };
        }

        public void Show(string glyph, double value)
        {
            _icon.Text = glyph;
            _fill.Height = Math.Max(0, (Height - 16 - 40) * value);
            if (!IsVisible) Show();
            _hide.Stop();
            _hide.Start();
        }
    }
}

/// <summary>Download alerts, the screenshot shelf and USB drive eject islands.</summary>
public sealed class FileAlertsModule : NotchModule
{
    private DownloadWatcher? _downloads;
    private ScreenshotWatcher? _screens;

    public override string Id => "filealerts";
    public override string Title => "File alerts";
    public override string Glyph => Glyphs.Download;
    public override bool HasTab => false;
    public override string Description => "Download-finished islands, screenshots staged on the shelf, and one-tap USB eject.";

    protected override void Start()
    {
        var h = SettingsStore.Current.Huds;
        if (h.DownloadAlerts) { _downloads = new DownloadWatcher(); _downloads.Start(); }
        if (h.ScreenshotShelf) { _screens = new ScreenshotWatcher(); _screens.Start(); }
        if (h.DriveAlerts) Notch.Drives.Start();
    }

    protected override void Stop()
    {
        _downloads?.Dispose(); _downloads = null;
        _screens?.Dispose(); _screens = null;
    }
}

/// <summary>Camera / microphone in-use indicator on the pill.</summary>
public sealed class PrivacyModule : NotchModule
{
    private readonly PrivacyMonitor _monitor = new();
    public override string Id => "privacy";
    public override string Title => "Privacy indicator";
    public override string Glyph => Glyphs.Camera;
    public override bool HasTab => false;
    public override string Description => "A green/orange dot on the pill whenever any app uses your camera or microphone.";
    protected override void Start() => _monitor.Start();
    protected override void Stop() => _monitor.Stop();
}

/// <summary>Focus / Do Not Disturb island; quiets low-priority islands while on.</summary>
public sealed class FocusModule : NotchModule
{
    private readonly FocusMonitor _monitor = new();
    public override string Id => "focus";
    public override string Title => "Focus mode";
    public override string Glyph => Glyphs.Moon;
    public override bool HasTab => false;
    public override string Description => "Shows when Do Not Disturb turns on/off and keeps low-priority islands quiet meanwhile.";
    protected override void Start() => _monitor.Start();
    protected override void Stop() => _monitor.Stop();

    public override IEnumerable<PaletteCommand> Commands => new[]
    {
        new PaletteCommand("Open Focus settings", Glyphs.Moon, () => Ui.OpenUrl("ms-settings:quietmomentshome"), null, "dnd do not disturb"),
    };
}

/// <summary>Local developer API (and Claude Code / Codex / shell hooks). Listens only when enabled in Settings.</summary>
public sealed class DeveloperApiModule : NotchModule
{
    public override string Id => "devapi";
    public override string Title => "Developer API";
    public override string Glyph => Glyphs.Code;
    public override bool HasTab => false;
    public override string Description => "127.0.0.1-only API with a per-install token for scripts, CI, agent approvals and shell activity.";

    protected override void Start()
    {
        if (SettingsStore.Current.Developer.ApiEnabled) Notch.DeveloperApi.Start();
    }

    protected override void Stop() => Notch.DeveloperApi.Stop();
}
