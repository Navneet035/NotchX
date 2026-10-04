using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Notchify.Controls;
using Notchify.Core;
using Notchify.Services;
using Notchify.Shell;
using Notchify.Views;

namespace Notchify.Modules;

/// <summary>
/// Now Playing: Spotify / Apple Music / any media app and browser video, with album art, scrubbing,
/// shuffle & repeat, per-app volume, playback speed for browser video, live spectrum and synced lyrics.
/// </summary>
public sealed class NowPlayingModule : NotchModule
{
    private readonly DispatcherTimer _lyricsTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public override string Id => "music";
    public override string Title => "Now Playing";
    public override string Glyph => Glyphs.Music;
    public override string Description => "Media controls, album art, scrubbing, speed, app volume, live spectrum and synced lyrics.";

    public MediaService Media => Notch.Media;
    public LyricsService Lyrics => Notch.Lyrics;

    protected override void Start()
    {
        Notch.Media.PropertyChanged += OnMediaChanged;
        Notch.Media.TrackChanged += OnTrackChanged;
        Notch.Lyrics.PropertyChanged += OnLyricsChanged;
        _lyricsTimer.Tick += LyricsTick;
        NotchWindow.PeekFactory = () => new MiniPlayerView();
        OnTrackChanged();
    }

    protected override void Stop()
    {
        Notch.Media.PropertyChanged -= OnMediaChanged;
        Notch.Media.TrackChanged -= OnTrackChanged;
        Notch.Lyrics.PropertyChanged -= OnLyricsChanged;
        _lyricsTimer.Tick -= LyricsTick;
        _lyricsTimer.Stop();
        NotchWindow.PeekFactory = null;
        Notch.Hub.Remove("media");
    }

    private void LyricsTick(object? sender, EventArgs e) => Notch.Lyrics.Tick(Notch.Media.Position);

    private void OnTrackChanged()
    {
        var m = Notch.Media;
        if (SettingsStore.Current.Media.Lyrics && m.HasSession)
            Notch.Lyrics.Load(m.Artist, m.Title, m.Album, m.Duration);
        UpdateActivity();

        if (m.HasSession && m.IsPlaying && !Notch.Shell.IsExpanded && !string.IsNullOrEmpty(m.Title))
            Notch.Hub.Show(new Island
            {
                Key = "track",
                Image = m.Art,
                Glyph = Glyphs.Music,
                Title = m.Title,
                Message = m.Artist,
                Accent = m.ArtBrush,
                Priority = IslandPriority.Low,
                Duration = TimeSpan.FromSeconds(2.5),
                OpenTab = Id,
            });
    }

    private void OnMediaChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MediaService.IsPlaying) or nameof(MediaService.HasSession) or nameof(MediaService.Art))
            UpdateActivity();
    }

    private void OnLyricsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LyricsService.HasLyrics)) UpdateActivity();
        if (e.PropertyName == nameof(LyricsService.CurrentLine) && SettingsStore.Current.Media.LyricsInPill && Notch.Hub.Has("media"))
            Notch.Hub.Upsert("media", a => a.Text = Notch.Lyrics.CurrentLine);
    }

    private void UpdateActivity()
    {
        var m = Notch.Media;
        var s = SettingsStore.Current.Media;
        if (m.IsPlaying && m.HasLyricsTimerNeeded()) _lyricsTimer.Start(); else _lyricsTimer.Stop();

        if (!s.ShowInPill || !m.HasSession || !m.IsPlaying)
        {
            Notch.Hub.Remove("media");
            return;
        }
        Notch.Hub.Upsert("media", a =>
        {
            a.Image = m.Art;
            a.Glyph = Glyphs.Music;
            a.Accent = m.ArtBrush;
            a.Priority = 60;
            a.OpenTab = Id;
            a.Detail = $"{m.Title} — {m.Artist}";
            a.Text = s.LyricsInPill ? Notch.Lyrics.CurrentLine : "";
            a.CompactContent ??= new SpectrumBars { Width = 22, Height = 14, BarCount = 4, Fill = m.ArtBrush, UseLiveAudio = s.SpectrumInPill };
            if (a.CompactContent is SpectrumBars bars) bars.Fill = m.ArtBrush;
        });
    }

    public override FrameworkElement CreateView() => new NowPlayingView();

    public override IEnumerable<PaletteCommand> Commands => new[]
    {
        new PaletteCommand("Play / Pause", Glyphs.Play, Notch.Media.PlayPause, Notch.Media.Title, "music media"),
        new PaletteCommand("Next track", Glyphs.Next, Notch.Media.Next, null, "music skip"),
        new PaletteCommand("Previous track", Glyphs.Previous, Notch.Media.Previous, null, "music back"),
        new PaletteCommand("Toggle shuffle", Glyphs.Shuffle, Notch.Media.ToggleShuffle, null, "music"),
        new PaletteCommand("Playback speed 1.5×", Glyphs.Speed, () => Notch.Media.SetRate(1.5), null, "video speed rate"),
        new PaletteCommand("Playback speed 1×", Glyphs.Speed, () => Notch.Media.SetRate(1.0), null, "video speed rate normal"),
        new PaletteCommand("Playback speed 2×", Glyphs.Speed, () => Notch.Media.SetRate(2.0), null, "video speed rate"),
    };
}

internal static class MediaExtensions
{
    /// <summary>Lyrics only need ticking while something will show them.</summary>
    public static bool HasLyricsTimerNeeded(this MediaService m) =>
        SettingsStore.Current.Media.Lyrics && Notch.Lyrics.HasLyrics;
}
