# Build and run NotchX on Windows.
#   .\build.ps1            → debug build + run
#   .\build.ps1 -Publish   → self-contained single-file NotchX.exe in .\publish
param([switch] $Publish)

$ErrorActionPreference = 'Stop'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host 'Installing the .NET 8 SDK…'
    winget install --id Microsoft.DotNet.SDK.8 -e --accept-source-agreements --accept-package-agreements
    $env:PATH = [Environment]::GetEnvironmentVariable('PATH', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('PATH', 'User')
}

if ($Publish) {
    $rid = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
    dotnet publish src/Notchify/Notchify.csproj -c Release -r $rid --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
    Write-Host "`nDone → publish\NotchX.exe"
} else {
    dotnet run --project src/Notchify/Notchify.csproj
}
