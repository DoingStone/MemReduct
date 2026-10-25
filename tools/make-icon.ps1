<#
.SYNOPSIS
    Generates src/MemReduct.Gui/app.ico — the application and tray icon.

.DESCRIPTION
    The icon is drawn rather than checked in as an opaque binary, so it stays
    reviewable and can be tweaked without an image editor. It is written as a
    multi-resolution .ico carrying PNG payloads (16/24/32/48/64/128/256), which
    Windows Vista and later read natively.

    The design is four ascending bars in a rounded dark tile: memory pressure
    rising, legible down to 16 px in the notification area.

.EXAMPLE
    pwsh -File tools/make-icon.ps1
#>
[CmdletBinding()]
param(
    # Defaults to src/MemReduct.Gui/app.ico next to this script. Resolved in the body:
    # $PSScriptRoot is not yet populated while parameter defaults are being bound.
    [string] $OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $here = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
    $OutputPath = Join-Path $here '..\src\MemReduct.Gui\app.ico'
}

Add-Type -AssemblyName System.Drawing

# Sizes Windows actually asks for: tray (16/24/32), Explorer (48/64/128), jumbo (256).
$sizes = @(16, 24, 32, 48, 64, 128, 256)

function New-IconPng {
    param([int] $Size)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)

        # --- Rounded tile -------------------------------------------------
        $radius = [float]($Size * 0.22)
        $diameter = $radius * 2
        $inset = [float]0
        $rect = New-Object System.Drawing.RectangleF($inset, $inset, [float]$Size, [float]$Size)

        $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
        $tile.AddArc($rect.X, $rect.Y, $diameter, $diameter, 180, 90)
        $tile.AddArc($rect.Right - $diameter, $rect.Y, $diameter, $diameter, 270, 90)
        $tile.AddArc($rect.Right - $diameter, $rect.Bottom - $diameter, $diameter, $diameter, 0, 90)
        $tile.AddArc($rect.X, $rect.Bottom - $diameter, $diameter, $diameter, 90, 90)
        $tile.CloseFigure()

        $bg = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 22, 27, 36))
        $g.FillPath($bg, $tile)
        $bg.Dispose()

        # Hairline rim keeps the tile from dissolving into a dark taskbar.
        $rimPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(70, 120, 220, 170), [float][Math]::Max(1.0, $Size / 48.0))
        $g.DrawPath($rimPen, $tile)
        $rimPen.Dispose()
        $tile.Dispose()

        # --- Four ascending bars ------------------------------------------
        $bars = 4
        $marginX = [float]($Size * 0.20)
        $gap = [float]($Size * 0.075)
        $usable = [float]($Size - 2 * $marginX)
        $barW = [float](($usable - ($bars - 1) * $gap) / $bars)
        $baseline = [float]($Size * 0.80)
        $minH = [float]($Size * 0.20)
        $maxH = [float]($Size * 0.56)

        $topColor = [System.Drawing.Color]::FromArgb(255, 74, 222, 128)   # bright green
        $botColor = [System.Drawing.Color]::FromArgb(255, 34, 150, 120)   # deep teal

        for ($i = 0; $i -lt $bars; $i++) {
            $t = if ($bars -eq 1) { 0.0 } else { $i / ($bars - 1.0) }
            $h = [float]($minH + ($maxH - $minH) * $t)
            $x = [float]($marginX + $i * ($barW + $gap))
            $y = [float]($baseline - $h)

            # Vertical gradient per bar, interpolated by hand so no GradientBrush
            # lifetime bookkeeping is needed.
            $r = [int]($botColor.R + ($topColor.R - $botColor.R) * $t)
            $gr = [int]($botColor.G + ($topColor.G - $botColor.G) * $t)
            $b = [int]($botColor.B + ($topColor.B - $botColor.B) * $t)
            $barColor = [System.Drawing.Color]::FromArgb(255, $r, $gr, $b)

            $barRect = New-Object System.Drawing.RectangleF($x, $y, $barW, $h)
            $barRadius = [float]($barW * 0.34)
            $bd = $barRadius * 2
            $barPath = New-Object System.Drawing.Drawing2D.GraphicsPath
            if ($bd -le 0 -or $barRect.Width -le 0 -or $barRect.Height -le 0) {
                $barPath.AddRectangle($barRect)
            }
            else {
                $barPath.AddArc($barRect.X, $barRect.Y, $bd, $bd, 180, 90)
                $barPath.AddArc($barRect.Right - $bd, $barRect.Y, $bd, $bd, 270, 90)
                $barPath.AddArc($barRect.Right - $bd, $barRect.Bottom - $bd, $bd, $bd, 0, 90)
                $barPath.AddArc($barRect.X, $barRect.Bottom - $bd, $bd, $bd, 90, 90)
                $barPath.CloseFigure()
            }

            $brush = New-Object System.Drawing.SolidBrush($barColor)
            $g.FillPath($brush, $barPath)
            $brush.Dispose()
            $barPath.Dispose()
        }
    }
    finally {
        $g.Dispose()
    }

    $stream = New-Object System.IO.MemoryStream
    $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $stream.ToArray()
    $stream.Dispose()
    $bmp.Dispose()

    return , $bytes
}

# --- Assemble the .ico ----------------------------------------------------
$images = foreach ($s in $sizes) { [pscustomobject]@{ Size = $s; Bytes = (New-IconPng -Size $s) } }

$resolved = [System.IO.Path]::GetFullPath($OutputPath)
$dir = [System.IO.Path]::GetDirectoryName($resolved)
if (-not [System.IO.Directory]::Exists($dir)) { [System.IO.Directory]::CreateDirectory($dir) | Out-Null }

$fs = [System.IO.File]::Create($resolved)
$bw = New-Object System.IO.BinaryWriter($fs)
try {
    $bw.Write([uint16]0)                 # reserved
    $bw.Write([uint16]1)                 # 1 = icon
    $bw.Write([uint16]$images.Count)

    $offset = 6 + 16 * $images.Count
    foreach ($img in $images) {
        # 256 is encoded as 0 in the single-byte dimension fields.
        $dim = if ($img.Size -ge 256) { 0 } else { $img.Size }
        $bw.Write([byte]$dim)            # width
        $bw.Write([byte]$dim)            # height
        $bw.Write([byte]0)               # palette colours
        $bw.Write([byte]0)               # reserved
        $bw.Write([uint16]1)             # colour planes
        $bw.Write([uint16]32)            # bits per pixel
        $bw.Write([uint32]$img.Bytes.Length)
        $bw.Write([uint32]$offset)
        $offset += $img.Bytes.Length
    }

    foreach ($img in $images) { $bw.Write($img.Bytes) }
}
finally {
    $bw.Dispose()
    $fs.Dispose()
}

$info = Get-Item $resolved
Write-Host ("Wrote {0} ({1} bytes, {2} resolutions: {3})" -f $info.FullName, $info.Length, $images.Count, ($sizes -join ', '))
