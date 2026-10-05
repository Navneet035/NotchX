using System.IO;
using System.Windows.Media;
using Notchify.Core;
using Windows.Media.Control;

namespace Notchify.Services;

/// <summary>
/// Now Playing via Windows' Global System Media Transport Controls — the same source the
/// Windows volume flyout uses. Works with Spotify, Apple Music, Groove/Media Player,
/// and browsers (Chrome, Edge, Brave, Vivaldi, Firefox) playing video or audio.
/// </summary>
public sealed class MediaService : ObservableObject
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private string _title = "";
    private string _artist = "";
    private string _album = "";
    private ImageSource? _art;
    private bool _isPlaying;
    private bool _shuffle;
    private int _repeat; // 0 none, 1 one, 2 all
    private TimeSpan _position, _duration;
    private DateTimeOffset _positionUpdated;
    private double _rate = 1.0;
    private bool _canRate, _canShuffle, _canRepeat, _canSeek;
    private string _sourceApp = "";
    private Color _artColor = Colors.SlateGray;

    public event Action? TrackChanged;

    public bool HasSession => _session != null;
    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Artist { get => _artist; private set => Set(ref _artist, value); }
    public string Album { get => _album; private set => Set(ref _album, value); }
    public ImageSource? Art { get => _art; private set { if (Set(ref _art, value)) Raise(nameof(HasArt)); } }
    public bool HasArt => _art != null;
    /// <summary>Dominant colour of the album art, used to tint the player.</summary>
    public Color ArtColor { get => _artColor; private set { if (Set(ref _artColor, value)) Raise(nameof(ArtBrush)); } }
    public Brush ArtBrush => new SolidColorBrush(_artColor);
    public bool IsPlaying { get => _isPlaying; private set { if (Set(ref _isPlaying, value)) Raise(nameof(PlayGlyph)); } }
    public string PlayGlyph => _isPlaying ? Glyphs.Pause : Glyphs.Play;
    public bool Shuffle { get => _shuffle; private set => Set(ref _shuffle, value); }
    public int Repeat { get => _repeat; private set { if (Set(ref _repeat, value)) Raise(nameof(RepeatGlyph)); } }
    public string RepeatGlyph => _repeat == 1 ? Glyphs.RepeatOne : Glyphs.RepeatAll;
    public double PlaybackRate { get => _rate; private set => Set(ref _rate, value); }
    public bool CanChangeRate { get => _canRate; private set => Set(ref _canRate, value); }
    public bool CanShuffle { get => _canShuffle; private set => Set(ref _canShuffle, value); }
    public bool CanRepeat { get => _canRepeat; private set => Set(ref _canRepeat, value); }
    public bool CanSeek { get => _canSeek; private set => Set(ref _canSeek, value); }
    public TimeSpan Duration { get => _duration; private set => Set(ref _duration, value); }
    public string SourceAppId { get => _sourceApp; private set { if (Set(ref _sourceApp, value)) Raise(nameof(SourceName)); } }
    public string SourceName => FriendlyAppName(_sourceApp);

    /// <summary>Process name likely owning the session, used to find its audio session for app volume.</summary>
    public string SourceProcess
    {
        get
        {
            var id = _sourceApp.ToLowerInvariant();
            if (id.Contains("spotify")) return "Spotify";
            if (id.Contains("chrome")) return "chrome";
            if (id.Contains("msedge")) return "msedge";
            if (id.Contains("brave")) return "brave";
            if (id.Contains("vivaldi")) return "vivaldi";
            if (id.Contains("opera")) return "opera";
            if (id.Contains("applemusic")) return "AppleMusic";
            if (id.Contains("308046b0af4a39cb") || id.Contains("firefox")) return "firefox";
            return Path.GetFileNameWithoutExtension(_sourceApp.Split('!')[0]);
        }
    }

    /// <summary>Interpolated playback position.</summary>
    public TimeSpan Position
    {
        get
        {
            if (!_isPlaying) return _position;
            var elapsed = DateTimeOffset.Now - _positionUpdated;
            var p = _position + TimeSpan.FromTicks((long)(elapsed.Ticks * _rate));
            return _duration > TimeSpan.Zero && p > _duration ? _duration : p;
        }
    }

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => Ui.Post(() => _ = AttachAsync(_manager.GetCurrentSession()));
            _manager.SessionsChanged += (_, _) => Ui.Post(() => _ = AttachAsync(_manager.GetCurrentSession()));
            await AttachAsync(_manager.GetCurrentSession());
        }
        catch (Exception ex)
        {
            Log.Error("Media session manager unavailable", ex);
        }
    }

    private async Task AttachAsync(GlobalSystemMediaTransportControlsSession? session)
    {
        // Prefer whichever session is actually playing.
        if (_manager != null)
        {
            try
            {
                var playing = _manager.GetSessions().FirstOrDefault(s =>
                    s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
                session = playing ?? session;
            }
            catch { }
        }

        if (session?.SourceAppUserModelId == _session?.SourceAppUserModelId && session != null && _session != null) return;

        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnMediaChanged;
            _session.PlaybackInfoChanged -= OnPlaybackChanged;
            _session.TimelinePropertiesChanged -= OnTimelineChanged;
        }

        _session = session;
        Raise(nameof(HasSession));

        if (_session == null)
        {
            Title = Artist = Album = "";
            Art = null;
            IsPlaying = false;
            TrackChanged?.Invoke();
            return;
        }

        SourceAppId = _session.SourceAppUserModelId ?? "";
        _session.MediaPropertiesChanged += OnMediaChanged;
        _session.PlaybackInfoChanged += OnPlaybackChanged;
        _session.TimelinePropertiesChanged += OnTimelineChanged;
        await RefreshMediaAsync();
        RefreshPlayback();
        RefreshTimeline();
    }

    private void OnMediaChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) =>
        Ui.Post(() => _ = RefreshMediaAsync());

    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) =>
        Ui.Post(() =>
        {
            RefreshPlayback();
            // Another app may have started playing; re-evaluate which session to follow.
            if (!IsPlaying && _manager != null) _ = AttachAsync(_manager.GetCurrentSession());
        });

    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) =>
        Ui.Post(RefreshTimeline);

    private async Task RefreshMediaAsync()
    {
        if (_session == null) return;
        try
        {
            var props = await _session.TryGetMediaPropertiesAsync();
            if (props == null) return;
            var changed = props.Title != Title || props.Artist != Artist;
            Title = props.Title ?? "";
            Artist = string.IsNullOrEmpty(props.Artist) ? props.AlbumArtist ?? "" : props.Artist;
            Album = props.AlbumTitle ?? "";

            if (props.Thumbnail != null)
            {
                using var ras = await props.Thumbnail.OpenReadAsync();
                using var stream = ras.AsStreamForRead();
                var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                ms.Position = 0;
                var img = Ui.LoadImage(ms, 300);
                Art = img;
                if (img != null) ArtColor = ImageColors.Dominant(img);
            }
            else
            {
                Art = null;
                ArtColor = Colors.SlateGray;
            }

            if (changed) TrackChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("media properties", ex);
        }
    }

    private void RefreshPlayback()
    {
        if (_session == null) return;
        try
        {
            var info = _session.GetPlaybackInfo();
            if (info == null) return;
            var wasPlaying = IsPlaying;
            var pos = Position;
            IsPlaying = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            if (wasPlaying != IsPlaying) { _position = pos; _positionUpdated = DateTimeOffset.Now; }
            Shuffle = info.IsShuffleActive ?? false;
            Repeat = info.AutoRepeatMode switch
            {
                Windows.Media.MediaPlaybackAutoRepeatMode.Track => 1,
                Windows.Media.MediaPlaybackAutoRepeatMode.List => 2,
                _ => 0,
            };
            PlaybackRate = info.PlaybackRate ?? 1.0;
            CanChangeRate = info.Controls.IsPlaybackRateEnabled;
            CanShuffle = info.Controls.IsShuffleEnabled;
            CanRepeat = info.Controls.IsRepeatEnabled;
            CanSeek = info.Controls.IsPlaybackPositionEnabled;
        }
        catch (Exception ex)
        {
            Log.Error("playback info", ex);
        }
    }

    private void RefreshTimeline()
    {
        if (_session == null) return;
        try
        {
            var t = _session.GetTimelineProperties();
            Duration = t.EndTime - t.StartTime;
            _position = t.Position;
            // LastUpdatedTime lets us interpolate accurately even when the app reports rarely.
            _positionUpdated = t.LastUpdatedTime.Year > 2000 ? t.LastUpdatedTime : DateTimeOffset.Now;
            Raise(nameof(Position));
        }
        catch { }
    }

    public async void PlayPause() { if (_session != null) await _session.TryTogglePlayPauseAsync(); }
    public async void Next() { if (_session != null) await _session.TrySkipNextAsync(); }
    public async void Previous() { if (_session != null) await _session.TrySkipPreviousAsync(); }

    public async void Seek(TimeSpan position)
    {
        if (_session == null) return;
        _position = position;
        _positionUpdated = DateTimeOffset.Now;
        await _session.TryChangePlaybackPositionAsync(position.Ticks);
    }

    public async void ToggleShuffle() { if (_session != null) await _session.TryChangeShuffleActiveAsync(!Shuffle); }

    public async void CycleRepeat()
    {
        if (_session == null) return;
        var next = Repeat switch
        {
            0 => Windows.Media.MediaPlaybackAutoRepeatMode.List,
            2 => Windows.Media.MediaPlaybackAutoRepeatMode.Track,
            _ => Windows.Media.MediaPlaybackAutoRepeatMode.None,
        };
        await _session.TryChangeAutoRepeatModeAsync(next);
    }

    /// <summary>Playback speed — works for video in Chromium browsers that expose it.</summary>
    public async void SetRate(double rate)
    {
        if (_session == null) return;
        await _session.TryChangePlaybackRateAsync(rate);
    }

    /// <summary>
    /// Bring the app that's playing to the front. For a browser with several windows, prefer the one whose title
    /// mentions the track (the playing tab is usually titled after it). Store apps without an open window are launched.
    /// </summary>
    public void ShowSourceApp()
    {
        var id = SourceAppId;
        if (string.IsNullOrEmpty(id)) return;
        var process = FriendlyAppName(id) switch
        {
            "Chrome" => "chrome",
            "Edge" => "msedge",
            "Brave" => "brave",
            "Firefox" => "firefox",
            "Vivaldi" => "vivaldi",
            "Opera" => "opera",
            "Spotify" => "Spotify",
            _ => Path.GetFileNameWithoutExtension(id.Split('!')[0]),
        };
        var pids = System.Diagnostics.Process.GetProcessesByName(process).Select(p => (uint)p.Id).ToHashSet();
        var title = Title;
        IntPtr best = IntPtr.Zero, any = IntPtr.Zero;
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h) || Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return true;
            Native.GetWindowThreadProcessId(h, out var pid);
            if (!pids.Contains(pid)) return true;
            var text = Native.GetWindowTitle(h);
            if (string.IsNullOrWhiteSpace(text)) return true;
            if (any == IntPtr.Zero) any = h;
            if (!string.IsNullOrEmpty(title) && text.Contains(title, StringComparison.OrdinalIgnoreCase)) { best = h; return false; }
            return true;
        }, IntPtr.Zero);

        var target = best != IntPtr.Zero ? best : any;
        if (target != IntPtr.Zero) { WindowListService.Activate(target); return; }
        // Store apps (Media Player, Apple Music…) open by their app id, which also brings back a closed window.
        if (id.Contains('!'))
        {
            Notch.Shell.Collapse();
            Ui.OpenUrl($"shell:AppsFolder\\{id}");
        }
    }

    public static string FriendlyAppName(string id)
    {
        var l = id.ToLowerInvariant();
        if (l.Contains("spotify")) return "Spotify";
        if (l.Contains("applemusic")) return "Apple Music";
        if (l.Contains("chrome")) return "Chrome";
        if (l.Contains("msedge")) return "Edge";
        if (l.Contains("brave")) return "Brave";
        if (l.Contains("vivaldi")) return "Vivaldi";
        if (l.Contains("opera")) return "Opera";
        if (l.Contains("308046b0af4a39cb") || l.Contains("firefox")) return "Firefox";
        if (l.Contains("zunemusic") || l.Contains("media")) return "Media Player";
        if (string.IsNullOrEmpty(id)) return "";
        return Path.GetFileNameWithoutExtension(id.Split('!')[0]);
    }
}

