# Compares a new Air Defender build against the saved baseline and writes a report of what the
# co-op mod needs to change. See UPDATE.md for the full procedure.
#
#   pwsh scripts/check-game-update.ps1                      # new build = Steam install (..\..\Air Defender)
#   pwsh scripts/check-game-update.ps1 -NewGameDir <path>   # any other folder with the new game files
#   pwsh scripts/check-game-update.ps1 -Logs                # after a test run: check the mod's startup/test logs
#
# Output: _update/<new buildid>/REPORT.md plus refdiff.md, build.log, manifest-diff.txt, decomp-diff.*
param(
    [string]$NewGameDir,
    [string]$Baseline,          # _baseline/<id> folder; default = newest
    [switch]$Logs,
    [string]$LogDir,            # for -Logs; default = this repo's game copy's BepInEx folder
    [switch]$Full,              # run every step even when AirDefender.dll is unchanged
    [switch]$SkipDecompile
)
. (Join-Path $PSScriptRoot 'GameUpdate.Common.ps1')
$oldGame = Split-Path -Parent $CoopRoot

if (-not $Baseline) {
    $Baseline = Get-ChildItem (Join-Path $CoopRoot '_baseline') -Directory -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $Baseline) { throw 'No baseline found. Run scripts/snapshot-game-baseline.ps1 on the working build first.' }
}
$base = Get-Content (Join-Path $Baseline 'env.json') -Raw | ConvertFrom-Json

# ====================================================================== -Logs
if ($Logs) {
    if (-not $LogDir) { $LogDir = Join-Path $oldGame 'BepInEx' }
    $main = Join-Path $LogDir 'LogOutput.log'
    if (-not (Test-Path $main)) { throw "No $main" }
    $coop = @(Get-ChildItem $LogDir -Filter 'coop-*.log' | Sort-Object LastWriteTime -Descending)
    if ($coop.Count) { $since = $coop[0].LastWriteTime.AddMinutes(-15); $coop = @($coop | Where-Object LastWriteTime -ge $since) }
    $files = @($main) + @($coop.FullName)
    Write-Host "Checking $main and $($coop.Count) coop log(s)"

    # Expected counts for the game build these logs came from, given the current mod source.
    $gameOfLogs = Split-Path -Parent $LogDir
    $tmp = Join-Path $CoopRoot '_update\logs-refdump.txt'
    New-Item -ItemType Directory -Force (Split-Path $tmp) | Out-Null
    & (Join-Path $PSScriptRoot 'build.ps1') | Out-Null
    Invoke-RefDump @('dump', (Join-Path $gameOfLogs 'Air Defender_Data\Managed\AirDefender.dll'), (Get-ModDll), (Get-ModSrc), $tmp) | Out-Null
    $g = (Select-String $tmp -Pattern 'SimulationGate TOTAL: (\d+) frame methods gated, (\d+) tick owners').Matches[0].Groups
    $expect = [ordered]@{
        frameMethods  = [int]$g[1].Value
        tickOwners    = [int]$g[2].Value
        scoringGuards = [int](Select-String $tmp -Pattern 'TOTAL guarded: (\d+)').Matches[0].Groups[1].Value
        commandRoutes = (Select-String (Join-Path (Get-ModSrc) 'Commands\RouteList.cs') -Pattern 'MethodRouter\.(Add|Reserve)\(').Count
        fingerprint   = (Select-String $tmp -Pattern '^fingerprint (\w+)').Matches[0].Groups[1].Value
    }
    $got = Get-LogCounts $main
    $bad = 0
    Write-Host "`nStartup counts (expected from the game DLL + mod source -> logged):"
    foreach ($k in $expect.Keys) {
        $ok = "$($got[$k])" -eq "$($expect[$k])"
        if (-not $ok) { $bad++ }
        Write-Host ("  {0,-14} {1,-14} {2,-14} {3}" -f $k, $expect[$k], $got[$k], $(if ($ok) { 'ok' } else { 'MISMATCH' }))
    }

    $markers = [ordered]@{
        'Patching failed - co-op disabled'               = 'A Harmony target is missing/ambiguous: co-op is OFF. Exception text names the method.'
        'SimulationGate: (tick )?type .* not found'      = 'A SimulationGate string type no longer exists (Puppet/SimulationGate.cs).'
        'Route: '                                        = 'A routed method was not found / not patchable (Commands/RouteList.cs).'
        'Ambiguous match|Undefined target method|Null method for' = 'Harmony could not pick a target (new overload or rename).'
        'AccessTools\.(Method|DeclaredMethod|Field|Property): Could not find' = 'AccessTools lookup failed (the expected Update/LateUpdate/FixedUpdate misses are filtered out).'
        'as a structural copy'                           = 'An entity had no save-format spawn record (Replication/SpawnCatalog.cs). A few (RadarCircleMover, heli shuttles) are normal.'
        'Clone .*: (unknown component|cannot add)'       = 'Clone spawn could not rebuild a component (SpawnCatalog clone path).'
        'could not add .* from host'                     = 'StateReplicator could not AddComponent on a client.'
        'World load failed|Could not snapshot the world|refused to load the host' = 'World join broken (Bootstrap/WorldSync.cs, save format).'
        'Unsupported (named-save|embedded parked-asset) schema' = 'Save schema bumped beyond what the game build on one side accepts.'
        'game version mismatch'                          = 'Peers on different game builds.'
        '\[AUTOTEST\] FAIL'                              = 'A scripted test step failed.'
        'Partner call .* threw|Host could not run|Bad result for' = 'A routed command failed on the host.'
        '^\[(Error|Fatal)'                               = 'Any BepInEx error line.'
    }
    $frameNoise = 'AccessTools\.DeclaredMethod: Could not find method for type .* and name (Update|LateUpdate|FixedUpdate) and parameters \(\)'
    Write-Host "`nFailure markers:"
    foreach ($p in $markers.Keys) {
        $hits = @(Select-String -Path $files -Pattern $p | Where-Object { $_.Line -notmatch $frameNoise })
        if ($hits.Count) {
            if ($p -ne 'as a structural copy') { $bad++ }
            Write-Host ("  {0,4}x  {1}`n         {2}" -f $hits.Count, $p, $markers[$p])
            $hits | Select-Object -First 3 | ForEach-Object { Write-Host "         > $($_.Filename): $($_.Line.Trim())" }
        }
    }
    $ok = @(Select-String -Path $coop.FullName -Pattern '\[DIGEST\] OK' -ErrorAction SilentlyContinue).Count
    $diff = @(Select-String -Path $coop.FullName -Pattern '\[DIGEST\] DIFF' -ErrorAction SilentlyContinue).Count
    if ($ok + $diff) { Write-Host ("`nDigests: {0} OK, {1} DIFF ({2:P0} match; v0.2.0 baseline was ~80-87%, mismatches = brand-new aircraft / mid-sweep tracks)" -f $ok, $diff, ($ok / ($ok + $diff))) }
    $pass = @(Select-String -Path $coop.FullName -Pattern '\[AUTOTEST\] (PASS|OK)' -ErrorAction SilentlyContinue).Count
    if ($pass) { Write-Host "Autotest pass lines: $pass" }
    Write-Host "`n$(if ($bad) { "$bad problem(s) found - see UPDATE.md 'Log markers'" } else { 'Logs look clean.' })"
    exit ([int]($bad -gt 0))
}

