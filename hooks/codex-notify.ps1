# NotchX ← Codex CLI. Shows an island when a Codex turn completes.
#
# In %USERPROFILE%\.codex\config.toml add:
#   notify = ["powershell.exe", "-NoProfile", "-File", "C:\\path\\to\\notchify\\hooks\\codex-notify.ps1"]
#
# Codex passes the event JSON as the first argument.
param([string] $Payload)

$settingsPath = (@((Join-Path $env:APPDATA 'NotchX\settings.json'), (Join-Path $env:APPDATA 'Notchify\settings.json')) | Where-Object { Test-Path $_ } | Select-Object -First 1)
if (-not $Payload -or -not (Test-Path $settingsPath)) { exit 0 }
$s = Get-Content $settingsPath -Raw | ConvertFrom-Json
try {
    Invoke-RestMethod -Uri "http://127.0.0.1:$($s.Developer.Port)/v1/codex" -Method Post `
        -Headers @{ Authorization = "Bearer $($s.Developer.Token)" } `
        -Body $Payload -ContentType 'application/json' -TimeoutSec 2 | Out-Null
} catch { }
exit 0
