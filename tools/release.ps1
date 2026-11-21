<#
.SYNOPSIS
    Builds, signs and packages a Horizon Converter release.

.DESCRIPTION
    Produces dist/v<version>/ with:
        cli/HorizonConverter.exe       self-contained single file
        gui/HorizonConverter.exe       self-contained single file
        android/HorizonConverter-<version>.apk
        SHA256SUMS.txt

    The version is taken from src/AeroflyConv.Core/ProductInfo.cs unless -Version is given.
    The Android build picks up keys/signing.local.props (release keystore) when present,
    otherwise it falls back to the debug key - the script warns about that.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/release.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File tools/release.ps1 -SkipAndroid -NoSign
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$SkipAndroid,
    [switch]$NoSign,
    [switch]$SkipDesktop
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Warn($text) { Write-Host "!! $text" -ForegroundColor Yellow }

$productInfo = Join-Path $root 'src/AeroflyConv.Core/ProductInfo.cs'
if (-not $Version) {
    $match = Select-String -Path $productInfo -Pattern 'Version\s*=\s*"([^"]+)"' | Select-Object -First 1
    if (-not $match) { throw "cannot read Version from $productInfo" }
    $Version = $match.Matches[0].Groups[1].Value
}
Write-Host "Horizon Converter $Version"

$outDir = Join-Path $root "dist/v$Version"
New-Item -ItemType Directory -Force $outDir | Out-Null

if (-not $SkipDesktop) {
    Step 'Build (Release)'
    dotnet build AeroflyConv.slnx -c Release
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }

    $common = @(
        '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '/p:PublishSingleFile=true',
        '/p:IncludeNativeLibrariesForSelfExtract=true',
        '/p:EnableCompressionInSingleFile=true',
        '/p:DebugType=none'
    )

    Step 'Publish CLI'
    dotnet publish src/AeroflyConv.Cli/AeroflyConv.Cli.csproj @common -o (Join-Path $outDir 'cli')
    if ($LASTEXITCODE -ne 0) { throw 'publish cli failed' }

    Step 'Publish GUI'
    dotnet publish src/AeroflyConv.Gui/AeroflyConv.Gui.csproj @common -o (Join-Path $outDir 'gui')
    if ($LASTEXITCODE -ne 0) { throw 'publish gui failed' }

    if (-not $NoSign) {
        $signer = Join-Path $root 'tools/sign-app.ps1'
        if (Test-Path $signer) {
            foreach ($exe in @('cli/HorizonConverter.exe', 'gui/HorizonConverter.exe')) {
                $path = Join-Path $outDir $exe
                Step "Sign $exe"
                & powershell -NoProfile -ExecutionPolicy Bypass -File $signer -Paths $path
                $status = (Get-AuthenticodeSignature $path).Status
                if ($status -ne 'Valid') { Warn "signature status: $status" }
            }
        } else {
            Warn 'tools/sign-app.ps1 not found - skipping signature'
        }
    }
}

if (-not $SkipAndroid) {
    $props = Join-Path $root 'keys/signing.local.props'
    if (-not (Test-Path $props)) {
        Warn 'keys/signing.local.props missing - the APK will be signed with the DEBUG key'
    }

    Step 'Build Android APK'
    dotnet build src/AeroflyConv.Android/AeroflyConv.Android.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'android build failed' }

    $apk = Get-ChildItem 'src/AeroflyConv.Android/bin/Release/net10.0-android/*-Signed.apk' |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $apk) { throw 'APK not found' }

    $androidDir = Join-Path $outDir 'android'
    New-Item -ItemType Directory -Force $androidDir | Out-Null
    $target = Join-Path $androidDir "HorizonConverter-$Version.apk"
    Copy-Item $apk.FullName $target -Force
    Write-Host "APK: $([math]::Round($apk.Length / 1MB, 2)) MB"
}

Step 'Checksums'
$sums = Join-Path $outDir 'SHA256SUMS.txt'
Get-ChildItem $outDir -Recurse -File |
    Where-Object { $_.Name -ne 'SHA256SUMS.txt' -and $_.Extension -ne '.md' } |
    Sort-Object FullName |
    ForEach-Object {
        $rel = $_.FullName.Substring($outDir.Length + 1).Replace('\', '/')
        "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower())  $rel"
    } | Set-Content $sums -Encoding ascii

Write-Host ''
Get-ChildItem $outDir -Recurse -File | Select-Object @{n = 'file'; e = { $_.FullName.Substring($outDir.Length + 1) } }, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } } | Format-Table -AutoSize
Write-Host "Release ready in $outDir" -ForegroundColor Green
Write-Host 'Next: create the tag and let .github/workflows/release.yml publish it (see RELEASING.md).'
