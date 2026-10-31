#!/usr/bin/env pwsh
# Builds self-contained single-file releases into .\dist
#   .\dist\cli\AeroflyConv.exe   (command line converter)
#   .\dist\gui\AeroflyConv.exe   (desktop app)
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$FrameworkDependent
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = Join-Path $root "dist"

$publishArgs = @(
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", (-not $FrameworkDependent).ToString().ToLowerInvariant(),
    "/p:PublishSingleFile=true",
    "/p:IncludeNativeLibrariesForSelfExtract=true",
    "/p:EnableCompressionInSingleFile=true",
    "/p:DebugType=none"
)

Write-Host "Publishing CLI ($Runtime)..." -ForegroundColor Cyan
dotnet publish (Join-Path $root "src/AeroflyConv.Cli/AeroflyConv.Cli.csproj") @publishArgs -o (Join-Path $dist "cli")

Write-Host "Publishing GUI ($Runtime)..." -ForegroundColor Cyan
dotnet publish (Join-Path $root "src/AeroflyConv.Gui/AeroflyConv.Gui.csproj") @publishArgs -o (Join-Path $dist "gui")

Write-Host ""
Get-ChildItem $dist -Recurse -Filter *.exe | ForEach-Object {
    "{0,-46} {1,8:N1} MB" -f $_.FullName.Replace($root + "\", ""), ($_.Length / 1MB)
}
