# Signs AeroflyConv.exe with a local self-signed code-signing certificate so Windows
# SmartScreen stops showing "Windows protected your PC / 仍要运行".
# Runs entirely in the current-user certificate stores - no admin rights required.
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File sign-app.ps1 [-Paths <exe...>]
param(
    [string[]]$Paths
)

$ErrorActionPreference = "Stop"
$subject = "CN=Horizon Converter Local Publishing"

$root = Split-Path -Parent $PSScriptRoot
$targets = if ($Paths) {
    $Paths
} else {
    $dist = Join-Path $root "dist"
    if (Test-Path $dist) {
        Get-ChildItem $dist -Recurse -Filter "HorizonConverter.exe" | ForEach-Object { $_.FullName }
    } else {
        Get-ChildItem $PSScriptRoot -Recurse -Filter "HorizonConverter.exe" | ForEach-Object { $_.FullName }
    }
}

if (-not $targets) {
    Write-Host "No HorizonConverter.exe found to sign."
    exit 1
}

# 1) certificate: reuse or create (current user, 5 years)
$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
    Where-Object { $_.Subject -eq $subject } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if (-not $cert) {
    Write-Host "Creating code-signing certificate '$subject' ..."
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $subject `
        -KeyUsage DigitalSignature `
        -FriendlyName "AeroflyConv Local Publishing" `
        -CertStoreLocation "Cert:\CurrentUser\My" `
        -NotAfter (Get-Date).AddYears(5) `
        -KeyExportPolicy Exportable

    $cer = Join-Path $env:TEMP "AeroflyConv-codesign.cer"
    Export-Certificate -Cert $cert -FilePath $cer -Force | Out-Null

    # trust it for this user: root (chain) + trusted publisher (SmartScreen)
    certutil -addstore -user -f Root $cer | Out-Null
    certutil -addstore -user -f TrustedPublisher $cer | Out-Null
    $keep = Join-Path $PSScriptRoot "AeroflyConv-codesign.cer"
    Copy-Item $cer $keep -Force
    Write-Host "Certificate trusted for the current user (undo: certutil -delstore -user Root <thumbprint>)."
}

Write-Host "Using certificate $($cert.Thumbprint)"

# 2) sign every build
$failed = 0
foreach ($target in $targets) {
    try {
        Set-AuthenticodeSignature -FilePath $target -Certificate $cert -HashAlgorithm SHA256 -ErrorAction Stop | Out-Null
    } catch {
        $failed++
        Write-Warning "could not sign $target (still running?): $($_.Exception.Message)"
        continue
    }
    $check = Get-AuthenticodeSignature $target
    Write-Host ("  {0,-10} {1}" -f $check.Status, $target)
}

if ($failed -gt 0) {
    Write-Host ""
    Write-Host "Close AeroflyConv and run this again so the locked file can be signed."
}
