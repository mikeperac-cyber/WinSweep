<#
Generates the MSIX app assets from one drawing, so every size stays consistent.

    .\tools\New-AppAssets.ps1 -OutputPath .\src\WinSweep.App\Assets
#>
param(
    [string]$OutputPath = (Join-Path $PSScriptRoot "..\src\WinSweep.App\Assets"),
    [string]$Foreground = "#2E7D32",
    [string]$Background = "#10251A"
)

Add-Type -AssemblyName System.Drawing

if (-not (Test-Path $OutputPath)) {
    New-Item -ItemType Directory -Force -Path $OutputPath | Out-Null
}

function New-Logo {
    param(
        [int]$Width,
        [int]$Height,
        [string]$FileName,
        [bool]$Transparent = $false
    )

    $bitmap = New-Object System.Drawing.Bitmap $Width, $Height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

    $graphics.Clear([System.Drawing.Color]::Transparent)

    if (-not $Transparent) {
        $graphics.FillRectangle([System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml($Background)), 0, 0, $Width, $Height)
    }

    # A broom shape: a handle and a swept head.
    $scale = [Math]::Min($Width, $Height) / 100.0
    $handleBrush = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml($Foreground))

    $handle = New-Object System.Drawing.RectangleF(
        [float](50 * $scale), [float](18 * $scale), [float](7 * $scale), [float](38 * $scale))
    $graphics.FillRectangle($handleBrush, $handle)

    $head = @(
        [float](34 * $scale), [float](56 * $scale),
        [float](66 * $scale), [float](56 * $scale),
        [float](58 * $scale), [float](82 * $scale),
        [float](42 * $scale), [float](82 * $scale)
    )
    $graphics.FillPolygon($handleBrush, [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF $head[0], $head[1]),
        (New-Object System.Drawing.PointF $head[2], $head[3]),
        (New-Object System.Drawing.PointF $head[4], $head[5]),
        (New-Object System.Drawing.PointF $head[6], $head[7])
    ))

    $path = Join-Path $OutputPath $FileName
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)

    $graphics.Dispose()
    $bitmap.Dispose()
    $handleBrush.Dispose()

    Write-Host "  $FileName ($Width x $Height)"
}

Write-Host "Writing assets to $OutputPath"

New-Logo -Width 44   -Height 44   -FileName "Square44x44Logo.png"
New-Logo -Width 16   -Height 16   -FileName "Square44x44Logo.targetsize-16_altform-unplated.png"
New-Logo -Width 24   -Height 24   -FileName "Square44x44Logo.targetsize-24_altform-unplated.png"
New-Logo -Width 32   -Height 32   -FileName "Square44x44Logo.targetsize-32_altform-unplated.png"
New-Logo -Width 48   -Height 48   -FileName "Square44x44Logo.targetsize-48_altform-unplated.png"
New-Logo -Width 64   -Height 64   -FileName "Square44x44Logo.targetsize-64_altform-unplated.png"
New-Logo -Width 150  -Height 150  -FileName "Square150x150Logo.png"
New-Logo -Width 310  -Height 310  -FileName "Square310x310Logo.png"
New-Logo -Width 310  -Height 150  -FileName "Wide310x150Logo.png"
New-Logo -Width 50   -Height 50   -FileName "StoreLogo.png"
New-Logo -Width 620  -Height 300  -FileName "SplashScreen.png"

Write-Host "Done."
