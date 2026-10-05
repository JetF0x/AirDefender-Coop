# Builds the co-op plugin. Regenerates the publicized compile reference when the game DLL changes.
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$game = Split-Path -Parent $root
$src = Join-Path $game 'Air Defender_Data\Managed\AirDefender.dll'
$pub = Join-Path $root 'lib\AirDefender.dll'
if (-not (Test-Path $pub) -or (Get-Item $src).LastWriteTimeUtc -gt (Get-Item $pub).LastWriteTimeUtc) {
    dotnet run -c Release --project (Join-Path $root 'tools\Publicizer') -- $src $pub
    if ($LASTEXITCODE) { throw 'publicizer failed' }
}
dotnet build (Join-Path $root 'src\AirDefender.Coop\AirDefender.Coop.csproj') -c $Configuration
if ($LASTEXITCODE) { throw 'build failed' }
