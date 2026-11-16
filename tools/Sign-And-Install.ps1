<#
Signs the MSIX package with a self-signed certificate and makes it installable.

    .\tools\Sign-And-Install.ps1                 # sign, verify, trust the cert, install
    .\tools\Sign-And-Install.ps1 -SkipInstall    # sign and verify only
    .\tools\Sign-And-Install.ps1 -Uninstall      # remove the installed package

The certificate lives in the current user's personal store, and the private key never
leaves it: the package is signed straight from the store by thumbprint, so no PFX or
password is written to disk or passed on a command line.
#>
param(
    [string]$Artifacts = (Join-Path $PSScriptRoot "..\artifacts"),
    [string]$Publisher = "CN=4F57FA0D-0E3C-4115-91BB-AEA7447D17EF",
    [switch]$SkipInstall,
    [switch]$Uninstall,
    [switch]$Timestamp,
    [switch]$Elevated
)

$ErrorActionPreference = "Stop"

# Package identities to look for. WinSweep.Dev is the pre-rename sideload identity;
# keep it so an older local install is replaced rather than left side by side.
$appxPackageNames = @("MikePerac.WinSweep", "WinSweep.Dev")

# An existing signing certificate lets the script check whether the machine already
# trusts it, which decides if the install needs administrator rights.
$existingCert = Get-ChildItem "Cert:\CurrentUser\My" -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -eq $Publisher } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
$script:CertificateThumbprint = if ($existingCert) { $existingCert.Thumbprint } else { $null }

function Test-IsAdmin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# The Windows package deployment service validates the publisher certificate against
# the machine certificate stores. When the certificate is already trusted there, the
# package can be installed without administrator rights.
function Test-CertificateTrusted {
    $thumbprint = $script:CertificateThumbprint
    if (-not $thumbprint) { return $false }

    # MSIX sideloading trusts the publisher through Trusted People; a machine-wide Root
    # entry is broader than the install needs, so trust is checked there only.
    foreach ($storeName in @("TrustedPeople")) {
        $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($storeName, "LocalMachine")
        $store.Open("ReadOnly")
        $found = $store.Certificates | Where-Object { $_.Thumbprint -eq $thumbprint }
        $store.Close()
        if ($found) { return $true }
    }

    return $false
}

