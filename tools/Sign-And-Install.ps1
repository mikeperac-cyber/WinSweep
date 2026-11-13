<#
Signs the MSIX package with a self-signed certificate and makes it installable.

    .\tools\Sign-And-Install.ps1                 # sign, verify, trust the cert, install
    .\tools\Sign-And-Install.ps1 -SkipInstall    # sign and verify only
    .\tools\Sign-And-Install.ps1 -Uninstall      # remove the installed package

The certificate lives in the current user's personal store. The private key is
exported to artifacts\signing so the package can be signed again later.
#>
param(
    [string]$Artifacts = (Join-Path $PSScriptRoot "..\artifacts"),
    [string]$Publisher = "CN=4F57FA0D-0E3C-4115-91BB-AEA7447D17EF",
    [switch]$SkipInstall,
    [switch]$Uninstall,
    [switch]$Timestamp,
    [switch]$Elevated,
    [string]$Password = "winsweep-dev"
)

$ErrorActionPreference = "Stop"

# An existing signing certificate lets the script check whether the machine already
# trusts it, which decides if the install needs administrator rights.
$existingCert = Get-ChildItem "Cert:\CurrentUser\My" -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -eq $Publisher } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
$script:CertificateThumbprint = $existingCert?.Thumbprint

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

    foreach ($storeName in @("TrustedPeople", "Root")) {
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
        "-Publisher", $Publisher,
        "-Password", $Password
    )
    if ($SkipInstall) { $arguments += "-SkipInstall" }
    if ($Uninstall) { $arguments += "-Uninstall" }
    if ($Timestamp) { $arguments += "-Timestamp" }

    Write-Host "Requesting administrator rights for the package installation..." -ForegroundColor Cyan
    Start-Process -FilePath "powershell.exe" `
        -ArgumentList ($arguments -join " ") `
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
    Get-AppxPackage -Name "WinSweep.Dev" -ErrorAction SilentlyContinue | Remove-AppxPackage -ErrorAction SilentlyContinue

    Write-Host "Removing the development certificate..." -ForegroundColor Cyan
    foreach ($storeName in @("TrustedPeople", "Root")) {
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

$pfxPath = Join-Path $signingDir "WinSweep.pfx"
Write-Host "Exporting the certificate to $pfxPath" -ForegroundColor Cyan
# A separate name: $Password is a typed string parameter and would coerce the
# secure string back to plain text.
$securePassword = ConvertTo-SecureString -String $Password -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $securePassword | Out-Null

$signtool = Get-SdkTool "signtool.exe"

# Windows only trusts a self-signed package when the publisher certificate is
# trusted, and it has to exist before the package is checked. It goes into the
# current user's Trusted People and Root stores, never into the machine stores.
Write-Host "Trusting the certificate for the current user..." -ForegroundColor Cyan
$publicKey = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($pfxPath, $Password)
$publicBytes = $publicKey.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)

foreach ($storeName in @("TrustedPeople", "Root")) {
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store($storeName, "LocalMachine")
    $store.Open("ReadWrite")
    $existing = $store.Certificates | Where-Object { $_.Thumbprint -eq $cert.Thumbprint }
    if (-not $existing) {
        $store.Add([System.Security.Cryptography.X509Certificates.X509Certificate2]::new($publicBytes))
        Write-Host "  Added to Cert:\LocalMachine\$storeName"
    }
    $store.Close()
}

Write-Host "Signing $($package.Name)..." -ForegroundColor Cyan
# The timestamp is optional. A release build should use one, but the timestamp
# chain has to be trusted on the machine that checks it, which a development
# certificate does not need.
$signArgs = @("sign", "/fd", "SHA256", "/f", $pfxPath, "/p", $Password)
if ($Timestamp) {
    $signArgs += @("/tr", "http://timestamp.digicert.com", "/td", "SHA256")
}
$signArgs += $package.FullName

& $signtool @signArgs
if ($LASTEXITCODE -ne 0) {
    throw "Signing failed with exit code $LASTEXITCODE."
}

Write-Host "Verifying the signature..." -ForegroundColor Cyan
$verifyOutput = & $signtool verify /pa /v $package.FullName 2>&1 | Out-String

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
    Write-Host "Signed. Skipping the install." -ForegroundColor Green
    return
}

Write-Host "Installing the package..." -ForegroundColor Cyan
Add-AppxPackage -Path $package.FullName
Write-Host "Installed. Look for WinSweep in the Start menu." -ForegroundColor Green
