<#
Generates the MSIX app assets, the window icon and the site favicon from one
source logo, so every size stays consistent.

    .\tools\New-AppAssets.ps1
    .\tools\New-AppAssets.ps1 -Source .\art\logo-source.png

The source's flat background is removed by flood-filling from the four corners,
so the logo itself can use any colour except one close to its background.
#>
param(
    [string]$Source = (Join-Path $PSScriptRoot "..\art\logo-source.png"),
    [string]$OutputPath = (Join-Path $PSScriptRoot "..\src\WinSweep.App\Assets"),
    [string]$FaviconPath = (Join-Path $PSScriptRoot "..\site\assets\favicon.png"),
    [int]$Tolerance = 24
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

# Pure pixel work, so it compiles the same under Windows PowerShell and PowerShell 7.
Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;

public static class LogoCutout
{
    // Clears (alpha = 0) every BGRA pixel connected to a corner whose colour is within
    // tolerance of that corner. Returns the bounding box of what is left: x, y, w, h.
    public static int[] RemoveBackground(byte[] px, int w, int h, int stride, int tolerance)
    {
        var cleared = new bool[w * h];
        int[,] corners = { { 0, 0 }, { w - 1, 0 }, { 0, h - 1 }, { w - 1, h - 1 } };
        for (int k = 0; k < 4; k++)
        {
            int ci = corners[k, 1] * stride + corners[k, 0] * 4;
            byte cb = px[ci], cg = px[ci + 1], cr = px[ci + 2];
            var queue = new Queue<int>();
            queue.Enqueue(corners[k, 1] * w + corners[k, 0]);
            while (queue.Count > 0)
            {
                int p = queue.Dequeue();
                if (cleared[p]) continue;
                int x = p % w, y = p / w, i = y * stride + x * 4;
                int d = Math.Max(Math.Abs(px[i] - cb), Math.Max(Math.Abs(px[i + 1] - cg), Math.Abs(px[i + 2] - cr)));
                if (d > tolerance) continue;
                cleared[p] = true;
                if (x + 1 < w) queue.Enqueue(p + 1);
                if (x > 0) queue.Enqueue(p - 1);
                if (y + 1 < h) queue.Enqueue(p + w);
                if (y > 0) queue.Enqueue(p - w);
            }
        }

        // Anti-aliased edge: within a few pixels of the cleared area, fade pixels that are
        // still close to the background and unmix the background colour out of them, so
        // no dark fringe shows on a light surface.
        byte bb = px[0], bg = px[1], br = px[2];
        const int band = 3, ramp = 72;
        var near = (bool[])cleared.Clone();
        for (int pass = 0; pass < band; pass++)
        {
            var next = (bool[])near.Clone();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int p = y * w + x;
                    if (near[p]) continue;
                    if ((x > 0 && near[p - 1]) || (x + 1 < w && near[p + 1]) || (y > 0 && near[p - w]) || (y + 1 < h && near[p + w]))
                    {
                        next[p] = true;
                        int i = y * stride + x * 4;
                        int d = Math.Max(Math.Abs(px[i] - bb), Math.Max(Math.Abs(px[i + 1] - bg), Math.Abs(px[i + 2] - br)));
                        double a = Math.Min(1.0, Math.Max(0.0, (d - tolerance) / (double)ramp));
                        if (a <= 0) { cleared[p] = true; continue; }
                        px[i] = Unmix(px[i], bb, a);
                        px[i + 1] = Unmix(px[i + 1], bg, a);
                        px[i + 2] = Unmix(px[i + 2], br, a);
                        px[i + 3] = (byte)Math.Round(px[i + 3] * a);
                    }
                }
            near = next;
        }

        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (cleared[y * w + x]) { px[y * stride + x * 4 + 3] = 0; continue; }
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
            }
        if (maxX < 0) throw new InvalidOperationException("The whole image matched the background colour.");
        return new[] { minX, minY, maxX - minX + 1, maxY - minY + 1 };
    }

    static byte Unmix(byte c, byte background, double alpha)
    {
        return (byte)Math.Round(Math.Min(255.0, Math.Max(0.0, (c - background * (1 - alpha)) / alpha)));
    }
}
"@

$Source = (Resolve-Path $Source).Path
if (-not (Test-Path $OutputPath)) {
    New-Item -ItemType Directory -Force -Path $OutputPath | Out-Null
}

# Load into a 32bpp ARGB bitmap, cut out the background, crop to the logo.
$argb = [System.Drawing.Imaging.PixelFormat]::Format32bppArgb
$original = [System.Drawing.Image]::FromFile($Source)
$full = New-Object System.Drawing.Bitmap $original.Width, $original.Height, $argb
$g = [System.Drawing.Graphics]::FromImage($full)
$g.DrawImage($original, 0, 0, $original.Width, $original.Height)
$g.Dispose()
$original.Dispose()

