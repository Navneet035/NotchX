using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Notchify.Core;

namespace Notchify.Services;

/// <summary>
/// Local developer API (off by default). Listens on 127.0.0.1 only and requires the per-install token.
///
///   POST   /v1/notify            { title, message?, glyph?, accent?, seconds?, priority? }
///   POST   /v1/activity          { id, title, detail?, glyph?, accent?, progress?, priority?, ttlSeconds? }
///   DELETE /v1/activity/{id}
///   POST   /v1/claude            Claude Code "http" hook endpoint (approvals + agent activity)
///   POST   /v1/codex             Codex notify payloads
///   POST   /v1/shell             { event: "start"|"end", id, command, exitCode?, seconds? }
///   GET    /v1/ping
/// </summary>
public sealed class DeveloperApiService : IDisposable
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<string, PendingApproval> _pending = new();
    private readonly ConcurrentDictionary<string, long> _turnStart = new();

    private sealed record PendingApproval(string SessionId, TaskCompletionSource<string?> Decision, Island Island);

    public bool IsRunning => _listener?.IsListening == true;
    public string? Error { get; private set; }

    public void Start()
    {
        Stop();
        var s = SettingsStore.Current.Developer;
        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{s.Port}/");
            _listener.Start();
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => Loop(_cts.Token));
            Error = null;
            Log.Info($"Developer API listening on 127.0.0.1:{s.Port}");
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log.Error("Developer API failed to start", ex);
            _listener = null;
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); _listener?.Close(); } catch { }
        _listener = null;
        foreach (var p in _pending.Values) p.Decision.TrySetResult(null);
        _pending.Clear();
    }

    public void Dispose() => Stop();

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => Handle(ctx), ct);
        }
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            var token = SettingsStore.Current.Developer.Token;
            var auth = req.Headers["Authorization"] ?? "";
            var supplied = auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : (req.Headers["X-NotchX-Token"] ?? req.Headers["X-Notchify-Token"]);
            if (!CryptographicEquals(supplied ?? "", token))
            {
                await Reply(res, 401, new { error = "invalid token" });
                return;
            }

            string body;
            using (var reader = new StreamReader(req.InputStream, Encoding.UTF8)) body = await reader.ReadToEndAsync();
            var json = string.IsNullOrWhiteSpace(body) ? new JsonObject() : JsonNode.Parse(body) as JsonObject ?? new JsonObject();
            var path = req.Url?.AbsolutePath.TrimEnd('/') ?? "";

            switch (req.HttpMethod, path)
            {
                case ("GET", "/v1/ping"):
                    await Reply(res, 200, new { ok = true, app = "NotchX", version = AppInfo.Version });
                    break;
                case ("POST", "/v1/notify"):
                    Ui.Post(() => Notch.Hub.Notify(
                        Str(json, "glyph") ?? Glyphs.Bell, Str(json, "title") ?? "Notification", Str(json, "message"),
                        Str(json, "accent") is { } a ? Ui.Brush(a) : null,
                        Enum.TryParse<IslandPriority>(Str(json, "priority"), true, out var pr) ? pr : IslandPriority.Normal,
                        Num(json, "seconds") ?? 4));
                    await Reply(res, 200, new { ok = true });
                    break;
                case ("POST", "/v1/activity"):
                    var id = "api:" + (Str(json, "id") ?? Guid.NewGuid().ToString("N"));
                    Ui.Post(() => Notch.Hub.Upsert(id, act =>
                    {
                        act.Glyph = Str(json, "glyph") ?? Glyphs.Code;
                        act.Text = Str(json, "title") ?? "";
                        act.Detail = Str(json, "detail");
                        act.Accent = Str(json, "accent") is { } c ? Ui.Brush(c) : Ui.Accent;
                        act.Progress = Num(json, "progress");
                        act.Priority = (int)(Num(json, "priority") ?? 20);
                        act.ExpiresAt = Num(json, "ttlSeconds") is { } ttl ? DateTime.Now.AddSeconds(ttl) : DateTime.Now.AddHours(2);
                    }));
                    await Reply(res, 200, new { ok = true, id });
                    break;
                case ("DELETE", var p) when p.StartsWith("/v1/activity/"):
                    var rid = "api:" + Uri.UnescapeDataString(p["/v1/activity/".Length..]);
                    Ui.Post(() => Notch.Hub.Remove(rid));
                    await Reply(res, 200, new { ok = true });
                    break;
                case ("POST", "/v1/claude"):
                    await Reply(res, 200, await HandleClaudeHook(json));
                    break;
                case ("POST", "/v1/codex"):
                    HandleCodex(json);
                    await Reply(res, 200, new { ok = true });
                    break;
                case ("POST", "/v1/shell"):
                    HandleShell(json);
                    await Reply(res, 200, new { ok = true });
                    break;
                default:
                    await Reply(res, 404, new { error = "not found" });
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("api request", ex);
            try { await Reply(res, 500, new { error = ex.Message }); } catch { }
        }
    }

    // ---------- Claude Code hooks ----------

    private async Task<object> HandleClaudeHook(JsonObject input)
    {
        var dev = SettingsStore.Current.Developer;
        var evt = Str(input, "hook_event_name") ?? "";
        var session = Str(input, "session_id") ?? "claude";
        var project = Path.GetFileName(Str(input, "cwd") ?? "") ?? "";
        var activityId = "claude:" + session;

        // Any later event for this session means the user answered in the terminal — clear stale approval islands.
        if (evt != "PermissionRequest")
            foreach (var (key, p) in _pending.Where(kv => kv.Value.SessionId == session).ToList())
                if (_pending.TryRemove(key, out _)) { p.Decision.TrySetResult(null); Ui.Post(() => Notch.Hub.Dismiss(p.Island)); }

        switch (evt)
        {
            case "PermissionRequest" when dev.AgentApprovals:
            {
                var tool = Str(input, "tool_name") ?? "tool";
                var toolInput = input["tool_input"] as JsonObject;
                var summary = Str(toolInput, "command") ?? Str(toolInput, "file_path") ?? Str(toolInput, "url") ??
                              Str(toolInput, "description") ?? toolInput?.ToJsonString() ?? "";
                if (summary.Length > 140) summary = summary[..140] + "…";

                var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                var key = Str(input, "tool_use_id") ?? Guid.NewGuid().ToString("N");
                var island = new Island
                {
                    Key = "approval:" + key,
                    Glyph = Glyphs.Shield,
                    Title = $"Claude wants to use {tool}" + (string.IsNullOrEmpty(project) ? "" : $" · {project}"),
                    Message = summary,
                    Accent = Ui.Orange,
                    Sticky = true,
                    Priority = IslandPriority.Critical,
                    Actions =
                    {
                        new IslandAction("Deny", () => tcs.TrySetResult("deny"), false, Glyphs.Close),
                        new IslandAction("Allow", () => tcs.TrySetResult("allow"), true, Glyphs.Check),
                    },
                };
                _pending[key] = new PendingApproval(session, tcs, island);
                Ui.Post(() => { Notch.Hub.Show(island); System.Media.SystemSounds.Asterisk.Play(); });

                var winner = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromMinutes(9)));
                _pending.TryRemove(key, out _);
                Ui.Post(() => Notch.Hub.Dismiss(island));
                var decision = winner == tcs.Task ? tcs.Task.Result : null;
                if (decision == null) return new { }; // fall back to the normal terminal prompt
                return new
                {
                    hookSpecificOutput = new
                    {
                        hookEventName = "PermissionRequest",
                        decision = decision == "allow"
                            ? (object)new { behavior = "allow" }
                            : new { behavior = "deny", message = "Denied from NotchX" },
                    },
                };
            }
            case "UserPromptSubmit" when dev.AgentActivity:
                _turnStart[session] = FileLength(Str(input, "transcript_path"));
                Ui.Post(() => Notch.Hub.Upsert(activityId, a =>
                {
                    a.Glyph = Glyphs.Robot; a.Accent = Ui.Orange; a.Text = "Claude"; a.Detail = $"Working · {project}";
                    a.Priority = 40; a.OpenTab = "ai"; a.ExpiresAt = DateTime.Now.AddHours(1);
                }));
                break;
            case "PostToolUse" or "PreToolUse" when dev.AgentActivity:
                var toolName = Str(input, "tool_name");
                Ui.Post(() => { if (Notch.Hub.Has(activityId)) Notch.Hub.Upsert(activityId, a => a.Detail = $"{toolName} · {project}"); });
                break;
            case "Notification":
                var msg = Str(input, "message") ?? "Claude needs your attention";
                Ui.Post(() => Notch.Hub.Notify(Glyphs.Robot, "Claude Code", msg, Ui.Orange, IslandPriority.High, 6, "claude-note:" + session, "ai"));
                break;
            case "Stop" or "SessionEnd" when dev.AgentActivity:
                var cost = _turnStart.TryRemove(session, out var start) ? AiUsageService.CostSince(Str(input, "transcript_path"), start) : null;
                Ui.Post(() =>
                {
                    Notch.Hub.Remove(activityId);
                    Notch.Hub.Notify(Glyphs.Robot, "Claude finished" + (string.IsNullOrEmpty(project) ? "" : $" · {project}"),
                        cost is { } c ? $"Turn cost ≈ ${c:0.000}" : null, Ui.Orange, IslandPriority.Normal, 4, "claude-done:" + session, "ai");
                });
                break;
        }
        return new { };
    }

    private static long FileLength(string? path)
    {
        try { return path != null && File.Exists(path) ? new FileInfo(path).Length : 0; } catch { return 0; }
    }

    private static void HandleCodex(JsonObject json)
    {
        if (!SettingsStore.Current.Developer.AgentActivity) return;
        var type = Str(json, "type");
        if (type != "agent-turn-complete") return;
        var last = Str(json, "last-assistant-message") ?? "";
        if (last.Length > 120) last = last[..120] + "…";
        Ui.Post(() => Notch.Hub.Notify(Glyphs.Robot, "Codex finished", last, Ui.Teal, IslandPriority.Normal, 5, null, "ai"));
    }

    private readonly ConcurrentDictionary<string, (string Command, bool Shown)> _shell = new();

    /// <summary>
    /// Shell activity: the hook reports every command start/end; only commands still running after
    /// <c>minSeconds</c> (default 10) become a live island, so quick commands never flicker.
    /// </summary>
    private void HandleShell(JsonObject json)
    {
        var id = "shell:" + (Str(json, "id") ?? "cmd");
        var command = Str(json, "command") ?? "";
        if (command.Length > 40) command = command[..40] + "…";
        var min = Num(json, "minSeconds") ?? 10;
        if (Str(json, "event") == "start")
        {
            _shell[id] = (command, false);
            _ = Task.Delay(TimeSpan.FromSeconds(min)).ContinueWith(_ =>
            {
                if (!_shell.TryGetValue(id, out var entry)) return;
                _shell[id] = (entry.Command, true);
                Ui.Post(() => Notch.Hub.Upsert(id, a =>
                {
                    a.Glyph = Glyphs.Terminal; a.Accent = Ui.Teal; a.Text = entry.Command; a.Priority = 30;
                    a.Detail = "Running in your terminal"; a.ExpiresAt = DateTime.Now.AddHours(12);
                }));
            });
        }
        else
        {
            var code = (int)(Num(json, "exitCode") ?? 0);
            var secs = Num(json, "seconds") ?? 0;
            var shown = _shell.TryRemove(id, out var entry) && entry.Shown;
            if (!shown && secs < min) return;
            Ui.Post(() =>
            {
                Notch.Hub.Remove(id);
                Notch.Hub.Notify(Glyphs.Terminal, code == 0 ? "Command finished" : $"Command failed ({code})",
                    $"{command} · {Ui.FormatSpan(TimeSpan.FromSeconds(secs))}", code == 0 ? Ui.Green : Ui.Red);
            });
        }
    }

    // ---------- helpers ----------

    private static string? Str(JsonObject? o, string key) =>
        o != null && o.TryGetPropertyValue(key, out var v) && v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : null;

    private static double? Num(JsonObject? o, string key) =>
        o != null && o.TryGetPropertyValue(key, out var v) && v is JsonValue jv && jv.TryGetValue<double>(out var d) ? d : null;

    private static bool CryptographicEquals(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static async Task Reply(HttpListenerResponse res, int status, object body)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(body);
        res.StatusCode = status;
        res.ContentType = "application/json";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    /// <summary>Ready-to-paste Claude Code settings.json "hooks" block pointing at this API.</summary>
    public static string ClaudeHookConfig()
    {
        var s = SettingsStore.Current.Developer;
        var url = $"http://127.0.0.1:{s.Port}/v1/claude";
        object Hook(int timeout) => new[]
        {
            new { hooks = new[] { new { type = "http", url, timeout, headers = new Dictionary<string, string> { ["Authorization"] = "Bearer " + s.Token } } } },
        };
        var config = new
        {
            hooks = new Dictionary<string, object>
            {
                ["PermissionRequest"] = Hook(600),
                ["UserPromptSubmit"] = Hook(5),
                ["PostToolUse"] = Hook(5),
                ["Notification"] = Hook(5),
                ["Stop"] = Hook(5),
            },
        };
        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    }
}
