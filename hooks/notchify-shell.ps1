# NotchX shell activity for PowerShell 7+ / Windows PowerShell 5.1 (with PSReadLine).
#
# Long-running commands become a live island on the notch; you get a "finished" island when they end.
# Requires: NotchX › Settings › Developer › "Enable the developer API".
#
# Enable:   add this line to your $PROFILE
#             . "C:\path\to\notchify\hooks\notchify-shell.ps1"
# Disable:  run  Disable-NotchifyShell   (and remove the line from $PROFILE)

$settingsPath = (@((Join-Path $env:APPDATA 'NotchX\settings.json'), (Join-Path $env:APPDATA 'Notchify\settings.json')) | Where-Object { Test-Path $_ } | Select-Object -First 1)
if (-not (Test-Path $settingsPath)) { return }
$notchifySettings = Get-Content $settingsPath -Raw | ConvertFrom-Json

$global:NotchifyShell = @{
    Uri        = "http://127.0.0.1:$($notchifySettings.Developer.Port)/v1/shell"
    Headers    = @{ Authorization = "Bearer $($notchifySettings.Developer.Token)" }
    MinSeconds = 10          # commands shorter than this never show an island
    Current    = $null
    Prompt     = $function:prompt
}

function global:Send-NotchifyShell([hashtable] $Body) {
    try {
        Invoke-RestMethod -Uri $NotchifyShell.Uri -Method Post -Headers $NotchifyShell.Headers `
            -Body ($Body | ConvertTo-Json -Compress) -ContentType 'application/json' -TimeoutSec 1 | Out-Null
    } catch { }
}

# "preexec": runs when you press Enter.
Set-PSReadLineKeyHandler -Key Enter -BriefDescription 'NotchifyAcceptLine' -ScriptBlock {
    $line = $null; $cursor = $null
    [Microsoft.PowerShell.PSConsoleReadLine]::GetBufferState([ref]$line, [ref]$cursor)
    if ($line -and $line.Trim()) {
        $id = [guid]::NewGuid().ToString('N').Substring(0, 8)
        $global:NotchifyShell.Current = @{ id = $id; command = $line.Trim(); started = Get-Date }
        Send-NotchifyShell @{ event = 'start'; id = $id; command = $line.Trim(); minSeconds = $NotchifyShell.MinSeconds }
    }
    [Microsoft.PowerShell.PSConsoleReadLine]::AcceptLine()
}

# "precmd": the prompt runs after each command finishes.
function global:prompt {
    $ok = $?
    $cur = $global:NotchifyShell.Current
    if ($cur) {
        $code = if ($ok) { 0 } elseif ($LASTEXITCODE) { $LASTEXITCODE } else { 1 }
        $secs = [math]::Round(((Get-Date) - $cur.started).TotalSeconds, 1)
        Send-NotchifyShell @{ event = 'end'; id = $cur.id; command = $cur.command; exitCode = $code; seconds = $secs; minSeconds = $NotchifyShell.MinSeconds }
        $global:NotchifyShell.Current = $null
    }
    & $global:NotchifyShell.Prompt
}

function global:Disable-NotchifyShell {
    Set-PSReadLineKeyHandler -Key Enter -Function AcceptLine
    $function:global:prompt = $global:NotchifyShell.Prompt
    Remove-Item function:\Send-NotchifyShell -ErrorAction SilentlyContinue
    Remove-Variable NotchifyShell -Scope Global -ErrorAction SilentlyContinue
    Write-Host 'NotchX shell activity disabled.'
}
