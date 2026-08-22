param(
    [string]$LogoPath = "..\..\..\android-app\app\src\main\res\drawable-nodpi\audiochoice_logo.png"
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourcePath = [System.IO.Path]::GetFullPath((Join-Path $scriptRoot $LogoPath))
$logo = [System.Drawing.Image]::FromFile($sourcePath)

function New-BrandedBitmap([int]$width, [int]$height, [string]$path, [double]$scale = 0.82) {
    $bitmap = [System.Drawing.Bitmap]::new($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.Clear([System.Drawing.Color]::FromArgb(8, 16, 13))
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $side = [int]([Math]::Min($width, $height) * $scale)
    $x = [int](($width - $side) / 2)
    $y = [int](($height - $side) / 2)
    $graphics.DrawImage($logo, $x, $y, $side, $side)
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Bmp)
    $graphics.Dispose()
    $bitmap.Dispose()
}

# Inno Setup's normal and small wizard artwork.
New-BrandedBitmap 164 314 (Join-Path $scriptRoot "wizard-large.bmp") 0.82
New-BrandedBitmap 55 55 (Join-Path $scriptRoot "wizard-small.bmp") 0.82

# A standard Windows icon for the setup file and its shortcuts.
$iconBitmap = [System.Drawing.Bitmap]::new(256, 256)
$iconGraphics = [System.Drawing.Graphics]::FromImage($iconBitmap)
$iconGraphics.Clear([System.Drawing.Color]::FromArgb(8, 16, 13))
$iconGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$iconGraphics.DrawImage($logo, 0, 0, 256, 256)
$iconHandle = $iconBitmap.GetHicon()
$icon = [System.Drawing.Icon]::FromHandle($iconHandle)
$file = [System.IO.File]::Create((Join-Path $scriptRoot "AudioChoiceCompanion.ico"))
$icon.Save($file)
$file.Dispose()
$icon.Dispose()
$iconGraphics.Dispose()
$iconBitmap.Dispose()
$logo.Dispose()