# ====================================================================== build comparison
if (-not $NewGameDir) { $NewGameDir = Join-Path (Split-Path -Parent $oldGame) 'Air Defender' }
$NewGameDir = (Get-Item -LiteralPath $NewGameDir).FullName  # long path; 8.3 short names would garble manifest keys
$new = Get-GameInfo $NewGameDir
$id = if ($new.buildid) { $new.buildid } else {
    # The Steam install itself: read its own manifest.
    $acf = Join-Path (Split-Path -Parent (Split-Path -Parent $NewGameDir)) "appmanifest_$AppId.acf"
    $b = if (Test-Path $acf) { [regex]::Match((Get-Content $acf -Raw), '"buildid"\s+"(\d+)"').Groups[1].Value }
    if ($b -and (Split-Path -Leaf $NewGameDir) -eq [regex]::Match((Get-Content $acf -Raw), '"installdir"\s+"([^"]+)"').Groups[1].Value) { $b }
    else { 'sha-' + $new.dllSha256.Substring(0, 12).ToLower() }
}
$out = Join-Path $CoopRoot "_update\$id"
New-Item -ItemType Directory -Force $out | Out-Null
Write-Host "Old: baseline $($base.id) ($Baseline)`nNew: $id ($NewGameDir)`nOutput: $out"

$findings = [System.Collections.Generic.List[object]]::new()
function Add-Finding($sev, $area, $text, $look) { $findings.Add([pscustomobject]@{ Severity = $sev; Area = $area; Text = $text; Look = $look }) }

