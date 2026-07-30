<#
.SYNOPSIS
    Build, deploy, and optionally launch UAD Mint Chip Plus.

.DESCRIPTION
    Wraps `dotnet build` for MintChipPlus.sln. By default it builds Release
    and deploys the DLL into the game's Mods folder (the .csproj DeployOnBuild
    target kills a running game, then copies MintChipPlus.dll). Use -Run to
    also launch the game via Steam after a successful deploy.

    The build needs UAD_PATH pointing at the game install (its references live
    under MelonLoader/). This script resolves it from -UadPath, then the
    existing $env:UAD_PATH, then common Steam library locations.

.EXAMPLE
    .\build.ps1                 # build + deploy into Mods
    .\build.ps1 -Run            # build + deploy + launch the game
    .\build.ps1 -NoDeploy       # build only, leave DLL in bin\
    .\build.ps1 -Config Debug   # Debug build + deploy
    .\build.ps1 -UadPath 'D:\Games\UAD\'
#>
[CmdletBinding()]
param(
    [string]$Config = 'Release',
    [switch]$NoDeploy,
    [switch]$Run,
    [string]$UadPath
)

$ErrorActionPreference = 'Stop'
$scriptDir = $PSScriptRoot
$solution = Join-Path $scriptDir 'MintChipPlus.sln'

# Resolve the game install path: explicit param > env var > known Steam locations.
function Resolve-UadPath {
    param([string]$Explicit)

    $candidates = @()
    if ($Explicit)        { $candidates += $Explicit }
    if ($env:UAD_PATH)    { $candidates += $env:UAD_PATH }
    $candidates += @(
        'C:\Program Files (x86)\Steam\steamapps\common\Ultimate Admiral Dreadnoughts',
        'C:\SteamLibrary\steamapps\common\Ultimate Admiral Dreadnoughts',
        'D:\SteamLibrary\steamapps\common\Ultimate Admiral Dreadnoughts',
        'E:\SteamLibrary\steamapps\common\Ultimate Admiral Dreadnoughts'
    )

    foreach ($c in $candidates) {
        if ($c -and (Test-Path -LiteralPath $c)) {
            # MSBuild concatenates $(UAD_PATH) + 'MelonLoader/...', so it must end in a separator.
            if ($c -notmatch '[\\/]$') { $c += '\' }
            return $c
        }
    }
    return $null
}

$resolved = Resolve-UadPath -Explicit $UadPath
if (-not $resolved) {
    Write-Error "Could not find the UAD install. Pass -UadPath '<game folder>' or set `$env:UAD_PATH."
    exit 1
}
$env:UAD_PATH = $resolved
Write-Host "UAD_PATH = $env:UAD_PATH" -ForegroundColor Cyan

$buildArgs = @('build', $solution, '-c', $Config)
# -Run implies a deploy (you can't launch the game on a stale DLL).
if (-not $NoDeploy -or $Run) { $buildArgs += '/p:DeployOnBuild=true' }
if ($Run)                    { $buildArgs += '/p:LaunchGame=true' }

Write-Host "dotnet $($buildArgs -join ' ')" -ForegroundColor Cyan
& dotnet @buildArgs
$code = $LASTEXITCODE
if ($code -ne 0) {
    Write-Error "Build failed (exit $code)."
    exit $code
}

$dll = Join-Path $scriptDir "MintChipPlus\bin\$Config\net6.0\MintChipPlus.dll"
Write-Host "Build succeeded: $dll" -ForegroundColor Green
if (-not $NoDeploy) { Write-Host "Deployed to: $($env:UAD_PATH)Mods\MintChipPlus.dll" -ForegroundColor Green }
if ($Run)           { Write-Host "Launching game via Steam..." -ForegroundColor Green }
