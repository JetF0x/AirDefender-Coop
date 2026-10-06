# Starts a host and one or more sandboxed client instances of the game on this PC, connected over loopback.
#   -HostScript / -ClientScript : --coop-autotest scripts (empty = drive the menus yourself)
#   -Clients                    : number of client instances (the host's MaxPlayers is set to Clients + 1)
#   -JoinStagger                : seconds between client launches, so later clients join a running session
#   -Seconds                    : stop every instance after this long and print their coop logs (0 = leave running)
param(
    [string]$HostScript = 'host',
    [string]$ClientScript = 'client',
    [int]$Clients = 1,
    [int]$JoinStagger = 3,
    [int]$Seconds = 0,
    [int]$Width = 0,
    [int]$Height = 0
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$game = Split-Path -Parent $root
if ($Clients -lt 1) { throw 'need at least one client' }
# Two instances fit side by side at 1280x720; more go in a grid of smaller windows.
if ($Width -le 0) { $Width = if ($Clients -eq 1) { 1280 } else { 960 } }
if ($Height -le 0) { $Height = if ($Clients -eq 1) { 720 } else { 540 } }

Get-Process 'Air Defender' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
& (Join-Path $PSScriptRoot 'install.ps1') -GameDir $game | Out-Null

$common = @('-screen-fullscreen', '0', '-screen-width', "$Width", '-screen-height', "$Height")
$hostArgs = @('--coop-host', '--coop-max-players', "$($Clients + 1)") + $common
if ($HostScript) { $hostArgs += @('--coop-autotest', $HostScript) }
$clientArgs = @('--coop-join', '127.0.0.1', '--coop-sandbox') + $common
if ($ClientScript) { $clientArgs += @('--coop-autotest', $ClientScript) }

$exe = Join-Path $game 'Air Defender.exe'
$procs = @()
$procs += Start-Process -FilePath $exe -WorkingDirectory $game -ArgumentList $hostArgs -PassThru
Start-Sleep 3
for ($i = 1; $i -le $Clients; $i++) {
    if ($i -gt 1) { Start-Sleep $JoinStagger }
    $procs += Start-Process -FilePath $exe -WorkingDirectory $game -ArgumentList $clientArgs -PassThru
}
Write-Host "host pid $($procs[0].Id)  client pids $(($procs | Select-Object -Skip 1 | ForEach-Object Id) -join ', ')"

# Place the windows: host first, then clients, left to right and top to bottom.
Add-Type -Namespace CoopTest -Name Win -MemberDefinition '[DllImport("user32.dll")] public static extern bool MoveWindow(System.IntPtr h, int x, int y, int w, int hgt, bool repaint);'
$cols = if ($procs.Count -le 2) { 2 } else { [int][Math]::Ceiling([Math]::Sqrt($procs.Count)) }
$deadline = (Get-Date).AddSeconds(30)
$placed = @{}
while ((Get-Date) -lt $deadline -and $placed.Count -lt $procs.Count) {
    for ($k = 0; $k -lt $procs.Count; $k++) {
        $p = $procs[$k]
        if ($placed.ContainsKey($p.Id)) { continue }
        $p.Refresh()
        if ($p.MainWindowHandle -ne [IntPtr]::Zero) {
            $x = ($k % $cols) * ($Width + 20)
            $y = 40 + [Math]::Floor($k / $cols) * ($Height + 45)
            [CoopTest.Win]::MoveWindow($p.MainWindowHandle, $x, $y, $Width + 16, $Height + 39, $true) | Out-Null
            $placed[$p.Id] = $true
        }
    }
    Start-Sleep -Milliseconds 500
}

if ($Seconds -gt 0) {
    Start-Sleep $Seconds
    foreach ($p in $procs) {
        $log = Join-Path $game "BepInEx\coop-$($p.Id).log"
        Write-Host "===== $log"
        if (Test-Path $log) { Get-Content $log }
    }
    Get-Process -Id ($procs | ForEach-Object Id) -ErrorAction SilentlyContinue | Stop-Process -Force
}