# ---------------------------------------------------------------- 1. environment
$old = $base.game
$dllSame = $old.dllSha256 -eq $new.dllSha256
if ($new.il2cpp -or -not $new.mono) { Add-Finding 'BLOCKER' 'Engine' 'The game is no longer a Mono build (GameAssembly.dll / il2cpp_data present or MonoBleedingEdge missing). BepInEx 5 cannot load; the mod would need BepInEx 6 IL2CPP and a rewrite of every reflection/Harmony path.' 'UPDATE.md: Environment' }
if ($old.unity -ne $new.unity) { Add-Finding 'HIGH' 'Engine' "Unity changed: $($old.unity) -> $($new.unity). Check BepInEx 5.4.23.5 still loads (LogOutput.log 'Chainloader started'); a newer BepInEx may be needed (update .deps zip, package.ps1, README)." 'UPDATE.md: Environment' }
if ($old.steamworks -ne $new.steamworks) { Add-Finding 'HIGH' 'Steam' "Steamworks.NET changed: '$($old.steamworks)' -> '$($new.steamworks)'. Rebuild is mandatory (Net/SteamTransport.cs marshals SteamNetworkingMessage_t; Session/SteamLobbyService.cs callbacks)." 'Net/SteamTransport.cs, Session/SteamLobbyService.cs' }
if ($old.bootBuildGuid -ne $new.bootBuildGuid) { Add-Finding 'INFO' 'Build' "boot.config build-guid changed ($($old.bootBuildGuid) -> $($new.bootBuildGuid)): this is a new Unity player build." '' }

Write-Host 'Hashing new game files...'
$oldManifest = Get-Content (Join-Path $Baseline 'manifest.json') -Raw | ConvertFrom-Json -AsHashtable
# Re-apply the exclusions in case the baseline was taken with an older exclusion list.
foreach ($k in @($oldManifest.Keys)) { if (Test-NonGamePath $k) { $oldManifest.Remove($k) } }
$newManifest = Get-FileManifest $NewGameDir
$added = @($newManifest.Keys | Where-Object { -not $oldManifest.ContainsKey($_) } | Sort-Object)
$removed = @($oldManifest.Keys | Where-Object { -not $newManifest.Contains($_) } | Sort-Object)
$changed = @($newManifest.Keys | Where-Object { $oldManifest.ContainsKey($_) -and $oldManifest[$_] -ne $newManifest[$_] } | Sort-Object)
@('# Game files: + added, - removed, * changed (BepInEx/mod files excluded)') +
    ($added | ForEach-Object { "+ $_" }) + ($removed | ForEach-Object { "- $_" }) + ($changed | ForEach-Object { "* $_" }) |
    Set-Content (Join-Path $out 'manifest-diff.txt')
$managedTouched = @($added + $removed + $changed | Where-Object { $_ -like 'Air Defender_Data\Managed\*' })
$gameAsms = 'AirDefender.dll', 'AirDefender.Utils.dll', 'com.rlabrecque.steamworks.net.dll', 'Unity.InputSystem.dll', 'UnityEngine.CoreModule.dll', 'UnityEngine.IMGUIModule.dll'
foreach ($f in $managedTouched) {
    $n = Split-Path -Leaf $f
    if ($n -in $gameAsms -and $n -ne 'AirDefender.dll') { Add-Finding 'MEDIUM' 'Assemblies' "$f changed; the mod compiles against it (AirDefender.Coop.csproj). The compile check below covers it." 'AirDefender.Coop.csproj' }
}
if ($managedTouched.Count) { Add-Finding 'INFO' 'Assemblies' "$($managedTouched.Count) file(s) in Managed added/removed/changed: $((($managedTouched | ForEach-Object { Split-Path -Leaf $_ }) | Select-Object -First 12) -join ', ')$(if ($managedTouched.Count -gt 12) { ', ...' })" 'manifest-diff.txt' }
if ($dllSame -and ($added.Count + $removed.Count + $changed.Count)) {
    Add-Finding 'HIGH' 'Handshake' "AirDefender.dll is unchanged but $($added.Count + $removed.Count + $changed.Count) other game file(s) changed (assets/data). The handshake fingerprint (AirDefender.dll MVID) stays $($base.fingerprint), so a player still on the old build can join and desync. Make sure every player updates; consider bumping Plugin.Version." 'Session/CoopSession.cs (GameFingerprint)'
}