# The Windows package deployment service validates the publisher certificate against
# the machine certificate stores, so installing needs administrator rights the first
# time. The script restarts itself elevated instead of failing.
if (-not $Elevated -and -not $SkipInstall -and ($Uninstall -or -not (Test-CertificateTrusted))) {
    $arguments = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", $PSCommandPath,
        "-Elevated",
        "-Publisher", $Publisher
    )
    # Forward the artifacts location, otherwise the elevated run reverts to its default.
    $arguments += @("-Artifacts", $Artifacts)
    if ($SkipInstall) { $arguments += "-SkipInstall" }
    if ($Uninstall) { $arguments += "-Uninstall" }
    if ($Timestamp) { $arguments += "-Timestamp" }

    # Quote every argument so a value containing a space, quote or semicolon cannot be
    # re-parsed as extra script after elevation.
    $quoted = $arguments | ForEach-Object {
        if ($_ -match '[\s";]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ }
    }

    Write-Host "Requesting administrator rights for the package installation..." -ForegroundColor Cyan
    Start-Process -FilePath "powershell.exe" `
        -ArgumentList ($quoted -join " ") `
        -Verb RunAs `
        -Wait
    return
}

$msixDir = Join-Path $Artifacts "msix"
$signingDir = Join-Path $Artifacts "signing"
$package = Get-ChildItem $msixDir -Filter "*.msix" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

if (-not $package -and -not $Uninstall) {
    throw "No .msix was found in $msixDir. Build it first with -p:PackageAsMsix=true."
}

function Get-SdkTool {
    param([string]$Name)

    $tool = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin" -Recurse -Filter $Name -ErrorAction SilentlyContinue |
        Where-Object { $_.DirectoryName -like "*\x64" } |
        Sort-Object FullName -Descending | Select-Object -First 1

    if (-not $tool) {
        throw "$Name was not found. Install the Windows SDK."
    }

    return $tool.FullName
}

if ($Uninstall) {
    Write-Host "Removing the installed package..." -ForegroundColor Cyan
    foreach ($packageName in $appxPackageNames) {
        Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue | Remove-AppxPackage -ErrorAction SilentlyContinue
    }

    $otherDevApps = @(Get-AppxPackage | Where-Object { $_.Publisher -eq $Publisher -and $_.SignatureKind -eq "Developer" })
    if ($otherDevApps.Count -gt 0) {
        Write-Host "Keeping the development certificate for $Publisher because other local packages still use it ($($otherDevApps.Name -join ', '))." -ForegroundColor Yellow
    }
    else {
        Write-Host "Removing the development certificate..." -ForegroundColor Cyan
        # Only the Trusted People entry this script adds is removed; a machine-wide Root
        # entry is never created, so nothing else is touched.
        foreach ($storeName in @("TrustedPeople")) {
            foreach ($scope in @("LocalMachine", "CurrentUser")) {
                $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($storeName, $scope)
                $store.Open("ReadWrite")
                foreach ($certificate in @($store.Certificates | Where-Object { $_.Subject -eq $Publisher })) {
                    $store.Remove($certificate)
                    Write-Host "  Removed from Cert:\$scope\$storeName"
                }
                $store.Close()
            }
        }
    }

    Write-Host "Done." -ForegroundColor Green
    return
}

New-Item -ItemType Directory -Force -Path $signingDir | Out-Null

# Create or reuse the signing certificate.
$cert = Get-ChildItem "Cert:\CurrentUser\My" -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -eq $Publisher } |
    Sort-Object NotAfter -Descending | Select-Object -First 1

if (-not $cert) {
    Write-Host "Creating a signing certificate for $Publisher..." -ForegroundColor Cyan
    $cert = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $Publisher `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -KeyUsage DigitalSignature `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -HashAlgorithm SHA256 `
        -NotAfter (Get-Date).AddYears(3) `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3")
}

$signtool = Get-SdkTool "signtool.exe"

if (-not (Test-CertificateTrusted)) {
    Write-Host "Trusting the certificate on this machine..." -ForegroundColor Cyan
    # Export only the public certificate; the private key stays in the current user's store.
    $publicBytes = $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)

    foreach ($storeName in @("TrustedPeople")) {
        $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($storeName, "LocalMachine")
        $store.Open("ReadWrite")
        $existing = $store.Certificates | Where-Object { $_.Thumbprint -eq $cert.Thumbprint }
        if (-not $existing) {
            $store.Add([System.Security.Cryptography.X509Certificates.X509Certificate2]::new($publicBytes))
            Write-Host "  Added to Cert:\LocalMachine\$storeName"
        }
        $store.Close()
    }
}
else {
    Write-Host "The signing certificate is already trusted on this machine." -ForegroundColor DarkGray
}

$signedPackagePath = Join-Path $signingDir ($package.BaseName + "-signed.msix")
Copy-Item -Path $package.FullName -Destination $signedPackagePath -Force

Write-Host "Signing $(Split-Path $signedPackagePath -Leaf) (keeping $($package.Name) unsigned for Store upload)..." -ForegroundColor Cyan
# /sha1 selects the certificate from the current user's store, so the private key is
# never exported and no password appears on the signtool command line. The timestamp
# is optional: a release build should use one, but the timestamp chain has to be
# trusted on the machine that checks it, which a development certificate does not need.
$signArgs = @("sign", "/fd", "SHA256", "/s", "My", "/sha1", $cert.Thumbprint)
if ($Timestamp) {
    $signArgs += @("/tr", "http://timestamp.digicert.com", "/td", "SHA256")
}
$signArgs += $signedPackagePath

& $signtool @signArgs
if ($LASTEXITCODE -ne 0) {
    throw "Signing failed with exit code $LASTEXITCODE."
}

Write-Host "Verifying the signature..." -ForegroundColor Cyan
$verifyOutput = & $signtool verify /pa /v $signedPackagePath 2>&1 | Out-String

if ($verifyOutput -match "Successfully Verified") {
    Write-Host "The signature verified." -ForegroundColor Green
}
elseif ($verifyOutput -match "terminated in a root certificate which is not trusted") {
    # The certificate is trusted as a publisher in Trusted People, which is what
    # Windows requires to install the package. The strict policy check still wants
    # a trusted root, so the install below is the real proof.
    Write-Host "The certificate is a self-signed publisher certificate; Windows will accept it as trusted." -ForegroundColor Yellow
}
else {
    Write-Host $verifyOutput
    throw "The signature did not verify."
}

if ($SkipInstall) {
    Write-Host "Signed package at $signedPackagePath. Skipping the install." -ForegroundColor Green
    return
}

Write-Host "Installing the package..." -ForegroundColor Cyan

# The legacy identity is a different package name, so it is removed first.
Get-AppxPackage -Name "WinSweep.Dev" -ErrorAction SilentlyContinue |
    Remove-AppxPackage -ErrorAction SilentlyContinue

# Try an in-place upgrade first: if it works, a failure elsewhere cannot leave the machine
# with no app installed. Windows blocks installing the same version with different content
# (0x80073CFB), which happens on every rebuild until the manifest version is bumped, so
# that one result falls back to removing the old build and installing the new one.
try {
    Add-AppxPackage -Path $signedPackagePath -ForceUpdateFromAnyVersion -ForceApplicationShutdown
    Write-Host "Installed. Look for WinSweep in the Start menu." -ForegroundColor Green
    return
}
catch {
    $sameIdentity = $_.Exception.Message -match '0x80073CFB|already installed'
    if (-not $sameIdentity) {
        throw
    }

    Write-Host "The installed build has the same version but different files; replacing it..." -ForegroundColor Yellow
}

# Keep the user's settings and history: they live in %LOCALAPPDATA%\WinSweep, outside the
# package container, so removing the package does not touch them.
foreach ($existing in Get-AppxPackage -Name "MikePerac.WinSweep" -ErrorAction SilentlyContinue) {
    Remove-AppxPackage -Package $existing.PackageFullName -ErrorAction Stop
}

Add-AppxPackage -Path $signedPackagePath -ForceApplicationShutdown
Write-Host "Installed. Look for WinSweep in the Start menu." -ForegroundColor Green
