# Shared helpers for snapshot-game-baseline.ps1 and check-game-update.ps1 (dot-source this file).
$ErrorActionPreference = 'Stop'
$script:CoopRoot = Split-Path -Parent $PSScriptRoot
$script:AppId = '3985030'

# Files that belong to BepInEx/the mod rather than the game.
$script:NonGamePaths = @('BepInEx\', 'CoopMod\', '.claude\','winhttp.dll', 'doorstop_config.ini', '.doorstop_version', 'steam_appid.txt', 'changelog.txt')

function Test-NonGamePath([string]$rel) {
    foreach ($p in $script:NonGamePaths) {
        if ($p.EndsWith('\')) { if ($rel.StartsWith($p, [StringComparison]::OrdinalIgnoreCase)) { return $true } }
        elseif ($rel -ieq $p) { return $true }
    }
    return $false
}

# Steam buildid of $GameDir: read from appmanifest_3985030.acf, accepted only when the Steam
# install's AirDefender.dll is byte-identical to the one in $GameDir (the coop copy has no manifest).
function Get-SteamBuildId([string]$GameDir) {
    $steamapps = Split-Path -Parent (Split-Path -Parent $GameDir)
    $acf = Join-Path $steamapps "appmanifest_$script:AppId.acf"
    if (-not (Test-Path $acf)) { return $null }
    $text = Get-Content $acf -Raw
    $build = [regex]::Match($text, '"buildid"\s+"(\d+)"').Groups[1].Value
    $installdir = [regex]::Match($text, '"installdir"\s+"([^"]+)"').Groups[1].Value
    if (-not $build -or -not $installdir) { return $null }
    $steamDll = Join-Path $steamapps "common\$installdir\Air Defender_Data\Managed\AirDefender.dll"
    $dll = Join-Path $GameDir 'Air Defender_Data\Managed\AirDefender.dll'
    if (-not (Test-Path $steamDll) -or -not (Test-Path $dll)) { return $null }
    if ((Get-FileHash $steamDll).Hash -ne (Get-FileHash $dll).Hash) { return $null }
    return $build
}

function Get-GameInfo([string]$GameDir) {
    $data = Join-Path $GameDir 'Air Defender_Data'
    $dll = Join-Path $data 'Managed\AirDefender.dll'
    if (-not (Test-Path $dll)) { throw "No Air Defender_Data\Managed\AirDefender.dll in $GameDir" }
    $boot = Join-Path $data 'boot.config'
    $sw = Join-Path $data 'Plugins\Steamworks.NET.txt'
    $bep = Join-Path $GameDir 'BepInEx\core\BepInEx.dll'
    $player = Join-Path $GameDir 'UnityPlayer.dll'
    $hash = (Get-FileHash $dll).Hash
    [ordered]@{
        gameDir       = $GameDir
        buildid       = Get-SteamBuildId $GameDir
        dllSha256     = $hash
        unity         = if (Test-Path $player) { (Get-Item $player).VersionInfo.ProductVersion } else { $null }
        mono          = Test-Path (Join-Path $GameDir 'MonoBleedingEdge')
        il2cpp        = (Test-Path (Join-Path $GameDir 'GameAssembly.dll')) -or (Test-Path (Join-Path $data 'il2cpp_data'))
        bootBuildGuid = if (Test-Path $boot) { ([regex]::Match((Get-Content $boot -Raw), 'build-guid=(\w+)')).Groups[1].Value } else { $null }
        steamworks    = if (Test-Path $sw) { ((Get-Content $sw) -match '^Steam' -join '; ') } else { $null }
        bepinex       = if (Test-Path $bep) { (Get-Item $bep).VersionInfo.FileVersion } else { $null }
    }
}

# Relative path -> "size sha256" for every game file (BepInEx and the mod excluded).
function Get-FileManifest([string]$GameDir) {
    $m = [ordered]@{}
    $base = (Get-Item -LiteralPath $GameDir).FullName.TrimEnd('\') + '\'
    Get-ChildItem $GameDir -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
        $rel = $_.FullName.Substring($base.Length)
        if (-not (Test-NonGamePath $rel)) { $m[$rel] = "$($_.Length) $((Get-FileHash $_.FullName).Hash)" }
    }
    $m
}

# The count lines the mod logs at startup, from a BepInEx LogOutput.log or coop-<pid>.log.
function Get-LogCounts([string]$Log) {
    $r = [ordered]@{}
    if (-not (Test-Path $Log)) { return $r }
    foreach ($line in Get-Content $Log) {
        if ($line -match 'SimulationGate: (\d+) frame methods gated, (\d+) tick owners') { $r.frameMethods = [int]$Matches[1]; $r.tickOwners = [int]$Matches[2] }
        elseif ($line -match 'Scoring guards: (\d+)') { $r.scoringGuards = [int]$Matches[1] }
        elseif ($line -match 'Command routes: (\d+)') { $r.commandRoutes = [int]$Matches[1] }
        elseif ($line -match 'Co-op (\S+) on Unity (\S+), game (\w+)') { $r.modVersion = $Matches[1]; $r.unity = $Matches[2]; $r.fingerprint = $Matches[3] }
    }
    $r
}

function Invoke-RefDump([string[]]$ToolArgs) {
    dotnet run -c Release --project (Join-Path $script:CoopRoot 'tools\RefDump') '--' @ToolArgs
    if ($LASTEXITCODE) { throw "RefDump $($ToolArgs[0]) failed" }
}

function Get-ModDll { Join-Path $script:CoopRoot 'src\AirDefender.Coop\bin\Release\netstandard2.1\AirDefender.Coop.dll' }
function Get-ModSrc { Join-Path $script:CoopRoot 'src\AirDefender.Coop' }
