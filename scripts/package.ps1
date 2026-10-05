# Builds artifacts/AirDefenderCoop-<version>.zip: BepInEx 5 loader + the co-op plugin + installer.
# Contains no game files.
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration

$dll = Join-Path $root "src\AirDefender.Coop\bin\$Configuration\netstandard2.1\AirDefender.Coop.dll"
$version = (Get-Item $dll).VersionInfo.ProductVersion -replace '\+.*$', ''
$bepZip = Join-Path $root '.deps\BepInEx_win_x64_5.4.23.5.zip'
if (-not (Test-Path $bepZip)) { throw "Missing $bepZip (download BepInEx 5.4.23.5 win x64 first)" }

$stage = Join-Path $root "artifacts\stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$game = Join-Path $stage 'game'
New-Item -ItemType Directory -Force $game | Out-Null
Expand-Archive -Path $bepZip -DestinationPath $game
Remove-Item (Join-Path $game 'changelog.txt') -ErrorAction SilentlyContinue
$plugins = Join-Path $game 'BepInEx\plugins\AirDefenderCoop'
New-Item -ItemType Directory -Force $plugins | Out-Null
Copy-Item $dll $plugins

Copy-Item (Join-Path $root 'package\Install-AirDefenderCoop.ps1') $stage
Copy-Item (Join-Path $root 'package\README-COOP.txt') $stage
Set-Content -Path (Join-Path $stage 'Install.bat') -Value "@echo off`r`npowershell -NoProfile -ExecutionPolicy Bypass -File `"%~dp0Install-AirDefenderCoop.ps1`"`r`npause`r`n"

$zip = Join-Path $root "artifacts\AirDefenderCoop-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Write-Host "Package: $zip ($([math]::Round((Get-Item $zip).Length / 1KB)) KB)"