# ---------------------------------------------------------------- 2. references (RefDump old vs new, both against the current mod build)
$runAll = $Full -or -not $dllSame
& (Join-Path $PSScriptRoot 'build.ps1') | Out-Null
$oldDump = Join-Path $out 'refdump-old.txt'; $newDump = Join-Path $out 'refdump-new.txt'; $refDiff = Join-Path $out 'refdiff.md'
Invoke-RefDump @('dump', (Join-Path $Baseline 'Managed\AirDefender.dll'), (Get-ModDll), (Get-ModSrc), $oldDump)
Invoke-RefDump @('dump', (Join-Path $NewGameDir 'Air Defender_Data\Managed\AirDefender.dll'), (Get-ModDll), (Get-ModSrc), $newDump)
$diffSummary = Invoke-RefDump @('diff', $oldDump, $newDump, $refDiff) | Select-Object -Last 1
Write-Host "RefDump: $diffSummary"
$sectionRows = Select-String $refDiff -Pattern '^\| (\d) \| \[([\w-]+)\]\(#[\w-]+\) \| (\d+) \| (\d+) \| (.*) \|$' | ForEach-Object {
    $g = $_.Matches[0].Groups
    [pscustomobject]@{ Prio = [int]$g[1].Value; Section = $g[2].Value; Removed = [int]$g[3].Value; Added = [int]$g[4].Value; Look = $g[5].Value }
}
$alertBlock = [regex]::Match((Get-Content $refDiff -Raw), '## Alerts in the new build\s+```\r?\n(.*?)```', 'Singleline').Groups[1].Value.Trim()
foreach ($a in ($alertBlock -split "`r?`n" | Where-Object { $_ })) { Add-Finding 'HIGH' 'Reference' $a 'refdiff.md' }
foreach ($r in $sectionRows | Where-Object { $_.Removed + $_.Added -gt 0 -and $_.Prio -le 3 }) {
    $sev = if ($r.Prio -le 1) { 'HIGH' } elseif ($r.Prio -eq 2) { 'MEDIUM' } else { 'LOW' }
    Add-Finding $sev "refdump:$($r.Section)" "$($r.Removed) line(s) removed / $($r.Added) added" $r.Look
}

# ---------------------------------------------------------------- 3. compile against the new game
$compileErrors = @()
if ($runAll) {
    Write-Host 'Compiling the mod against the new game...'
    $buildLog = Join-Path $out 'build.log'
    # Child process so a failed build still leaves its full compiler output in build.log.
    pwsh -NoProfile -File (Join-Path $PSScriptRoot 'build.ps1') -GameDir $NewGameDir -OutLib (Join-Path $out 'lib\AirDefender.dll') -OutDir (Join-Path $out 'build') *> $buildLog
    $compileErrors = @(Select-String $buildLog -Pattern '^\s*(.+?)\((\d+),\d+\): error (CS\d+): (.*?)( \[.*\])?$' | ForEach-Object {
        $g = $_.Matches[0].Groups
        '{0}:{1} {2} {3}' -f [IO.Path]::GetRelativePath($CoopRoot, $g[1].Value), $g[2].Value, $g[3].Value, $g[4].Value
    } | Sort-Object -Unique)
    if ($compileErrors.Count) { Add-Finding 'HIGH' 'Compile' "$($compileErrors.Count) compile error(s) against the new game - fix these first." 'build.log' }
    elseif (-not (Select-String $buildLog -Pattern 'Build succeeded' -Quiet)) { Add-Finding 'HIGH' 'Compile' 'Build did not succeed (no CS errors parsed; read build.log).' 'build.log' }
    else {
        Add-Finding 'INFO' 'Compile' 'The mod builds against the new game (0 errors).' 'build.log'
        # The old mod DLL binds the old signature; a clean rebuild re-binds it (e.g. a new optional parameter).
        foreach ($f in $findings | Where-Object { $_.Area -eq 'Reference' -and $_.Text -like 'MISSING(signature changed)*' }) {
            $f.Severity = 'MEDIUM'
            $f.Text += ' - fixed by the rebuild; check the call still means the same'
        }
    }
}

