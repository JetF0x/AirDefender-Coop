# Installs the Air Defender co-op mod (BepInEx 5 + plugin) into your Steam copy of Air Defender.
# Usage: right-click > Run with PowerShell, or:  powershell -ExecutionPolicy Bypass -File Install-AirDefenderCoop.ps1 [-GameDir <path>] [-Uninstall]
param([string]$GameDir, [switch]$Uninstall)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$appId = '3985030'

function Find-GameDir {
    $steam = (Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam' -Name SteamPath -ErrorAction SilentlyContinue).SteamPath
    if (-not $steam) { return $null }
    $libs = @($steam)
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) {
        foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) { $libs += $m.Groups[1].Value.Replace('\\', '\') }
    }
    foreach ($lib in $libs | Select-Object -Unique) {
        $acf = Join-Path $lib "steamapps\appmanifest_$appId.acf"
        if (Test-Path $acf) {
            $dir = [regex]::Match((Get-Content $acf -Raw), '"installdir"\s+"([^"]+)"').Groups[1].Value
            $full = Join-Path $lib "steamapps\common\$dir"
            if (Test-Path (Join-Path $full 'Air Defender.exe')) { return $full }
        }
    }
    return $null
}

if (-not $GameDir) { $GameDir = Find-GameDir }
if (-not $GameDir -or -not (Test-Path (Join-Path $GameDir 'Air Defender.exe'))) {
    Write-Host 'Could not find Air Defender. Run again with -GameDir "<folder containing Air Defender.exe>".' -ForegroundColor Red
    exit 1
}
if (Get-Process 'Air Defender' -ErrorAction SilentlyContinue) {
    Write-Host 'Please close Air Defender first.' -ForegroundColor Red
    exit 1
}
Write-Host "Game folder: $GameDir"

$files = Get-ChildItem -Path (Join-Path $here 'game') -Recurse -File
if ($Uninstall) {
    # Remove only what this package installed; BepInEx configs and logs stay unless BepInEx is now empty.
    foreach ($f in $files) {
        $rel = $f.FullName.Substring((Join-Path $here 'game').Length + 1)
        $dst = Join-Path $GameDir $rel
        if (Test-Path $dst) { Remove-Item $dst -Force }
    }
    Write-Host 'Co-op mod removed. The game is back to normal.' -ForegroundColor Green
    exit 0
}

foreach ($f in $files) {
    $rel = $f.FullName.Substring((Join-Path $here 'game').Length + 1)
    $dst = Join-Path $GameDir $rel
    New-Item -ItemType Directory -Force (Split-Path -Parent $dst) | Out-Null
    Copy-Item $f.FullName $dst -Force
}
Write-Host 'Air Defender co-op installed.' -ForegroundColor Green
Write-Host 'Start the game from Steam. Press F9 in game for the co-op panel.'
