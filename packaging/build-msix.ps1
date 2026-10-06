# Build the Microsoft Store package (MSIX) for NotchX.
#
#   .\packaging\build-msix.ps1                 → x64 + arm64 .msix files and one .msixbundle in .\msix-out
#   .\packaging\build-msix.ps1 -Arch x64       → just x64
#
# Identity (name / publisher) comes from packaging\identity.json — copy it from Partner Center.
# The Store signs the package when you upload it, so nothing here needs a certificate.
param(
    [ValidateSet('x64', 'arm64')] [string[]] $Arch = @('x64', 'arm64'),
    [string] $Out = 'msix-out'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$work = Join-Path $root 'obj\msix'
$Out = Join-Path $root $Out
Remove-Item $work, $Out -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $work, $Out | Out-Null

# ---------- tools: makeappx + makepri (Windows SDK, or the SDK BuildTools NuGet package) ----------
function Find-SdkTool([string] $name) {
    $roots = @("${env:ProgramFiles(x86)}\Windows Kits\10\bin", "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools")
    $hit = $roots | Where-Object { Test-Path $_ } |
        ForEach-Object { Get-ChildItem $_ -Recurse -Filter $name -ErrorAction SilentlyContinue } |
        Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
    return $hit.FullName
}
$makeappx = Find-SdkTool 'makeappx.exe'
if (-not $makeappx) {
    Write-Host 'Fetching the Windows SDK build tools from NuGet…'
    $tools = Join-Path $work 'sdktools'
    New-Item -ItemType Directory -Force $tools | Out-Null
    '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="Microsoft.Windows.SDK.BuildTools" Version="10.0.*" /></ItemGroup></Project>' |
        Set-Content (Join-Path $tools 'tools.csproj')
    dotnet restore (Join-Path $tools 'tools.csproj') -v q | Out-Null
    $makeappx = Find-SdkTool 'makeappx.exe'
}
$makepri = Join-Path (Split-Path $makeappx) 'makepri.exe'
if (-not (Test-Path $makepri)) { $makepri = Find-SdkTool 'makepri.exe' }
Write-Host "makeappx: $makeappx"

# ---------- identity and version ----------
$identity = Get-Content (Join-Path $PSScriptRoot 'identity.json') -Raw | ConvertFrom-Json
[xml] $proj = Get-Content 'src\Notchify\Notchify.csproj'
$version = ($proj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
$packageVersion = "$version.0"   # the Store requires the 4th part to be 0
if ($identity.IdentityName -like '*Placeholder*') {
    Write-Warning 'packaging\identity.json still has placeholder values: this package builds and installs locally but Partner Center will reject it.'
}

# ---------- Store images, generated from the app icon ----------
Add-Type -AssemblyName System.Drawing
$iconSource = Join-Path $root 'Icon\notchify-1024.png'
$images = Join-Path $work 'Images'
New-Item -ItemType Directory -Force $images | Out-Null
function Save-Image([string] $file, [int] $w, [int] $h, [double] $iconShare = 1.0) {
    $src = [System.Drawing.Image]::FromFile($iconSource)
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.SmoothingMode = 'HighQuality'; $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $size = [int]([Math]::Min($w, $h) * $iconShare)
    $g.DrawImage($src, [int](($w - $size) / 2), [int](($h - $size) / 2), $size, $size)
    $bmp.Save((Join-Path $images $file), [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose(); $src.Dispose()
}
foreach ($s in @(@{q='scale-100';f=1}, @{q='scale-200';f=2}, @{q='scale-400';f=4})) {
    Save-Image "Square44x44Logo.$($s.q).png" (44 * $s.f) (44 * $s.f)
    Save-Image "Square150x150Logo.$($s.q).png" (150 * $s.f) (150 * $s.f) 0.66
    Save-Image "Wide310x150Logo.$($s.q).png" (310 * $s.f) (150 * $s.f) 0.66
    Save-Image "StoreLogo.$($s.q).png" (50 * $s.f) (50 * $s.f)
}
# Taskbar / Start list sizes, without the coloured plate behind them.
foreach ($t in 16, 24, 32, 48, 256) {
    Save-Image "Square44x44Logo.targetsize-$t.png" $t $t
    Save-Image "Square44x44Logo.targetsize-${t}_altform-unplated.png" $t $t
}

# ---------- one .msix per architecture ----------
$built = @()
foreach ($a in $Arch) {
    Write-Host "`n== $a =="
    $layout = Join-Path $work "layout-$a"
    dotnet publish src\Notchify\Notchify.csproj -c Release -r "win-$a" --self-contained true -p:PublishSingleFile=false -o $layout -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $a" }
    Copy-Item $images (Join-Path $layout 'Images') -Recurse -Force

    $manifest = (Get-Content (Join-Path $PSScriptRoot 'AppxManifest.xml') -Raw).
        Replace('$IDENTITY_NAME$', $identity.IdentityName).
        Replace('$STORE_NAME$', [Security.SecurityElement]::Escape($identity.StoreName)).
        Replace('$PUBLISHER_DISPLAY_NAME$', [Security.SecurityElement]::Escape($identity.PublisherDisplayName)).
        Replace('$PUBLISHER$', [Security.SecurityElement]::Escape($identity.Publisher)).
        Replace('$VERSION$', $packageVersion).
        Replace('$ARCH$', $a)
    [IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $manifest, (New-Object Text.UTF8Encoding $false))

    # resources.pri lets Windows pick the right image size (scale / targetsize variants).
    $pri = Join-Path $work "pri-$a"
    New-Item -ItemType Directory -Force $pri | Out-Null
    Copy-Item (Join-Path $layout 'Images') (Join-Path $pri 'Images') -Recurse -Force
    Copy-Item (Join-Path $layout 'AppxManifest.xml') $pri
    & $makepri createconfig /cf (Join-Path $work 'priconfig.xml') /dq en-US /pv 10.0.0 /o | Out-Null
    & $makepri new /pr $pri /cf (Join-Path $work 'priconfig.xml') /mn (Join-Path $pri 'AppxManifest.xml') /of (Join-Path $layout 'resources.pri') /o | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "makepri failed for $a" }

    $msix = Join-Path $Out "NotchX_${packageVersion}_$a.msix"
    & $makeappx pack /d $layout /p $msix /o | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed for $a" }
    $built += $msix
    Write-Host "built $msix"
}

# ---------- bundle: the single file to upload to Partner Center ----------
if ($built.Count -gt 1) {
    $bundleDir = Join-Path $work 'bundle'
    New-Item -ItemType Directory -Force $bundleDir | Out-Null
    $built | ForEach-Object { Copy-Item $_ $bundleDir }
    $bundle = Join-Path $Out "NotchX_$packageVersion.msixbundle"
    & $makeappx bundle /d $bundleDir /p $bundle /bv $packageVersion /o | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'makeappx bundle failed' }
    Write-Host "built $bundle"
}
