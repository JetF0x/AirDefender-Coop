# Copies the built plugin into <GameDir>\BepInEx\plugins\AirDefenderCoop. Close the game first.
param([string]$GameDir, [string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $GameDir) { $GameDir = Split-Path -Parent $root }
if (-not (Test-Path (Join-Path $GameDir 'BepInEx\core\BepInEx.dll'))) { throw "BepInEx is not installed in $GameDir" }
$dest = Join-Path $GameDir 'BepInEx\plugins\AirDefenderCoop'
New-Item -ItemType Directory -Force $dest | Out-Null
$bin = Join-Path $root "src\AirDefender.Coop\bin\$Configuration\netstandard2.1"
Copy-Item (Join-Path $bin 'AirDefender.Coop.dll') $dest -Force
if (Test-Path (Join-Path $bin 'AirDefender.Coop.pdb')) { Copy-Item (Join-Path $bin 'AirDefender.Coop.pdb') $dest -Force }
# Lets the game start directly from this folder without Steam relaunching the registered install.
Set-Content -NoNewline -Path (Join-Path $GameDir 'steam_appid.txt') -Value '3985030'
Write-Host "Installed to $dest"