$data = $full.LockBits((New-Object System.Drawing.Rectangle 0, 0, $full.Width, $full.Height), [System.Drawing.Imaging.ImageLockMode]::ReadWrite, $argb)
$px = New-Object byte[] ($data.Stride * $full.Height)
[System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $px, 0, $px.Length)
$bounds = [LogoCutout]::RemoveBackground($px, $full.Width, $full.Height, $data.Stride, $Tolerance)
[System.Runtime.InteropServices.Marshal]::Copy($px, 0, $data.Scan0, $px.Length)
$full.UnlockBits($data)
$logo = $full.Clone((New-Object System.Drawing.Rectangle $bounds[0], $bounds[1], $bounds[2], $bounds[3]), $argb)
$full.Dispose()

# Draws the logo centred on a transparent canvas, aspect preserved, filling $Fill of the
# canvas's smaller side.
function New-LogoBitmap([int]$Width, [int]$Height, [double]$Fill) {
    $bitmap = New-Object System.Drawing.Bitmap $Width, $Height, $argb
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias

    $box = [Math]::Min($Width, $Height) * $Fill
    $scale = [Math]::Min($box / $logo.Width, $box / $logo.Height)
    $w = [int][Math]::Round($logo.Width * $scale)
    $h = [int][Math]::Round($logo.Height * $scale)
    $dest = New-Object System.Drawing.Rectangle ([int](($Width - $w) / 2)), ([int](($Height - $h) / 2)), $w, $h

    # Clamp sampling at the edges so the bicubic filter doesn't wrap in pixels from the far side.
    $attributes = New-Object System.Drawing.Imaging.ImageAttributes
    $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
    $graphics.DrawImage($logo, $dest, 0, 0, $logo.Width, $logo.Height, [System.Drawing.GraphicsUnit]::Pixel, $attributes)

    $attributes.Dispose()
    $graphics.Dispose()
    return $bitmap
}

function Save-Logo([int]$Width, [int]$Height, [double]$Fill, [string]$Path) {
    $bitmap = New-LogoBitmap $Width $Height $Fill
    $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    Write-Host "  $(Split-Path $Path -Leaf) ($Width x $Height)"
}

# ICO with PNG-compressed frames (supported since Vista).
function Save-Ico([int[]]$Sizes, [string]$Path) {
    $frames = foreach ($size in $Sizes) {
        $bitmap = New-LogoBitmap $size $size 0.85
        $stream = New-Object System.IO.MemoryStream
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
        , $stream.ToArray()
    }

    $file = [System.IO.File]::Create($Path)
    $writer = New-Object System.IO.BinaryWriter $file
    $writer.Write([UInt16]0)
    $writer.Write([UInt16]1)
    $writer.Write([UInt16]$Sizes.Count)
    $offset = 6 + 16 * $Sizes.Count
    for ($i = 0; $i -lt $Sizes.Count; $i++) {
        $dim = if ($Sizes[$i] -ge 256) { 0 } else { $Sizes[$i] }
        $writer.Write([byte]$dim)
        $writer.Write([byte]$dim)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]32)
        $writer.Write([UInt32]$frames[$i].Length)
        $writer.Write([UInt32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write($frame) }
    $writer.Dispose()
    Write-Host "  $(Split-Path $Path -Leaf) ($($Sizes -join ', '))"
}

Write-Host "Writing assets to $OutputPath"

$square = 0.85
$wide = 0.80
Save-Logo 71  71  $square (Join-Path $OutputPath "Square71x71Logo.png")
Save-Logo 44  44  $square (Join-Path $OutputPath "Square44x44Logo.png")
foreach ($size in 16, 24, 32, 48, 64) {
    Save-Logo $size $size $square (Join-Path $OutputPath "Square44x44Logo.targetsize-$($size)_altform-unplated.png")
}
Save-Logo 150 150 $square (Join-Path $OutputPath "Square150x150Logo.png")
Save-Logo 310 310 $square (Join-Path $OutputPath "Square310x310Logo.png")
Save-Logo 310 150 $wide   (Join-Path $OutputPath "Wide310x150Logo.png")
Save-Logo 50  50  $square (Join-Path $OutputPath "StoreLogo.png")
Save-Logo 620 300 $wide   (Join-Path $OutputPath "SplashScreen.png")

Save-Ico @(16, 24, 32, 48, 64, 256) (Join-Path $OutputPath "WinSweep.ico")

$faviconDir = Split-Path $FaviconPath -Parent
if (-not (Test-Path $faviconDir)) {
    New-Item -ItemType Directory -Force -Path $faviconDir | Out-Null
}
Save-Logo 64 64 $square $FaviconPath

$logo.Dispose()
Write-Host "Done."
