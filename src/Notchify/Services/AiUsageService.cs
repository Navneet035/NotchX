using System.IO;
using System.Net.Http;
using System.Text.Json;
using Notchify.Core;

namespace Notchify.Services;

public sealed class ProviderUsage : ObservableObject
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>One word for the closed-notch chip.</summary>
    public string ShortName { get; init; } = "";
    public string Glyph { get; init; } = Glyphs.Robot;
    public bool Available { get; set; }
    public string Status { get; set; } = "";
    /// <summary>Current rate-limit window usage, 0..1 (null when unknown).</summary>
    public double? WindowUsed { get; set; }
    public string WindowLabel { get; set; } = "";
    public string WindowResets { get; set; } = "";
    public double? WeeklyUsed { get; set; }
    public string WeeklyLabel { get; set; } = "";
    public long TokensToday { get; set; }
    public long TokensWeek { get; set; }
    public double CostToday { get; set; }
    public double CostWeek { get; set; }
    public List<double> Daily { get; set; } = new();

    public string TokensTodayText => Ui.FormatTokens(TokensToday);
    public string TokensWeekText => Ui.FormatTokens(TokensWeek);
    public string CostTodayText => CostToday > 0 ? $"${CostToday:0.00}" : "—";
    public string CostWeekText => CostWeek > 0 ? $"${CostWeek:0.00}" : "—";
    public bool HasWindow => WindowUsed.HasValue;
    public bool HasWeekly => WeeklyUsed.HasValue;
    public double WindowPercent => (WindowUsed ?? 0) * 100;
    public double WeeklyPercent => (WeeklyUsed ?? 0) * 100;

    public void Changed() => Raise(string.Empty);
}

/// <summary>
/// AI usage tracker. Reads local CLI logs (no network, no keys):
///   • Claude Code: ~/.claude/projects/**.jsonl — tokens, cost estimate, 5-hour session window
///   • Codex: ~/.codex/sessions/**.jsonl — tokens and the server-reported rate-limit windows
/// Optional API tokens (stored with Windows DPAPI) add GitHub Copilot premium-request quota.
/// </summary>
public sealed class AiUsageService
{
    private readonly Dictionary<string, int> _alerted = new();

    public List<ProviderUsage> Providers { get; } = new()
    {
        new() { Id = "claude", Name = "Claude Code", ShortName = "Claude", Glyph = Glyphs.Robot },
        new() { Id = "codex", Name = "Codex", ShortName = "Codex", Glyph = Glyphs.Code },
        new() { Id = "copilot", Name = "GitHub Copilot", ShortName = "Copilot", Glyph = Glyphs.Code },
        new() { Id = "cursor", Name = "Cursor", ShortName = "Cursor", Glyph = Glyphs.Edit },
    };

    /// <summary>Apps that can have a closed-notch chip (Cursor has no data source yet).</summary>
    public IEnumerable<ProviderUsage> ChipProviders => Providers.Where(p => p.Id != "cursor");

    /// <summary>
    /// Put a usage chip on the closed notch for each chosen app: "Claude 42%" with a small bar, green → orange
    /// at the alert threshold → red at the limit. Hover for the details. Call on the UI thread.
    /// </summary>
    public void UpdateNotchChips()
    {
        var s = SettingsStore.Current.AiUsage;
        var threshold = s.AlertAtPercent / 100.0;
        foreach (var p in Providers)
        {
            var id = "ai-usage-" + p.Id;
            if (!s.ShowInNotch || !s.NotchProviders.Contains(p.Id) || !p.Available)
            {
                Notch.Hub.Remove(id);
                continue;
            }
            Notch.Hub.Upsert(id, a =>
            {
                a.Glyph = p.Glyph;
                a.OpenTab = "ai";
                a.Priority = 5; // below music, timers and running agents
                if (p.WindowUsed is { } used)
                {
                    a.Text = $"{p.ShortName} {used * 100:0}%";
                    a.Progress = Math.Min(1, used);
                    a.Accent = used >= 1 ? Ui.Red : used >= threshold ? Ui.Orange : Ui.Green;
                }
                else
                {
                    a.Text = $"{p.ShortName} –";
                    a.Progress = null;
                    a.Accent = Ui.Gray;
                }
                a.Detail = string.Join("\n", new[] { p.Name, p.WindowLabel, p.WindowResets, p.WeeklyLabel, p.Status }
                    .Where(x => !string.IsNullOrWhiteSpace(x)));
            });
        }
    }

