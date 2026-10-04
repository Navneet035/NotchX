using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using Notchify.Core;

namespace Notchify.Services;

public sealed class CalendarEvent
{
    public string Title { get; init; } = "";
    public DateTime Start { get; init; }
    public DateTime End { get; init; }
    public bool AllDay { get; init; }
    public string? Location { get; init; }
    public string? Url { get; init; }
    public string TimeText => AllDay ? "All day" : $"{Start:t} – {End:t}";
    public string DayText => Start.Date == DateTime.Today ? "Today" : Start.Date == DateTime.Today.AddDays(1) ? "Tomorrow" : Start.ToString("ddd d MMM");
    public bool IsNow => !AllDay && DateTime.Now >= Start && DateTime.Now < End;
    /// <summary>Video-call link found in the location/description, for one-tap join.</summary>
    public string? JoinUrl { get; init; }
    public bool HasJoin => JoinUrl != null;
}

/// <summary>
/// Calendar from iCal (.ics) subscription links — every provider offers one
/// (Google: "Secret address in iCal format", Outlook/Exchange: "Publish calendar", iCloud: "Public calendar").
/// Handles time zones and common RRULEs (daily/weekly/monthly/yearly, INTERVAL, COUNT, UNTIL, BYDAY, EXDATE).
/// </summary>
public sealed class CalendarService : ObservableObject
{
    private readonly DispatcherTimer _refresh = new();
    private readonly DispatcherTimer _alerts = new() { Interval = TimeSpan.FromSeconds(30) };
    private readonly HashSet<string> _alerted = new();

    public ObservableCollection<CalendarEvent> Upcoming { get; } = new();
    public string Status { get; private set; } = "";

    public CalendarService()
    {
        _refresh.Tick += (_, _) => _ = RefreshAsync();
        _alerts.Tick += (_, _) => CheckAlerts();
    }

    public void Start()
    {
        _refresh.Interval = TimeSpan.FromMinutes(Math.Max(5, SettingsStore.Current.Calendar.RefreshMinutes));
        _refresh.Start();
        _alerts.Start();
        _ = RefreshAsync();
    }

    public void Stop() { _refresh.Stop(); _alerts.Stop(); }

    public async Task RefreshAsync()
    {
        var urls = SettingsStore.Current.Calendar.IcsUrls.Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        if (urls.Count == 0) { Status = "Add an iCal link in Settings › Integrations"; Raise(nameof(Status)); return; }
        var from = DateTime.Today;
        var to = DateTime.Today.AddDays(7);
        var all = new List<CalendarEvent>();
        foreach (var url in urls)
        {
            try
            {
                var text = await Http.Client.GetStringAsync(url.Replace("webcal://", "https://"));
                all.AddRange(Parse(text, from, to));
            }
            catch (Exception ex) { Log.Info("ics: " + ex.Message); }
        }
        Upcoming.Clear();
        foreach (var e in all.Where(e => e.End > DateTime.Now).OrderBy(e => e.Start).Take(40)) Upcoming.Add(e);
        Status = Upcoming.Count == 0 ? "Nothing in the next 7 days" : "";
        Raise(nameof(Status));
        UpdateActivity();
    }

    private void CheckAlerts()
    {
        var lead = SettingsStore.Current.Calendar.AlertMinutesBefore;
        foreach (var e in Upcoming.Where(e => !e.AllDay))
        {
            var until = e.Start - DateTime.Now;
            if (until.TotalMinutes <= lead && until.TotalMinutes > -1 && _alerted.Add(e.Title + e.Start))
            {
                var island = new Island
                {
                    Glyph = Glyphs.Calendar,
                    Title = e.Title,
                    Message = until.TotalMinutes < 1 ? "Starting now" : $"In {(int)Math.Ceiling(until.TotalMinutes)} min" + (e.Location != null ? $" · {e.Location}" : ""),
                    Accent = Ui.Red,
                    Priority = IslandPriority.High,
                    Duration = TimeSpan.FromSeconds(10),
                    OpenTab = "calendar",
                };
                if (e.JoinUrl != null) island.Actions.Add(new IslandAction("Join", () => Ui.OpenUrl(e.JoinUrl), true, Glyphs.Video));
                Notch.Hub.Show(island);
            }
        }
        UpdateActivity();
    }

