# Records the current game build as the baseline that check-game-update.ps1 compares a new build against.
# Run it while the game is on the build the mod works with (i.e. before an update, and again after the
# mod has been fixed for a new build). Writes _baseline/<buildid>/ (git-ignored; contains game files):
#   Managed/        copy of Air Defender_Data\Managed (old DLLs for RefDump and for re-decompiling)
#   manifest.json   size + SHA256 of every game file (BepInEx and the mod excluded)
#   env.json        build id, Unity/Steamworks/BepInEx versions, fingerprint, startup log counts
#   refdump.txt     tools/RefDump output for this build (informational; the check regenerates it)
param([string]$GameDir, [switch]$Force)
. (Join-Path $PSScriptRoot 'GameUpdate.Common.ps1')
if (-not $GameDir) { $GameDir = Split-Path -Parent $CoopRoot }
$GameDir = (Get-Item -LiteralPath $GameDir).FullName

$info = Get-GameInfo $GameDir
$id = if ($info.buildid) { $info.buildid } else { 'sha-' + $info.dllSha256.Substring(0, 12).ToLower() }
$out = Join-Path $CoopRoot "_baseline\$id"
if ((Test-Path $out) -and -not $Force) { throw "$out already exists (use -Force to overwrite)" }
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null
Write-Host "Baseline $id from $GameDir"

Copy-Item (Join-Path $GameDir 'Air Defender_Data\Managed') (Join-Path $out 'Managed') -Recurse
Write-Host 'Hashing game files...'
Get-FileManifest $GameDir | ConvertTo-Json | Set-Content (Join-Path $out 'manifest.json')

# Startup counts the mod logged on this build. The coop copy's log is preferred; the Steam install
# runs the same plugin, so its log is a fallback when it is on the same build.
$counts = [ordered]@{}
$logs = @((Join-Path $GameDir 'BepInEx\LogOutput.log'))
$steamapps = Split-Path -Parent (Split-Path -Parent $GameDir)
$steamLog = Join-Path $steamapps 'common\Air Defender\BepInEx\LogOutput.log'
if ($info.buildid -and (Test-Path $steamLog)) { $logs += $steamLog }
foreach ($l in $logs) {
    $c = Get-LogCounts $l
    if ($c.Count -and $c.fingerprint) { $counts = $c; $counts.source = $l; break }
}

& (Join-Path $PSScriptRoot 'build.ps1') | Out-Null
$dump = Join-Path $out 'refdump.txt'
Invoke-RefDump @('dump', (Join-Path $out 'Managed\AirDefender.dll'), (Get-ModDll), (Get-ModSrc), $dump)
$fp = (Select-String $dump -Pattern '^fingerprint (\w+)').Matches[0].Groups[1].Value
$gate = (Select-String $dump -Pattern 'SimulationGate TOTAL: (\d+) frame methods gated, (\d+) tick owners').Matches[0].Groups
$guards = (Select-String $dump -Pattern 'TOTAL guarded: (\d+)').Matches[0].Groups[1].Value

# The decompile of this build: check-game-update leaves it in _decomp\AirDefender-<id>. The plain
# _decomp\AirDefender folder is the very first build's (25378621). With neither, the check
# decompiles the baseline's Managed copy itself.
$decomp = Join-Path $CoopRoot "_decomp\AirDefender-$id"
if (-not (Test-Path $decomp) -and $id -eq '25378621') { $decomp = Join-Path $CoopRoot '_decomp\AirDefender' }
$envInfo = [ordered]@{
    id            = $id
    snapshotUtc   = (Get-Date).ToUniversalTime().ToString('o')
    game          = $info
    fingerprint   = $fp
    expected      = [ordered]@{ frameMethods = [int]$gate[1].Value; tickOwners = [int]$gate[2].Value; scoringGuards = [int]$guards }
    logCounts     = $counts
    modVersion    = (Get-Item (Get-ModDll)).VersionInfo.ProductVersion -replace '\+.*$', ''
    decompile     = if (Test-Path $decomp) { $decomp } else { $null }
}
$envInfo | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $out 'env.json')
Write-Host "Fingerprint $fp, gated $($gate[1].Value)/$($gate[2].Value), scoring guards $guards, log counts: $(($counts.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ', ')"
Write-Host "Baseline written to $out"