# ---------------------------------------------------------------- 4. decompile + diff
$decompStat = @()
$touchedChanged = @()
if ($runAll -and -not $SkipDecompile) {
    if (-not (Get-Command ilspycmd -ErrorAction SilentlyContinue)) {
        Add-Finding 'INFO' 'Decompile' 'ilspycmd not found; install with: dotnet tool install -g ilspycmd' ''
    } else {
        $oldDec = if ($base.decompile -and (Test-Path $base.decompile)) { $base.decompile } else { Join-Path $CoopRoot "_decomp\AirDefender-$($base.id)" }
        $newDec = Join-Path $CoopRoot "_decomp\AirDefender-$id"
        foreach ($d in @(@($oldDec, (Join-Path $Baseline 'Managed')), @($newDec, (Join-Path $NewGameDir 'Air Defender_Data\Managed')))) {
            if (Test-Path $d[0]) { continue }
            Write-Host "Decompiling to $($d[0])..."
            ilspycmd -p -o $d[0] -r $d[1] (Join-Path $d[1] 'AirDefender.dll') | Out-Null
            if ($LASTEXITCODE) { throw "ilspycmd failed for $($d[0])" }
        }
        Push-Location (Join-Path $CoopRoot '_decomp')
        try {
            $a = Split-Path -Leaf $oldDec; $b = Split-Path -Leaf $newDec
            $decompStat = @(git -c core.autocrlf=false diff --no-index --no-color --stat=220 -- $a $b 2>$null)
            $decompStat | Set-Content (Join-Path $out 'decomp-diff-stat.txt')
            git -c core.autocrlf=false diff --no-index --no-color -- $a $b 2>$null | Set-Content (Join-Path $out 'decomp-diff.patch')
            # The generated .csproj embeds the reference paths, so it always differs; ignore it.
            $names = @(git -c core.autocrlf=false diff --no-index --name-only -- $a $b 2>$null | Where-Object { $_ -notlike '*.csproj' })
        } finally { Pop-Location }
        # Decompile files of game types the mod touches (types named in the new refdump's compile/named/surface sections).
        $touched = @{}
        foreach ($line in Get-Content $newDump) {
            # Surface header lines: "<Type>[+Nested] class|struct|enum|interface base=..."; nested types live in the outer type's file.
            if ($line -match '^(AirDefender\.[\w.]+?)(\+[\w`]+)* (class|struct|enum|interface) base=') {
                $typeName = $Matches[1]; $ns = $typeName.Substring(0, $typeName.LastIndexOf('.')); $tn = $typeName.Substring($typeName.LastIndexOf('.') + 1)
                $touched["$ns\$tn.cs"] = $true
            }
        }
        $touchedChanged = @($names | ForEach-Object { ($_ -replace '/', '\') -replace '^[^\\]+\\', '' } | Sort-Object -Unique | Where-Object { $touched.ContainsKey($_) })
        if ($names.Count) { Add-Finding 'INFO' 'Decompile' "$(@($names | Sort-Object -Unique).Count) decompiled file(s) differ; $($touchedChanged.Count) of them are game types the mod touches (listed below)." 'decomp-diff.patch' }
    }
}

# ---------------------------------------------------------------- 5. report
$order = @{ BLOCKER = 0; HIGH = 1; MEDIUM = 2; LOW = 3; INFO = 4 }
$sorted = $findings | Sort-Object { $order[$_.Severity] }, Area
$verdict =
    if ($findings | Where-Object Severity -eq 'BLOCKER') { 'BLOCKER - the mod cannot work on this build without major changes' }
    elseif ($compileErrors.Count -or ($findings | Where-Object { $_.Severity -eq 'HIGH' -and $_.Area -in 'Reference', 'Compile' })) { 'CODE FIXES NEEDED' }
    elseif ($findings | Where-Object { $_.Severity -in 'HIGH', 'MEDIUM', 'LOW' }) { 'REVIEW NEEDED - builds, but referenced game code changed' }
    elseif (-not $dllSame) { 'REBUILD ONLY - game code changed but nothing the mod depends on' }
    elseif ($findings.Count) { 'NO CODE CHANGE - see notes' }
    else { 'NO CHANGE - identical game build' }

$r = [System.Text.StringBuilder]::new()
[void]$r.AppendLine("# Game update report: $($base.id) -> $id").AppendLine()
[void]$r.AppendLine("**Verdict: $verdict**").AppendLine()
[void]$r.AppendLine("Generated $(Get-Date -Format 'yyyy-MM-dd HH:mm') by scripts/check-game-update.ps1. Procedure and reference inventory: [UPDATE.md](../../UPDATE.md).").AppendLine()
[void]$r.AppendLine('| | Old (baseline) | New |').AppendLine('|---|---|---|')
[void]$r.AppendLine("| Build id | $($base.id) | $id |")
[void]$r.AppendLine("| AirDefender.dll | ``$($old.dllSha256.Substring(0,16))`` | ``$($new.dllSha256.Substring(0,16))``$(if ($dllSame) { ' (same)' }) |")
$newFp = (Select-String $newDump -Pattern '^fingerprint (\w+)').Matches[0].Groups[1].Value
[void]$r.AppendLine("| Fingerprint (MVID) | $($base.fingerprint) | $newFp |")
[void]$r.AppendLine("| Unity | $($old.unity) | $($new.unity) |")
[void]$r.AppendLine("| Mono / IL2CPP | $($old.mono) / $($old.il2cpp) | $($new.mono) / $($new.il2cpp) |")
[void]$r.AppendLine("| Steamworks.NET | $(($old.steamworks -split ';')[1]) | $(($new.steamworks -split ';')[1]) |")
$sg = { param($f) (Select-String $f -Pattern 'SimulationGate TOTAL: (\d+) frame methods gated, (\d+) tick owners').Matches[0].Groups }
$og = & $sg $oldDump; $ng = & $sg $newDump
$guards = { param($f) (Select-String $f -Pattern 'TOTAL guarded: (\d+)').Matches[0].Groups[1].Value }
[void]$r.AppendLine("| SimulationGate (frame/tick) | $($og[1].Value)/$($og[2].Value) | $($ng[1].Value)/$($ng[2].Value) |")
[void]$r.AppendLine("| Scoring guards | $(& $guards $oldDump) | $(& $guards $newDump) |")
[void]$r.AppendLine("| Game files +/-/changed | | $($added.Count) / $($removed.Count) / $($changed.Count) ([manifest-diff.txt](manifest-diff.txt)) |").AppendLine()

[void]$r.AppendLine('## Findings').AppendLine()
if ($sorted) {
    [void]$r.AppendLine('| Severity | Area | Finding | Look at |').AppendLine('|---|---|---|---|')
    foreach ($f in $sorted) { [void]$r.AppendLine("| $($f.Severity) | $($f.Area) | $($f.Text -replace '\|', '\|') | $($f.Look) |") }
} else { [void]$r.AppendLine('None.') }
[void]$r.AppendLine()

if ($compileErrors.Count) {
    [void]$r.AppendLine('## Compile errors against the new game').AppendLine().AppendLine('```')
    $compileErrors | ForEach-Object { [void]$r.AppendLine($_) }
    [void]$r.AppendLine('```').AppendLine()
}

[void]$r.AppendLine('## Reference diff').AppendLine()
[void]$r.AppendLine("$diffSummary. Full detail: [refdiff.md](refdiff.md) (raw dumps: refdump-old.txt / refdump-new.txt).").AppendLine()
if ($sectionRows) {
    [void]$r.AppendLine('| Prio | Section | Removed | Added | Look at |').AppendLine('|---|---|---|---|---|')
    foreach ($s in $sectionRows) { [void]$r.AppendLine("| $($s.Prio) | $($s.Section) | $($s.Removed) | $($s.Added) | $($s.Look) |") }
    [void]$r.AppendLine()
}

if ($touchedChanged.Count) {
    [void]$r.AppendLine('## Changed decompiled files the mod depends on').AppendLine()
    [void]$r.AppendLine("Diff: ``git diff --no-index _decomp/$(Split-Path -Leaf $oldDec)/<file> _decomp/AirDefender-$id/<file>`` (all diffs: [decomp-diff.patch](decomp-diff.patch)).").AppendLine()
    $touchedChanged | ForEach-Object { [void]$r.AppendLine("- ``$_``") }
    [void]$r.AppendLine()
}

[void]$r.AppendLine('## Next steps').AppendLine()
[void]$r.AppendLine('1. Fix compile errors (if any), then work through HIGH findings and the prio 1-2 refdiff sections, using UPDATE.md to map each to mod code.')
[void]$r.AppendLine('2. Review prio 3 sections: new **NOT gated** sim-candidates, new **DROP** field-schema lines on replicated state, NotifyHit callers, new file/cloud writers.')
[void]$r.AppendLine('3. Copy the new game files into the coop copy (UPDATE.md step 4), build, install, run-local-test with 1 and 3 clients, then `pwsh scripts/check-game-update.ps1 -Logs`.')
[void]$r.AppendLine('4. Bump versions, package, then `pwsh scripts/snapshot-game-baseline.ps1` to make this build the new baseline.')
$report = Join-Path $out 'REPORT.md'
$r.ToString() | Set-Content $report
Write-Host "`nVerdict: $verdict`nReport: $report"
