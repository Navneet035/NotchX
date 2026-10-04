# Send any message to the notch from a script, CI job or scheduled task.
#
#   .\notify.ps1 -Title "Build passed" -Message "main @ 3f2a1c" -Glyph ([char]0xE73E) -Accent "#FF30D158"
#   .\notify.ps1 -Activity build -Title "Building…" -Progress 0.4      # live activity on the pill
#   .\notify.ps1 -Activity build -Done                                   # remove it
param(
    [string] $Title = 'NotchX',
    [string] $Message,
    [string] $Glyph,
    [string] $Accent,
    [string] $Activity,
    [double] $Progress = -1,
    [switch] $Done
)

$s = Get-Content ((@((Join-Path $env:APPDATA 'NotchX\settings.json'), (Join-Path $env:APPDATA 'Notchify\settings.json')) | Where-Object { Test-Path $_ } | Select-Object -First 1)) -Raw | ConvertFrom-Json
$base = "http://127.0.0.1:$($s.Developer.Port)/v1"
$headers = @{ Authorization = "Bearer $($s.Developer.Token)" }

if ($Activity -and $Done) {
    Invoke-RestMethod -Method Delete -Uri "$base/activity/$Activity" -Headers $headers | Out-Null
    return
}

$body = @{ title = $Title }
if ($Message) { $body.message = $Message; $body.detail = $Message }
if ($Glyph) { $body.glyph = $Glyph }
if ($Accent) { $body.accent = $Accent }
if ($Progress -ge 0) { $body.progress = $Progress }

if ($Activity) {
    $body.id = $Activity
    Invoke-RestMethod -Method Post -Uri "$base/activity" -Headers $headers -Body ($body | ConvertTo-Json) -ContentType 'application/json' | Out-Null
} else {
    Invoke-RestMethod -Method Post -Uri "$base/notify" -Headers $headers -Body ($body | ConvertTo-Json) -ContentType 'application/json' | Out-Null
}