    public void RemoveNotchChips()
    {
        foreach (var p in Providers) Notch.Hub.Remove("ai-usage-" + p.Id);
    }

    public event Action? Updated;

    public async Task RefreshAsync()
    {
        await Task.Run(() =>
        {
            try { ReadClaude(Providers[0]); } catch (Exception ex) { Providers[0].Status = ex.Message; }
            try { ReadCodex(Providers[1]); } catch (Exception ex) { Providers[1].Status = ex.Message; }
        });
        try { await ReadCopilotAsync(Providers[2]); } catch (Exception ex) { Providers[2].Status = ex.Message; }
        var cursor = Providers[3];
        cursor.Available = false;
        cursor.Status = "Cursor usage needs its dashboard session; not supported yet.";

        foreach (var p in Providers) p.Changed();
        Alert();
        Ui.Post(UpdateNotchChips);
        Updated?.Invoke();
    }

    private void Alert()
    {
        var threshold = SettingsStore.Current.AiUsage.AlertAtPercent / 100.0;
        foreach (var p in Providers.Where(p => p.WindowUsed.HasValue))
        {
            var used = p.WindowUsed!.Value;
            var bucket = used >= 1 ? 2 : used >= threshold ? 1 : 0;
            _alerted.TryGetValue(p.Id, out var prev);
            if (bucket > prev)
                Ui.Post(() => Notch.Hub.Notify(Glyphs.Warning, $"{p.Name}: {used:P0} of window used",
                    p.WindowResets, used >= 1 ? Ui.Red : Ui.Orange, IslandPriority.High, 6, "ai-alert-" + p.Id, "ai"));
            _alerted[p.Id] = bucket;
        }
    }

    // ---------- Claude Code ----------

    private static string ClaudeDir()
    {
        var custom = SettingsStore.Current.AiUsage.ClaudeDir;
        if (!string.IsNullOrWhiteSpace(custom)) return custom;
        var env = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        return Path.Combine(string.IsNullOrEmpty(env) ? Path.Combine(Paths.UserProfile, ".claude") : env, "projects");
    }

    private sealed record UsageRow(DateTime Time, string Model, long Input, long Output, long CacheWrite, long CacheRead)
    {
        public long Total => Input + Output + CacheWrite + CacheRead;
    }

