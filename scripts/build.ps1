# Builds the co-op plugin. Regenerates the publicized compile reference when the game DLL changes.
# -GameDir/-OutLib/-OutDir build against another game install (e.g. the freshly updated Steam copy)
# without touching lib/ or the normal bin/obj folders; scripts/check-game-update.ps1 uses this.
param(
    [string]$Configuration = 'Release',
    [string]$GameDir,
    [string]$OutLib,
    [string]$OutDir
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $GameDir) { $GameDir = Split-Path -Parent $root }
$GameDir = (Resolve-Path $GameDir).Path
if (-not $OutLib) { $OutLib = Join-Path $root 'lib\AirDefender.dll' }
$OutLib = [IO.Path]::GetFullPath($OutLib)
$src = Join-Path $GameDir 'Air Defender_Data\Managed\AirDefender.dll'

# Staleness is decided by the source DLL's hash, not timestamps: copied game files keep their
# original write times, which made a timestamp check silently skip publicizing.
$stamp = "$OutLib.source.sha256"
$hash = (Get-FileHash $src -Algorithm SHA256).Hash
if (-not (Test-Path $OutLib) -or -not (Test-Path $stamp) -or (Get-Content $stamp -Raw).Trim() -ne $hash) {
    dotnet run -c Release --project (Join-Path $root 'tools\Publicizer') -- $src $OutLib
    if ($LASTEXITCODE) { throw 'publicizer failed' }
    Set-Content -NoNewline -Path $stamp -Value $hash
}

$buildArgs = @((Join-Path $root 'src\AirDefender.Coop\AirDefender.Coop.csproj'), '-c', $Configuration,
    "-p:GameDir=$GameDir", "-p:GameLib=$OutLib")
# A freshly updated install may not have BepInEx; compile against this repo's game copy's loader then.
if (-not (Test-Path (Join-Path $GameDir 'BepInEx\core\BepInEx.dll'))) {
    $buildArgs += "-p:BepInExCore=$(Join-Path (Split-Path -Parent $root) 'BepInEx\core')"
}
if ($OutDir) {
    $OutDir = [IO.Path]::GetFullPath($OutDir)
    # Separate obj so an alternate build never leaves its output for the normal incremental build to reuse.
    $buildArgs += @("-p:OutDir=$(Join-Path $OutDir 'bin')\", "-p:IntermediateOutputPath=$(Join-Path $OutDir 'obj')\")
}
dotnet build @buildArgs
if ($LASTEXITCODE) { throw 'build failed' }
