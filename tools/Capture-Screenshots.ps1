<#
Captures screenshots of the running app, one PNG per page.

WinUI 3 draws through DirectComposition. This script asks PrintWindow for the
window's own rendered surface (PW_RENDERFULLCONTENT), so the result does not
depend on the window being unobstructed, and falls back to copying screen pixels
when the window reports nothing usable.

    .\tools\Capture-Screenshots.ps1
    .\tools\Capture-Screenshots.ps1 -OutDir site\assets\screenshots -Width 1280 -Height 820
#>
param(
    [string]$Exe = "",
    [string]$OutDir = "",
    [int]$Width = 1280,
    [int]$Height = 820,
    [string[]]$Only = @()
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

if (-not $Exe) {
    $Exe = Join-Path $root "src\WinSweep.App\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\WinSweep.App.exe"
}

if (-not $OutDir) {
    $OutDir = Join-Path $root "site\assets\screenshots"
}

if (-not (Test-Path $Exe)) {
    throw "The app is not built: $Exe. Run .\build.ps1 first."
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;

public static class WinSweepCapture
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT rect, int size);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
}
"@

[void][WinSweepCapture]::SetProcessDPIAware()

# Name, command-line arguments, and how long the page needs before it is settled.
# Click = a point inside the window to press once the page is up (Open, Review, and
# similar), because a page that starts on its landing card shows no detail.
$pages = @(
    @{ Name = "status";   Args = @("--page", "Status");   Wait = 5000 }
    @{ Name = "clean";    Args = @("--page", "Clean", "--scan"); Wait = 14000 }
    @{ Name = "apps";     Args = @("--page", "Apps");     Wait = 24000 }
    @{ Name = "projects"; Args = @("--page", "Artifacts", "--scan"); Wait = 14000 }
    @{ Name = "optimize"; Args = @("--page", "Optimize"); Wait = 4000; Click = @{ X = 640; Y = 594 }; ClickWait = 2500 }
    @{ Name = "analyze";  Args = @("--page", "Analyze");  Wait = 5000; Click = @{ X = 539; Y = 613 }; ClickWait = 30000 }
    @{ Name = "history";  Args = @("--page", "History");  Wait = 3500 }
    @{ Name = "settings"; Args = @("--page", "Settings"); Wait = 3500 }
)

$wanted = @($Only | ForEach-Object { $_ -split ',' } | Where-Object { $_ -and $_.Trim() } | ForEach-Object { $_.Trim() })
if ($wanted.Count -gt 0) {
    $pages = $pages | Where-Object { $wanted -contains $_.Name }
}

function Focus-Window([IntPtr]$handle) {
    $foreground = [WinSweepCapture]::GetForegroundWindow()
    $foregroundThread = [WinSweepCapture]::GetWindowThreadProcessId($foreground, [IntPtr]::Zero)
    $ownThread = [WinSweepCapture]::GetWindowThreadProcessId($handle, [IntPtr]::Zero)

    if ($foregroundThread -ne $ownThread) {
        [void][WinSweepCapture]::AttachThreadInput($foregroundThread, $ownThread, $true)
    }

    [void][WinSweepCapture]::BringWindowToTop($handle)
    [void][WinSweepCapture]::SetForegroundWindow($handle)

    if ($foregroundThread -ne $ownThread) {
        [void][WinSweepCapture]::AttachThreadInput($foregroundThread, $ownThread, $false)
    }
}

# A captured frame with almost no colour variation is a failed render, not a page.
function Test-Blank($bitmap) {
    $colours = New-Object 'System.Collections.Generic.HashSet[int]'
    for ($x = 0; $x -lt $bitmap.Width; $x += 37) {
        for ($y = 0; $y -lt $bitmap.Height; $y += 37) {
            [void]$colours.Add($bitmap.GetPixel($x, $y).ToArgb())
        }
    }

    return $colours.Count -lt 12
}

function Save-Window([IntPtr]$handle, [string]$path) {
    # GetWindowRect covers the whole window including its invisible frame, which is
    # what PrintWindow reproduces; the DWM bounds would clip a few pixels.
    $rect = New-Object WinSweepCapture+RECT
    [void][WinSweepCapture]::GetWindowRect($handle, [ref]$rect)

    $w = $rect.Right - $rect.Left
    $h = $rect.Bottom - $rect.Top
    if ($w -le 0 -or $h -le 0) {
        throw "The window has no measurable size."
    }

    $bitmap = New-Object System.Drawing.Bitmap $w, $h
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    $printed = [WinSweepCapture]::PrintWindow($handle, $hdc, 2)
    $graphics.ReleaseHdc($hdc)

    if (-not $printed -or (Test-Blank $bitmap)) {
        # Fall back to the visible screen area, which needs the window unobstructed.
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size $w, $h))
        $source = "screen"
    }
    else {
        $source = "window"
    }

    $graphics.Dispose()

    if (Test-Blank $bitmap) {
        $bitmap.Dispose()
        throw "The capture of this window came back blank."
    }

    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()

    return "$w x $h ($source)"
}

foreach ($page in $pages) {
    $arguments = @($page.Args)
    $process = Start-Process -FilePath $Exe -ArgumentList $arguments -PassThru

    $handle = [IntPtr]::Zero
    for ($i = 0; $i -lt 60; $i++) {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
        if ($process.HasExited) { break }
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
            $handle = $process.MainWindowHandle
            break
        }
    }

    if ($handle -eq [IntPtr]::Zero) {
        if (-not $process.HasExited) { $process.Kill() }
        throw "The window for '$($page.Name)' never appeared."
    }

    [void][WinSweepCapture]::ShowWindow($handle, 5)
    [void][WinSweepCapture]::SetWindowPos($handle, [IntPtr]::Zero, 60, 40, $Width, $Height, 0x0040)
    Focus-Window $handle
    Start-Sleep -Milliseconds $page.Wait
    Focus-Window $handle
    Start-Sleep -Milliseconds 600

    if ($page.Click) {
        $rect = New-Object WinSweepCapture+RECT
        [void][WinSweepCapture]::GetWindowRect($handle, [ref]$rect)
        $targetX = $rect.Left + $page.Click.X
        $targetY = $rect.Top + $page.Click.Y
        [void][WinSweepCapture]::SetCursorPos($targetX, $targetY)
        Start-Sleep -Milliseconds 150
        [WinSweepCapture]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 60
        [WinSweepCapture]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds $page.ClickWait
    }

    $path = Join-Path $OutDir "$($page.Name).png"
    $size = Save-Window $handle $path
    Write-Host "$($page.Name).png  $size"

    if (-not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit(5000)
    }

    Start-Sleep -Milliseconds 800
}

Write-Host "`nScreenshots written to $OutDir" -ForegroundColor Green