    private void UpdateActivity()
    {
        var next = Upcoming.FirstOrDefault(e => !e.AllDay && e.Start > DateTime.Now && (e.Start - DateTime.Now).TotalMinutes <= 15);
        if (next == null) { Notch.Hub.Remove("calendar"); return; }
        Notch.Hub.Upsert("calendar", a =>
        {
            a.Glyph = Glyphs.Calendar; a.Accent = Ui.Red; a.Priority = 50; a.OpenTab = "calendar";
            a.Text = $"{(int)Math.Ceiling((next.Start - DateTime.Now).TotalMinutes)}m";
            a.Detail = next.Title;
        });
    }

    // ---------- iCalendar parsing ----------

    public static List<CalendarEvent> Parse(string ics, DateTime from, DateTime to)
    {
        var result = new List<CalendarEvent>();
        // Unfold continuation lines (RFC 5545 §3.1).
        var lines = Regex.Replace(ics, @"\r?\n[ \t]", "").Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Dictionary<string, (string Params, string Value)>? ev = null;
        var exdates = new List<DateTime>();
        var overrides = new HashSet<(string, DateTime)>();
        var events = new List<(Dictionary<string, (string, string)> Props, List<DateTime> Ex)>();

        foreach (var line in lines)
        {
            if (line == "BEGIN:VEVENT") { ev = new(); exdates = new(); continue; }
            if (line == "END:VEVENT" && ev != null) { events.Add((ev, exdates)); ev = null; continue; }
            if (ev == null) continue;
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var head = line[..colon];
            var value = line[(colon + 1)..];
            var semi = head.IndexOf(';');
            var name = semi < 0 ? head : head[..semi];
            var prms = semi < 0 ? "" : head[(semi + 1)..];
            if (name == "EXDATE")
                foreach (var v in value.Split(',')) if (ParseDate(v, prms, out var d, out _)) exdates.Add(d);
            ev[name] = (prms, value);
        }

        // Instances that were moved/edited individually (RECURRENCE-ID) replace the generated occurrence.
        foreach (var (props, _) in events)
            if (props.TryGetValue("RECURRENCE-ID", out var rid) && props.TryGetValue("UID", out var uid) &&
                ParseDate(rid.Item2, rid.Item1, out var rd, out _))
                overrides.Add((uid.Item2, rd));

        foreach (var (props, ex) in events)
        {
            if (!props.TryGetValue("DTSTART", out var ds) || !ParseDate(ds.Item2, ds.Item1, out var start, out var allDay)) continue;
            if (props.TryGetValue("STATUS", out var st) && st.Item2 == "CANCELLED") continue;
            DateTime end;
            if (props.TryGetValue("DTEND", out var de) && ParseDate(de.Item2, de.Item1, out var e, out _)) end = e;
            else if (props.TryGetValue("DURATION", out var du)) end = start + ParseDuration(du.Item2);
            else end = allDay ? start.AddDays(1) : start.AddHours(1);
            var length = end - start;
            var title = Unescape(props.TryGetValue("SUMMARY", out var s) ? s.Item2 : "(No title)");
            var location = props.TryGetValue("LOCATION", out var l) ? Unescape(l.Item2) : null;
            var desc = props.TryGetValue("DESCRIPTION", out var dd) ? Unescape(dd.Item2) : "";
            var join = FindJoinUrl((location ?? "") + " " + desc + " " + (props.TryGetValue("URL", out var u) ? u.Item2 : ""));
            var uidValue = props.TryGetValue("UID", out var uidp) ? uidp.Item2 : "";
            var isOverride = props.ContainsKey("RECURRENCE-ID");

            IEnumerable<DateTime> starts = props.TryGetValue("RRULE", out var rr) && !isOverride
                ? Expand(start, rr.Item2, to).Where(o => !ex.Contains(o) && !overrides.Contains((uidValue, o)))
                : new[] { start };

            foreach (var o in starts)
            {
                if (o + length <= from || o >= to) continue;
                result.Add(new CalendarEvent
                {
                    Title = title, Start = o, End = o + length, AllDay = allDay, Location = string.IsNullOrWhiteSpace(location) ? null : location,
                    JoinUrl = join,
                });
            }
        }
        return result;
    }

