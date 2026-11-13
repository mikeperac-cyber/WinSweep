<#
Builds WinSweep.

The WinUI 3 project needs the full MSBuild from Visual Studio, not the dotnet CLI,
because the XAML compiler runs as an MSBuild task.

    .\build.ps1                 # Debug build
    .\build.ps1 -Configuration Release -Publish
#>
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$Publish,
    [switch]$PackageAsMsix,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

function Find-MSBuild {
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere) {
        $path = & $vswhere -latest -products * `
            -requires Microsoft.Component.MSBuild `
            -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
            -find MSBuild\**\Bin\MSBuild.exe | Select-Object -First 1

        if ($path) { return $path }
    }

    $fallback = Get-ChildItem "C:\Program Files\Microsoft Visual Studio" `
        -Filter MSBuild.exe -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like "*\Current\Bin\MSBuild.exe" } |
        Select-Object -First 1

    if ($fallback) { return $fallback.FullName }

    throw "MSBuild was not found. Install Visual Studio 2022 or newer with the .NET desktop workload."
}

$msbuild = Find-MSBuild
$appProject = Join-Path $root "src\WinSweep.App\WinSweep.App.csproj"

Write-Host "Using $msbuild" -ForegroundColor DarkGray

if (-not $SkipTests) {
    Write-Host "`nRunning tests..." -ForegroundColor Cyan
    & dotnet test (Join-Path $root "tests\WinSweep.Core.Tests\WinSweep.Core.Tests.csproj") --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
}

Write-Host "`nBuilding the app ($Configuration)..." -ForegroundColor Cyan
$target = if ($Publish) { "Publish" } else { "Build" }

$properties = @("-restore", "-t:$target", "-p:Configuration=$Configuration", "-p:Platform=x64", "-v:m", "-nologo")
if ($PackageAsMsix) {
    $properties += "-p:PackageAsMsix=true"
}

& $msbuild $appProject @properties
if ($LASTEXITCODE -ne 0) { throw "The build failed." }

$exe = Join-Path $root "src\WinSweep.App\bin\x64\$Configuration\net8.0-windows10.0.19041.0\win-x64\WinSweep.App.exe"
if ($Publish) {
    $exe = Join-Path $root "src\WinSweep.App\bin\x64\$Configuration\net8.0-windows10.0.19041.0\win-x64\publish\WinSweep.App.exe"
}

Write-Host "`nDone. Run it with:" -ForegroundColor Green
Write-Host "  $exe" -ForegroundColor Green

if ($PackageAsMsix) {
    $msix = Get-ChildItem (Join-Path $root "artifacts\msix") -Filter "*.msix" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1

    if ($msix) {
        Write-Host "`nThe package is ready. Sign and install it with:" -ForegroundColor Green
        Write-Host "  .\tools\Sign-And-Install.ps1" -ForegroundColor Green
        Write-Host "  $($msix.FullName)" -ForegroundColor DarkGray
    }
}
