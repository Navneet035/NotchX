using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using Notchify.Core;

namespace Notchify.Services;

public sealed record LyricLine(TimeSpan Time, string Text);

/// <summary>Time-synced lyrics from LRCLIB (free, no key). Results are cached per track.</summary>
public sealed class LyricsService : ObservableObject
{
    private readonly Dictionary<string, List<LyricLine>?> _cache = new();
    private List<LyricLine>? _lines;
    private string _currentLine = "";
    private int _index = -1;
    private CancellationTokenSource? _cts;

    public List<LyricLine>? Lines { get => _lines; private set { if (Set(ref _lines, value)) Raise(nameof(HasLyrics)); } }
    public bool HasLyrics => _lines is { Count: > 0 };
    public string CurrentLine { get => _currentLine; private set => Set(ref _currentLine, value); }
    public int CurrentIndex { get => _index; private set => Set(ref _index, value); }

    public async void Load(string artist, string title, string album, TimeSpan duration)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Lines = null;
        CurrentLine = "";
        CurrentIndex = -1;
        if (string.IsNullOrWhiteSpace(title)) return;

        var key = $"{artist}|{title}".ToLowerInvariant();
        if (_cache.TryGetValue(key, out var cached)) { Lines = cached; return; }

        try
        {
            var url = "https://lrclib.net/api/get?" +
                      $"artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}" +
                      (string.IsNullOrEmpty(album) ? "" : $"&album_name={Uri.EscapeDataString(album)}") +
                      (duration.TotalSeconds > 0 ? $"&duration={(int)duration.TotalSeconds}" : "");
            var lrc = await Fetch(url, ct);
            if (lrc == null)
            {
                // Fall back to a fuzzy search (titles like "Song - Remastered 2011").
                var q = Regex.Replace(title, @"\s*[\(\[-].*$", "");
                var json = await Http.Client.GetStringAsync(
                    $"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(q)}&artist_name={Uri.EscapeDataString(artist)}", ct);
                using var doc = JsonDocument.Parse(json);
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String)
                    {
                        lrc = s.GetString();
                        break;
                    }
                }
            }
            if (ct.IsCancellationRequested) return;
            var parsed = lrc == null ? null : Parse(lrc);
            _cache[key] = parsed;
            Lines = parsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Info("lyrics: " + ex.Message);
        }
    }

    private static async Task<string?> Fetch(string url, CancellationToken ct)
    {
        using var resp = await Http.Client.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String
            ? s.GetString()
            : null;
    }

    private static readonly Regex LineRx = new(@"^\[(\d+):(\d+(?:\.\d+)?)\](.*)$", RegexOptions.Compiled);

    public static List<LyricLine> Parse(string lrc)
    {
        var list = new List<LyricLine>();
        foreach (var raw in lrc.Split('\n'))
        {
            var m = LineRx.Match(raw.Trim());
            if (!m.Success) continue;
            var t = TimeSpan.FromMinutes(int.Parse(m.Groups[1].Value)) +
                    TimeSpan.FromSeconds(double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
            list.Add(new LyricLine(t, m.Groups[3].Value.Trim()));
        }
        return list.OrderBy(l => l.Time).ToList();
    }

    /// <summary>Call periodically with the current playback position.</summary>
    public void Tick(TimeSpan position)
    {
        if (_lines == null || _lines.Count == 0) return;
        var i = _lines.FindLastIndex(l => l.Time <= position + TimeSpan.FromMilliseconds(250));
        if (i == _index) return;
        CurrentIndex = i;
        CurrentLine = i >= 0 ? _lines[i].Text : "";
    }
}

public static class Http
{
    public static readonly HttpClient Client = Create();

    private static HttpClient Create()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"NotchX/{AppInfo.Version} (+{AppInfo.RepoUrl})");
        return c;
    }
}
