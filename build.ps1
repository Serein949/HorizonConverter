#!/usr/bin/env pwsh
# Deprecated: kept for compatibility. It now just calls tools/release.ps1.
#
#   .\build.ps1                 -> full release (desktop + APK + checksums)
#   .\build.ps1 -SkipAndroid    -> desktop only
#
param(
    [switch]$SkipAndroid,
    [switch]$NoSign
)

$script = Join-Path $PSScriptRoot "tools/release.ps1"
& (Join-Path $PSHOME "powershell.exe") -NoProfile -ExecutionPolicy Bypass -File $script @PSBoundParameters
exit $LASTEXITCODE