    private static string? FindJoinUrl(string text)
    {
        var m = Regex.Match(text, @"https://[^\s""<>]*(zoom\.us/j|meet\.google\.com|teams\.microsoft\.com/l/meetup-join|webex\.com)[^\s""<>]*", RegexOptions.IgnoreCase);
        return m.Success ? m.Value : null;
    }

    private static string Unescape(string s) => s.Replace("\\n", "\n").Replace("\\N", "\n").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");

    private static bool ParseDate(string value, string prms, out DateTime result, out bool allDay)
    {
        allDay = prms.Contains("VALUE=DATE") && !prms.Contains("VALUE=DATE-TIME") || value.Length == 8;
        result = default;
        if (allDay)
            return DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out result);

        var utc = value.EndsWith('Z');
        if (!DateTime.TryParseExact(value.TrimEnd('Z'), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return false;
        if (utc) { result = DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToLocalTime(); return true; }

        var tzid = Regex.Match(prms, @"TZID=""?([^;:""]+)").Groups[1].Value;
        if (!string.IsNullOrEmpty(tzid))
        {
            try
            {
                // .NET on Windows accepts both Windows and IANA zone ids.
                var tz = TimeZoneInfo.FindSystemTimeZoneById(tzid);
                result = TimeZoneInfo.ConvertTime(dt, tz, TimeZoneInfo.Local);
                return true;
            }
            catch { }
        }
        result = dt; // floating time
        return true;
    }

    private static TimeSpan ParseDuration(string d)
    {
        var m = Regex.Match(d, @"P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?");
        int G(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value) : 0;
        return new TimeSpan(G(1) * 7 + G(2), G(3), G(4), G(5));
    }

    private static IEnumerable<DateTime> Expand(DateTime start, string rrule, DateTime to)
    {
        var parts = rrule.Split(';').Select(p => p.Split('=')).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
        var freq = parts.GetValueOrDefault("FREQ", "DAILY");
        var interval = int.TryParse(parts.GetValueOrDefault("INTERVAL"), out var iv) ? Math.Max(1, iv) : 1;
        int? count = int.TryParse(parts.GetValueOrDefault("COUNT"), out var c) ? c : null;
        DateTime? until = parts.TryGetValue("UNTIL", out var u) && ParseDate(u, "", out var ud, out _) ? ud.Date == ud ? ud.AddDays(1) : ud : null;
        var byDay = parts.TryGetValue("BYDAY", out var bd)
            ? bd.Split(',').Select(x => Regex.Replace(x, @"^[+-]?\d+", "")).Select(DayOf).Where(x => x.HasValue).Select(x => x!.Value).ToHashSet()
            : null;

        var produced = 0;
        var limit = until.HasValue && until < to ? until.Value : to;
        for (var i = 0; i < 2000; i++)
        {
            IEnumerable<DateTime> candidates;
            switch (freq)
            {
                case "WEEKLY":
                    var weekStart = start.AddDays(7 * interval * i);
                    var monday = weekStart.AddDays(-(((int)weekStart.DayOfWeek + 6) % 7));
                    candidates = byDay is { Count: > 0 }
                        ? Enumerable.Range(0, 7).Select(k => monday.AddDays(k)).Where(d => byDay.Contains(d.DayOfWeek))
                            .Select(d => d.Date + start.TimeOfDay)
                        : new[] { weekStart };
                    break;
                case "MONTHLY": candidates = new[] { start.AddMonths(interval * i) }; break;
                case "YEARLY": candidates = new[] { start.AddYears(interval * i) }; break;
                default:
                    var day = start.AddDays(interval * i);
                    candidates = byDay is { Count: > 0 } && !byDay.Contains(day.DayOfWeek) ? Array.Empty<DateTime>() : new[] { day };
                    break;
            }
            foreach (var cand in candidates.OrderBy(x => x))
            {
                if (cand < start) continue;
                if (cand >= limit) yield break;
                if (count.HasValue && produced >= count) yield break;
                produced++;
                yield return cand;
            }
        }
    }

    private static DayOfWeek? DayOf(string s) => s switch
    {
        "MO" => DayOfWeek.Monday, "TU" => DayOfWeek.Tuesday, "WE" => DayOfWeek.Wednesday, "TH" => DayOfWeek.Thursday,
        "FR" => DayOfWeek.Friday, "SA" => DayOfWeek.Saturday, "SU" => DayOfWeek.Sunday, _ => null,
    };
}
