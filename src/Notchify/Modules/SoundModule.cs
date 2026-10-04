using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Notchify.Core;
using Notchify.Services;
using static Notchify.Views.ViewKit;

namespace Notchify.Modules;

/// <summary>
/// Sound: output switcher, master volume, one system-wide mic mute, and a per-app mixer
/// (turn one app down or mute it without touching the system volume).
/// </summary>
public sealed class SoundModule : NotchModule
{
    public override string Id => "sound";
    public override string Title => "Sound";
    public override string Glyph => Glyphs.Volume;
    public override string Description => "Audio output switcher, per-app volume mixer and a global microphone mute.";

    public override FrameworkElement CreateView()
    {
        var audio = Notch.Audio;

        // Output picker
        var outputs = new ComboBox { ItemsSource = audio.Outputs, DisplayMemberPath = nameof(AudioDevice.Name), MinWidth = 200 };
        var syncing = false;
        void SyncOutput() { syncing = true; outputs.SelectedItem = audio.Outputs.FirstOrDefault(o => o.IsDefault); syncing = false; }
        outputs.SelectionChanged += (_, _) =>
        {
            if (!syncing && outputs.SelectedItem is AudioDevice d && !d.IsDefault) audio.SetDefaultOutput(d.Id);
        };
        audio.Outputs.CollectionChanged += (_, _) => SyncOutput();

        var master = new Slider { Minimum = 0, Maximum = 100 };
        master.SetBinding(Slider.ValueProperty, new Binding(nameof(AudioService.Volume)) { Source = audio, Mode = BindingMode.TwoWay });
        var muteBtn = IconButton(Glyphs.Volume, "Mute", (_, _) => audio.ToggleMute());
        muteBtn.SetBinding(ContentControl.ContentProperty, new Binding(nameof(AudioService.VolumeGlyph)) { Source = audio });
        var mic = IconButton(Glyphs.Mic, "Mute microphone in every app", (_, _) =>
        {
            audio.MicMuted = !audio.MicMuted;
            Notch.Hub.Notify(audio.MicGlyph, audio.MicMuted ? "Microphone muted" : "Microphone on", "All apps", audio.MicMuted ? Ui.Red : Ui.Green, IslandPriority.Normal, 1.5, "mic");
        });
        mic.SetBinding(ContentControl.ContentProperty, new Binding(nameof(AudioService.MicGlyph)) { Source = audio });

        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(mic);
        right.Children.Add(IconButton(Glyphs.Settings, "Windows sound settings", (_, _) => Ui.OpenUrl("ms-settings:sound")));
        DockPanel.SetDock(right, Dock.Right);
        DockPanel.SetDock(outputs, Dock.Left);
        top.Children.Add(right);
        top.Children.Add(outputs);
        top.Children.Add(Columns((muteBtn, Auto), (master, Star())));
        ((FrameworkElement)top.Children[2]).Margin = new Thickness(12, 0, 12, 0);

        // Per-app mixer
        var sessions = new ListBox { ItemsSource = audio.Sessions };
        sessions.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(@"
<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'>
  <Grid>
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width='28' /><ColumnDefinition Width='150' /><ColumnDefinition Width='*' /><ColumnDefinition Width='Auto' /><ColumnDefinition Width='Auto' />
    </Grid.ColumnDefinitions>
    <Image Source='{Binding Icon}' Width='18' Height='18' />
    <TextBlock Grid.Column='1' Text='{Binding Name}' Style='{DynamicResource Body}' VerticalAlignment='Center' Margin='4,0,8,0' />
    <Slider Grid.Column='2' Minimum='0' Maximum='100' Value='{Binding Volume, Mode=TwoWay}' />
    <TextBlock Grid.Column='3' Text='{Binding Volume, StringFormat={}{0:0}}' Style='{DynamicResource Caption}' Width='28' TextAlignment='Right' VerticalAlignment='Center' />
    <ToggleButton Grid.Column='4' Style='{DynamicResource IconToggle}' Content='{Binding MuteGlyph}' IsChecked='{Binding Muted, Mode=TwoWay}' Margin='6,0,0,0' />
  </Grid>
</DataTemplate>");

        var eqNote = Text("Per-app equaliser needs a system audio driver (APO) on Windows — see docs/FEATURES.md.", "Caption", 10);
        eqNote.Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("FaintTextBrush");
        eqNote.Margin = new Thickness(2, 6, 0, 0);

        var dock = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(eqNote, Dock.Bottom);
        dock.Children.Add(top);
        dock.Children.Add(eqNote);
        dock.Children.Add(sessions);
        dock.IsVisibleChanged += (_, _) =>
        {
            if (!dock.IsVisible) return;
            audio.RefreshDevices();
            audio.RefreshSessions();
            SyncOutput();
        };
        return dock;
    }

    public override IEnumerable<PaletteCommand> Commands =>
        Notch.Audio.Outputs.Select(o => new PaletteCommand($"Switch output to {o.Name}", o.Glyph, () => Notch.Audio.SetDefaultOutput(o.Id), o.IsDefault ? "current" : null, "audio sound speakers headphones"))
            .Append(new PaletteCommand("Toggle mute", Glyphs.Mute, Notch.Audio.ToggleMute, null, "sound volume"));
}
