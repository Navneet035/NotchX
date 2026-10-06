# Compose 1920×1080 Microsoft Store screenshots from the README images (docs/images/*-v2.png):
# the notch hangs from the top of a "screen", with a headline and a line of text underneath.
#   .\packaging\store-screenshots.ps1   → docs\store\screenshots\01-home.png …
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$images = Join-Path $root 'docs\images'
$out = Join-Path $root 'docs\store\screenshots'
New-Item -ItemType Directory -Force $out | Out-Null

$W = 1920; $H = 1080
$slate = [System.Drawing.Color]::FromArgb(255, 0x3A, 0x40, 0x58)   # the README images' backdrop
$deep = [System.Drawing.Color]::FromArgb(255, 0x16, 0x19, 0x26)
$white = [System.Drawing.Color]::FromArgb(255, 0xF4, 0xF6, 0xFB)
$soft = [System.Drawing.Color]::FromArgb(255, 0xB4, 0xBC, 0xD4)

function Font([string] $family, [float] $size, [System.Drawing.FontStyle] $style) {
    $f = New-Object System.Drawing.Font($family, $size, $style, [System.Drawing.GraphicsUnit]::Pixel)
    if ($f.Name -ne $family) { $f.Dispose(); $f = New-Object System.Drawing.Font('Segoe UI', $size, $style, [System.Drawing.GraphicsUnit]::Pixel) }
    return $f
}

function Shot([string] $file, [string] $source, [string] $headline, [string] $line, [switch] $Window) {
    $bmp = New-Object System.Drawing.Bitmap $W, $H
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'HighQuality'; $g.InterpolationMode = 'HighQualityBicubic'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $img = [System.Drawing.Image]::FromFile((Join-Path $images $source))

    if ($Window) {
        # A Settings window: headline on top, the window below with a soft shadow.
        $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point 0, $H), $slate, $deep)
        $g.FillRectangle($bg, 0, 0, $W, $H)
        $textTop = 70
        $scale = [Math]::Min(1500 / $img.Width, 760 / $img.Height)
        $iw = [int]($img.Width * $scale); $ih = [int]($img.Height * $scale)
        $x = [int](($W - $iw) / 2); $y = $H - $ih - 60
        for ($i = 18; $i -ge 1; $i--) {
            $shadow = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(6, 0, 0, 0))
            $g.FillRectangle($shadow, $x - $i, $y - $i + 10, $iw + 2 * $i, $ih + 2 * $i); $shadow.Dispose()
        }
        $g.DrawImage($img, $x, $y, $iw, $ih)
    }
    else {
        # The notch image already has the slate backdrop; continue it down into a darker screen.
        $scale = [Math]::Min(1760 / $img.Width, 1.0)
        $iw = [int]($img.Width * $scale); $ih = [int]($img.Height * $scale)
        $g.Clear($slate)
        $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.Point 0, ($ih - 2)), (New-Object System.Drawing.Point 0, $H), $slate, $deep)
        $g.FillRectangle($bg, 0, $ih - 2, $W, $H - $ih + 2)
        $g.DrawImage($img, [int](($W - $iw) / 2), 0, $iw, $ih)
        $textTop = [Math]::Max($ih + 70, 560)
    }

    $center = New-Object System.Drawing.StringFormat; $center.Alignment = 'Center'
    $h1 = Font 'Segoe UI Variable Display' 64 ([System.Drawing.FontStyle]::Bold)
    $p = Font 'Segoe UI Variable Text' 32 ([System.Drawing.FontStyle]::Regular)
    $g.DrawString($headline, $h1, (New-Object System.Drawing.SolidBrush $white), (New-Object System.Drawing.RectangleF 80, $textTop, ($W - 160), 90), $center)
    $g.DrawString($line, $p, (New-Object System.Drawing.SolidBrush $soft), (New-Object System.Drawing.RectangleF 160, ($textTop + 96), ($W - 320), 100), $center)

    $bmp.Save((Join-Path $out $file), [System.Drawing.Imaging.ImageFormat]::Png)
    $img.Dispose(); $g.Dispose(); $bmp.Dispose()
    Write-Host "saved $file"
}

Shot '01-home.png' 'home-v2.png' 'Everything you need, one hover away' 'Clock, weather, sound, brightness, Bluetooth and notes in a notch at the top of your screen.'
Shot '02-music.png' 'now-playing-v2.png' 'Your music, right at the top' 'Album art, controls and synced lyrics for Spotify, YouTube Music and more.'
Shot '03-desktops.png' 'desktops-v2.png' 'Every desktop at a glance' 'Jump to any window, or drag it onto another virtual desktop.'
Shot '04-documents.png' 'documents-v2.png' 'Convert and edit documents' 'Word to PDF, PDF to Word and a PDF editor, all on your PC. Nothing is uploaded.'
Shot '05-themes.png' 'settings-v2.png' 'Make it yours' 'Themes, colours, sizes, tabs and a Home page you arrange yourself.' -Window