    private static void ReadClaude(ProviderUsage p)
    {
        var dir = ClaudeDir();
        if (!Directory.Exists(dir)) { p.Available = false; p.Status = "No Claude Code logs found"; return; }
        p.Available = true;

        var since = DateTime.Now.Date.AddDays(-6);
        var rows = new List<UsageRow>();
        var seen = new HashSet<string>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.AllDirectories))
        {
            if (File.GetLastWriteTime(file) < since) continue;
            foreach (var row in ParseClaudeFile(file, 0, seen)) if (row.Time >= since) rows.Add(row);
        }

        var today = DateTime.Now.Date;
        p.TokensToday = rows.Where(r => r.Time >= today).Sum(r => r.Total);
        p.TokensWeek = rows.Sum(r => r.Total);
        p.CostToday = rows.Where(r => r.Time >= today).Sum(Cost);
        p.CostWeek = rows.Sum(Cost);
        p.Daily = Enumerable.Range(0, 7).Select(i => rows.Where(r => r.Time.Date == today.AddDays(i - 6)).Sum(r => (double)r.Total)).ToList();

        // Claude's usage limits reset on rolling 5-hour sessions that start with your first message.
        var ordered = rows.OrderBy(r => r.Time).ToList();
        DateTime? blockStart = null;
        foreach (var r in ordered)
            if (blockStart == null || r.Time >= blockStart.Value.AddHours(5))
                blockStart = new DateTime(r.Time.Year, r.Time.Month, r.Time.Day, r.Time.Hour, 0, 0);
        if (blockStart is { } bs && DateTime.Now < bs.AddHours(5))
        {
            var blockTokens = ordered.Where(r => r.Time >= bs).Sum(r => r.Total);
            var budget = SettingsStore.Current.AiUsage.ClaudeWindowTokenBudget;
            p.WindowUsed = budget > 0 ? Math.Min(1.5, (double)blockTokens / budget) : null;
            p.WindowLabel = $"5-hour window · {Ui.FormatTokens(blockTokens)} tokens · ${ordered.Where(r => r.Time >= bs).Sum(Cost):0.00}";
            var left = bs.AddHours(5) - DateTime.Now;
            p.WindowResets = $"Resets in {(int)left.TotalHours}h {left.Minutes}m";
        }
        else
        {
            p.WindowUsed = null;
            p.WindowLabel = "No active 5-hour window";
            p.WindowResets = "";
        }
        p.Status = "From local Claude Code logs";
    }

    private static IEnumerable<UsageRow> ParseClaudeFile(string file, long offset, HashSet<string>? seen)
    {
        var result = new List<UsageRow>();
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (!line.Contains("\"usage\"")) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) continue;
                    if (!msg.TryGetProperty("usage", out var u)) continue;
                    // Streaming writes the same message several times — count each message/request once.
                    var id = (msg.TryGetProperty("id", out var mid) ? mid.GetString() : "") + ":" +
                             (root.TryGetProperty("requestId", out var rid) ? rid.GetString() : "");
                    if (seen != null && id != ":" && !seen.Add(id)) continue;
                    var time = root.TryGetProperty("timestamp", out var ts) && ts.TryGetDateTime(out var t) ? t.ToLocalTime() : DateTime.Now;
                    result.Add(new UsageRow(time,
                        msg.TryGetProperty("model", out var m) ? m.GetString() ?? "" : "",
                        Long(u, "input_tokens"), Long(u, "output_tokens"),
                        Long(u, "cache_creation_input_tokens"), Long(u, "cache_read_input_tokens")));
                }
                catch (JsonException) { }
            }
        }
        catch (IOException) { }
        return result;
    }

    private static double Cost(UsageRow r)
    {
        var prices = SettingsStore.Current.AiUsage.Prices;
        var match = prices.Where(kv => r.Model.Contains(kv.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(kv => kv.Key.Length).Select(kv => kv.Value).FirstOrDefault();
        if (match == null) return 0;
        return (r.Input * match.Input + r.Output * match.Output + r.CacheWrite * match.CacheWrite + r.CacheRead * match.CacheRead) / 1_000_000;
    }

    /// <summary>Estimated cost of everything appended to a transcript after <paramref name="offset"/> (one agent turn).</summary>
    public static double? CostSince(string? transcript, long offset)
    {
        if (transcript == null || !File.Exists(transcript)) return null;
        var rows = ParseClaudeFile(transcript, offset, new HashSet<string>());
        var cost = rows.Sum(Cost);
        return cost > 0 ? cost : null;
    }

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    // ---------- Codex ----------

    private static void ReadCodex(ProviderUsage p)
    {
        var custom = SettingsStore.Current.AiUsage.CodexDir;
        var home = string.IsNullOrWhiteSpace(custom)
            ? Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } ch ? ch : Path.Combine(Paths.UserProfile, ".codex")
            : custom;
        var dir = Path.Combine(home, "sessions");
        if (!Directory.Exists(dir)) { p.Available = false; p.Status = "No Codex sessions found"; return; }
        p.Available = true;

        var since = DateTime.Now.Date.AddDays(-6);
        var today = DateTime.Now.Date;
        long tokToday = 0, tokWeek = 0;
        var daily = new double[7];
        JsonElement? latestLimits = null;
        DateTime latestLimitsTime = DateTime.MinValue;
        JsonDocument? keep = null;

        foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.AllDirectories))
        {
            var modified = File.GetLastWriteTime(file);
            if (modified < since) continue;
            long fileTotal = 0;
            DateTime fileTime = modified;
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (!line.Contains("token_count")) continue;
                    try
                    {
                        var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        var payload = root.TryGetProperty("payload", out var pl) ? pl : root;
                        if (payload.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object &&
                            info.TryGetProperty("total_token_usage", out var total))
                            fileTotal = Long(total, "total_tokens") is var tt && tt > 0 ? tt : Long(total, "input_tokens") + Long(total, "output_tokens");
                        var t = root.TryGetProperty("timestamp", out var ts) && ts.TryGetDateTime(out var dt) ? dt.ToLocalTime() : modified;
                        fileTime = t;
                        if (payload.TryGetProperty("rate_limits", out var rl) && rl.ValueKind == JsonValueKind.Object && t > latestLimitsTime)
                        {
                            keep?.Dispose();
                            keep = doc;
                            latestLimits = rl;
                            latestLimitsTime = t;
                            continue;
                        }
                        doc.Dispose();
                    }
                    catch (JsonException) { }
                }
            }
            catch (IOException) { }

            // total_token_usage is cumulative per session file.
            tokWeek += fileTotal;
            if (fileTime >= today) tokToday += fileTotal;
            var idx = (int)(fileTime.Date - today.AddDays(-6)).TotalDays;
            if (idx is >= 0 and < 7) daily[idx] += fileTotal;
        }

        p.TokensToday = tokToday;
        p.TokensWeek = tokWeek;
        p.Daily = daily.ToList();
        p.WindowUsed = null;
        p.WeeklyUsed = null;
        if (latestLimits is { } limits)
        {
            if (limits.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.Object)
            {
                p.WindowUsed = Pct(primary) / 100;
                p.WindowLabel = $"{WindowName(primary, "Session")} · {Pct(primary):0}% used";
                p.WindowResets = Resets(primary, latestLimitsTime);
            }
            if (limits.TryGetProperty("secondary", out var secondary) && secondary.ValueKind == JsonValueKind.Object)
            {
                p.WeeklyUsed = Pct(secondary) / 100;
                p.WeeklyLabel = $"{WindowName(secondary, "Weekly")} · {Pct(secondary):0}% used · {Resets(secondary, latestLimitsTime)}";
            }
        }
        keep?.Dispose();
        p.Status = latestLimits == null ? "From local Codex logs" : $"Limits as of {latestLimitsTime:t}";

        static double Pct(JsonElement e) => e.TryGetProperty("used_percent", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

        static string WindowName(JsonElement e, string fallback)
        {
            if (!e.TryGetProperty("window_minutes", out var w) || w.ValueKind != JsonValueKind.Number) return fallback;
            var mins = w.GetDouble();
            return mins >= 1440 * 6 ? "Weekly" : mins >= 60 ? $"{mins / 60:0}-hour window" : $"{mins:0}-minute window";
        }

        static string Resets(JsonElement e, DateTime asOf)
        {
            double? secs = e.TryGetProperty("resets_in_seconds", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetDouble() : null;
            if (secs == null && e.TryGetProperty("resets_at", out var ra) && ra.ValueKind == JsonValueKind.Number)
                return $"Resets {DateTimeOffset.FromUnixTimeSeconds(ra.GetInt64()).LocalDateTime:ddd t}";
            if (secs == null) return "";
            var at = asOf.AddSeconds(secs.Value);
            var left = at - DateTime.Now;
            return left.TotalSeconds <= 0 ? "Reset" : left.TotalHours >= 24 ? $"Resets {at:ddd t}" : $"Resets in {(int)left.TotalHours}h {left.Minutes}m";
        }
    }

    // ---------- GitHub Copilot (optional token) ----------

    private static async Task ReadCopilotAsync(ProviderUsage p)
    {
        var token = Secrets.Unprotect(SettingsStore.Current.AiUsage.CopilotTokenProtected);
        if (string.IsNullOrEmpty(token))
        {
            p.Available = false;
            p.Status = "Add a GitHub token in Settings › Integrations to see Copilot quota";
            return;
        }
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/copilot_internal/user");
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("token", token);
        req.Headers.Accept.ParseAdd("application/json");
        using var res = await Http.Client.SendAsync(req);
        if (!res.IsSuccessStatusCode) { p.Available = false; p.Status = $"GitHub returned {(int)res.StatusCode}"; return; }
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        p.Available = true;
        if (doc.RootElement.TryGetProperty("quota_snapshots", out var snaps) &&
            snaps.TryGetProperty("premium_interactions", out var prem))
        {
            var unlimited = prem.TryGetProperty("unlimited", out var un) && un.ValueKind == JsonValueKind.True;
            var pctRemaining = prem.TryGetProperty("percent_remaining", out var pr) && pr.ValueKind == JsonValueKind.Number ? pr.GetDouble() : 100;
            p.WindowUsed = unlimited ? null : (100 - pctRemaining) / 100;
            p.WindowLabel = unlimited ? "Premium requests · unlimited" : $"Premium requests · {100 - pctRemaining:0}% used";
        }
        if (doc.RootElement.TryGetProperty("quota_reset_date", out var reset) && reset.ValueKind == JsonValueKind.String)
            p.WindowResets = "Resets " + reset.GetString();
        p.Status = "From the GitHub API";
    }
}