public static class ImageColors
{
    /// <summary>Cheap average of a down-sampled bitmap, nudged towards a vivid colour.</summary>
    public static Color Dominant(System.Windows.Media.Imaging.BitmapSource src)
    {
        try
        {
            var small = new System.Windows.Media.Imaging.TransformedBitmap(src,
                new ScaleTransform(16.0 / src.PixelWidth, 16.0 / src.PixelHeight));
            var conv = new System.Windows.Media.Imaging.FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            var w = conv.PixelWidth; var h = conv.PixelHeight;
            var px = new byte[w * h * 4];
            conv.CopyPixels(px, w * 4, 0);
            double r = 0, g = 0, b = 0, weight = 0;
            for (var i = 0; i < px.Length; i += 4)
            {
                double bb = px[i], gg = px[i + 1], rr = px[i + 2];
                var max = Math.Max(rr, Math.Max(gg, bb)); var min = Math.Min(rr, Math.Min(gg, bb));
                var sat = max == 0 ? 0 : (max - min) / max;
                var wgt = 0.2 + sat * (max / 255.0);
                r += rr * wgt; g += gg * wgt; b += bb * wgt; weight += wgt;
            }
            if (weight <= 0) return Colors.SlateGray;
            return Color.FromRgb((byte)(r / weight), (byte)(g / weight), (byte)(b / weight));
        }
        catch { return Colors.SlateGray; }
    }
}
