using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Notchify.Core;
using Notchify.Services;

namespace Notchify.Views;

public partial class NowPlayingView : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool _dragging;
    private bool _syncing;
    private AppAudioSession? _session;

    public NowPlayingView()
    {
        InitializeComponent();
        DataContext = Notch.Modules.Get("music");
        _timer.Tick += (_, _) => Sync();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                Notch.Media.PropertyChanged += OnMedia;
                Notch.Lyrics.PropertyChanged += OnLyrics;
                RefreshAll();
                _timer.Start();
            }
            else
            {
                Notch.Media.PropertyChanged -= OnMedia;
                Notch.Lyrics.PropertyChanged -= OnLyrics;
                _timer.Stop();
            }
        };
    }

    private void RefreshAll()
    {
        LyricsColumn.Width = SettingsStore.Current.Media.Lyrics ? new GridLength(0.9, GridUnitType.Star) : new GridLength(0);
        LyricsCard.Visibility = SettingsStore.Current.Media.Lyrics ? Visibility.Visible : Visibility.Collapsed;
        BindLyrics();
        FindSession();
        Sync();
    }

    private void OnMedia(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MediaService.SourceAppId)) FindSession();
        Sync();
    }

    private void OnLyrics(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LyricsService.Lines)) BindLyrics();
        if (e.PropertyName == nameof(LyricsService.CurrentIndex)) HighlightLyric();
    }

    private void FindSession()
    {
        Notch.Audio.RefreshSessions();
        _session = Notch.Audio.FindSession(Notch.Media.SourceProcess);
        _syncing = true;
        AppVolume.IsEnabled = _session != null;
        AppVolume.Value = _session?.Volume ?? 100;
        _syncing = false;
    }

    private void Sync()
    {
        var m = Notch.Media;
        _syncing = true;
        if (!_dragging)
        {
            Scrubber.Maximum = Math.Max(1, m.Duration.TotalSeconds);
            Scrubber.Value = Math.Min(Scrubber.Maximum, m.Position.TotalSeconds);
        }
        Scrubber.IsEnabled = m.CanSeek;
        Elapsed.Text = Ui.FormatSpan(TimeSpan.FromSeconds(Scrubber.Value));
        Remaining.Text = "-" + Ui.FormatSpan(TimeSpan.FromSeconds(Math.Max(0, Scrubber.Maximum - Scrubber.Value)));
        ShuffleToggle.IsChecked = m.Shuffle;
        RepeatToggle.IsChecked = m.Repeat != 0;
        foreach (ComboBoxItem item in Speed.Items)
            if (Math.Abs(double.Parse((string)item.Tag, System.Globalization.CultureInfo.InvariantCulture) - m.PlaybackRate) < 0.01)
                Speed.SelectedItem = item;
        _syncing = false;
        Notch.Lyrics.Tick(m.Position);
    }

    private void BindLyrics()
    {
        var lines = Notch.Lyrics.Lines;
        LyricsList.ItemsSource = lines;
        NoLyrics.Visibility = lines is { Count: > 0 } ? Visibility.Collapsed : Visibility.Visible;
        HighlightLyric();
    }

    private void HighlightLyric()
    {
        var idx = Notch.Lyrics.CurrentIndex;
        var faint = (Brush)FindResource("FaintTextBrush");
        var bright = (Brush)FindResource("TextBrush");
        for (var i = 0; i < LyricsList.Items.Count; i++)
        {
            if (LyricsList.ItemContainerGenerator.ContainerFromIndex(i) is not ContentPresenter cp) continue;
            if (VisualTreeHelper.GetChildrenCount(cp) == 0 || VisualTreeHelper.GetChild(cp, 0) is not TextBlock tb) continue;
            var current = i == idx;
            tb.Foreground = current ? bright : faint;
            tb.FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal;
            if (current)
            {
                // Keep the current line about a third of the way down.
                var pos = cp.TransformToAncestor(LyricsList).Transform(new Point(0, 0)).Y;
                LyricsScroll.ScrollToVerticalOffset(Math.Max(0, pos - LyricsScroll.ViewportHeight / 3));
            }
        }
    }

    private void Scrubber_DragStarted(object sender, DragStartedEventArgs e) => _dragging = true;

    private void Scrubber_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _dragging = false;
        Notch.Media.Seek(TimeSpan.FromSeconds(Scrubber.Value));
    }

    private void Scrubber_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) Notch.Media.Seek(TimeSpan.FromSeconds(Scrubber.Value));
    }

    private void AppVolume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || _session == null) return;
        _session.Volume = e.NewValue;
    }

    private void Speed_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || Speed.SelectedItem is not ComboBoxItem item) return;
        Notch.Media.SetRate(double.Parse((string)item.Tag, System.Globalization.CultureInfo.InvariantCulture));
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => Notch.Media.Previous();
    private void Play_Click(object sender, RoutedEventArgs e) => Notch.Media.PlayPause();
    private void Next_Click(object sender, RoutedEventArgs e) => Notch.Media.Next();
    private void Shuffle_Click(object sender, RoutedEventArgs e) => Notch.Media.ToggleShuffle();
    private void Repeat_Click(object sender, RoutedEventArgs e) => Notch.Media.CycleRepeat();
}

/// <summary>The minimal "peek on hover" player: art, title and transport only.</summary>
public sealed class MiniPlayerView : Grid
{
    public MiniPlayerView()
    {
        Margin = new Thickness(12, 8, 12, 10);
        Width = 360;
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var m = Notch.Media;
        var art = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(8), Background = new ImageBrush(m.Art) { Stretch = Stretch.UniformToFill } };
        var text = new StackPanel { Margin = new Thickness(10, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = m.Title, Style = (Style)Application.Current.FindResource("Title"), FontSize = 13 });
        text.Children.Add(new TextBlock { Text = m.Artist, Style = (Style)Application.Current.FindResource("Caption") });
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        var play = new Button { Style = (Style)Application.Current.FindResource("IconButton"), Content = m.PlayGlyph };
        play.Click += (_, _) => { m.PlayPause(); play.Content = m.IsPlaying ? Glyphs.Play : Glyphs.Pause; };
        var prev = new Button { Style = (Style)Application.Current.FindResource("IconButton"), Content = Glyphs.Previous };
        prev.Click += (_, _) => m.Previous();
        var next = new Button { Style = (Style)Application.Current.FindResource("IconButton"), Content = Glyphs.Next };
        next.Click += (_, _) => m.Next();
        controls.Children.Add(prev);
        controls.Children.Add(play);
        controls.Children.Add(next);
        SetColumn(text, 1);
        SetColumn(controls, 2);
        Children.Add(art);
        Children.Add(text);
        Children.Add(controls);
    }
}
