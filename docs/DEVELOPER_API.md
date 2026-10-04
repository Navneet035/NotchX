# Developer API

NotchX can listen on **`127.0.0.1` only** so your scripts, CI, build tools and AI agents can put things on the notch.
It is **off by default**: turn it on in **Settings › Developer**. Every request needs the per-install token
(Settings › Developer › Copy), sent as `Authorization: Bearer <token>`.

Default port: `9999`.

## Endpoints

| Method | Path | Body | What it does |
|---|---|---|---|
| `GET` | `/v1/ping` | — | Health check |
| `POST` | `/v1/notify` | `{ title, message?, glyph?, accent?, seconds?, priority? }` | Transient island. `priority`: `Low`, `Normal`, `High`, `Critical` |
| `POST` | `/v1/activity` | `{ id, title, detail?, glyph?, accent?, progress?, priority?, ttlSeconds? }` | Live activity on the collapsed pill (update by re-posting the same `id`) |
| `DELETE` | `/v1/activity/{id}` | — | Remove a live activity |
| `POST` | `/v1/claude` | Claude Code hook input | Agent approvals + activity (see below) |
| `POST` | `/v1/codex` | Codex `notify` payload | "Codex finished" island |
| `POST` | `/v1/shell` | `{ event: "start"\|"end", id, command, exitCode?, seconds?, minSeconds? }` | Shell activity |

`glyph` is a [Segoe Fluent Icons](https://learn.microsoft.com/windows/apps/design/style/segoe-fluent-icons-font) character, `accent` a colour like `#FF30D158`, `progress` 0–1.

### curl

```bash
curl -X POST http://127.0.0.1:9999/v1/notify \
  -H "Authorization: Bearer $NOTCHIFY_TOKEN" -H "Content-Type: application/json" \
  -d '{"title":"Deploy finished","message":"prod · 2m 14s","accent":"#FF30D158"}'
```

### PowerShell

```powershell
.\hooks\notify.ps1 -Activity build -Title "Building…" -Progress 0.3
.\hooks\notify.ps1 -Activity build -Done
.\hooks\notify.ps1 -Title "Tests passed" -Accent "#FF30D158"
```

## Claude Code: approvals and agent activity

Claude Code can call NotchX directly with **`http` hooks**. In NotchX open **Settings › Developer** and click
**Copy Claude Code hooks config**, then merge it into `%USERPROFILE%\.claude\settings.json`. It looks like:

```json
{
  "hooks": {
    "PermissionRequest": [{ "hooks": [{ "type": "http", "url": "http://127.0.0.1:9999/v1/claude", "timeout": 600,
                                         "headers": { "Authorization": "Bearer <your token>" } }] }],
    "UserPromptSubmit": [{ "hooks": [{ "type": "http", "url": "http://127.0.0.1:9999/v1/claude", "timeout": 5, "headers": { "...": "..." } }] }],
    "PostToolUse":      [{ "hooks": [{ "type": "http", "url": "http://127.0.0.1:9999/v1/claude", "timeout": 5, "headers": { "...": "..." } }] }],
    "Notification":     [{ "hooks": [{ "type": "http", "url": "http://127.0.0.1:9999/v1/claude", "timeout": 5, "headers": { "...": "..." } }] }],
    "Stop":             [{ "hooks": [{ "type": "http", "url": "http://127.0.0.1:9999/v1/claude", "timeout": 5, "headers": { "...": "..." } }] }]
  }
}
```

* **PermissionRequest** — when Claude is blocked on a permission, an **Allow / Deny** island appears. Your answer goes back as
  `{"hookSpecificOutput":{"hookEventName":"PermissionRequest","decision":{"behavior":"allow"}}}`.
  If you answer in the terminal instead, the next hook event for that session clears the island.
  If nobody answers within 9 minutes, NotchX returns no decision and Claude Code falls back to its normal prompt.
* **UserPromptSubmit / PostToolUse** — a "Claude · working" activity on the pill showing the current tool.
* **Stop** — "Claude finished" with an estimated cost for that turn (from the transcript, using the prices in settings).
* **Notification** — e.g. "Claude is waiting for your input".

## Codex

Add to `%USERPROFILE%\.codex\config.toml`:

```toml
notify = ["powershell.exe", "-NoProfile", "-File", "C:\\path\\to\\notchify\\hooks\\codex-notify.ps1"]
```

## Shell activity (PowerShell)

Add to your `$PROFILE`:

```powershell
. "C:\path\to\notchify\hooks\notchify-shell.ps1"
```

Commands that run longer than 10 seconds become a live island; you get a ✓/✗ island when they finish.
Run `Disable-NotchifyShell` to remove the hook from the current session.

## Security

* Binds to `127.0.0.1` only — never reachable from your network.
* Requires the token on every request (constant-time comparison). Regenerate it any time in Settings.
* Off by default; nothing listens until you enable it.
