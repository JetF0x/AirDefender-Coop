# Starts a host and a sandboxed client instance of the game on this PC, connected over loopback.
#   -HostScript / -ClientScript : --coop-autotest scripts (empty = drive the menus yourself)
#   -Seconds                    : stop both instances after this long and print their coop logs (0 = leave running)
param(
    [string]$HostScript = 'host',
    [string]$ClientScript = 'client',
    [int]$Seconds = 0,
    [int]$Width = 1280,
    [int]$Height = 720
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$game = Split-Path -Parent $root
Get-Process 'Air Defender' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
& (Join-Path $PSScriptRoot 'install.ps1') -GameDir $game | Out-Null

$common = @('-screen-fullscreen', '0', '-screen-width', "$Width", '-screen-height', "$Height")
$hostArgs = @('--coop-host') + $common
if ($HostScript) { $hostArgs += @('--coop-autotest', $HostScript) }
$clientArgs = @('--coop-join', '127.0.0.1', '--coop-sandbox') + $common
if ($ClientScript) { $clientArgs += @('--coop-autotest', $ClientScript) }

$exe = Join-Path $game 'Air Defender.exe'
$h = Start-Process -FilePath $exe -WorkingDirectory $game -ArgumentList $hostArgs -PassThru
Start-Sleep 3
$c = Start-Process -FilePath $exe -WorkingDirectory $game -ArgumentList $clientArgs -PassThru
Write-Host "host pid $($h.Id)  client pid $($c.Id)"

# Place the windows side by side: host left, client right.
Add-Type -Namespace CoopTest -Name Win -MemberDefinition '[DllImport("user32.dll")] public static extern bool MoveWindow(System.IntPtr h, int x, int y, int w, int hgt, bool repaint);'
$deadline = (Get-Date).AddSeconds(30)
$placed = @{}
while ((Get-Date) -lt $deadline -and $placed.Count -lt 2) {
    foreach ($p in @($h, $c)) {
        if ($placed.ContainsKey($p.Id)) { continue }
        $p.Refresh()
        if ($p.MainWindowHandle -ne [IntPtr]::Zero) {
            $x = if ($p.Id -eq $h.Id) { 0 } else { $Width + 20 }
            [CoopTest.Win]::MoveWindow($p.MainWindowHandle, $x, 40, $Width + 16, $Height + 39, $true) | Out-Null
            $placed[$p.Id] = $true
        }
    }
    Start-Sleep -Milliseconds 500
}

if ($Seconds -gt 0) {
    Start-Sleep $Seconds
    foreach ($p in @($h, $c)) {
        $log = Join-Path $game "BepInEx\coop-$($p.Id).log"
        Write-Host "===== $log"
        if (Test-Path $log) { Get-Content $log }
    }
    Get-Process -Id $h.Id, $c.Id -ErrorAction SilentlyContinue | Stop-Process -Force
}
