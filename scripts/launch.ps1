<#
.SYNOPSIS
    Builds, deploys and launches Big Walk modded through Gale.
.DESCRIPTION
    One command for the edit -> test loop. Gale can start a profile from the command line,
    so the game always comes up with the mod loader attached; launching from Steam does not.

    The game locks the plugin DLL while it runs, so this refuses to deploy over a running
    game rather than half-updating the profile.
.PARAMETER NoBuild
    Launch whatever is already deployed, without building.
.PARAMETER NoWait
    Return as soon as Gale is told to launch, instead of waiting for the loader to come up.
.PARAMETER KeepGale
    Leave a running Gale window alone and stop, instead of closing it. Gale is single-instance,
    so an open window makes the command line a no-op.
.PARAMETER ProfileName
    Gale profile to launch. Defaults to GALE_PROFILE from .env, else "Default".
.PARAMETER TimeoutSeconds
    How long to wait for the game to report itself modded. Default 300.
.EXAMPLE
    .\scripts\launch.ps1
    .\scripts\launch.ps1 -NoBuild
#>
[CmdletBinding()]
param(
    [switch]$NoBuild,
    [switch]$NoWait,
    [switch]$KeepGale,
    [string]$ProfileName,
    [int]$TimeoutSeconds = 300
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\common.ps1"

if (-not $ProfileName) { $ProfileName = Get-GaleProfileName }
$gale = Get-GalePath
$slug = Get-GaleGameSlug

if (-not $NoBuild) {
    Assert-GameClosed
    Write-Host "Building and deploying..." -ForegroundColor Cyan
    # build.ps1 throws on a failed build, so reaching the next line means it worked.
    # Do not test $LASTEXITCODE here: under Set-StrictMode it is undefined until a
    # native executable has run in this session.
    & "$PSScriptRoot\build.ps1" -Deploy
}

if (Get-Process -Name 'Big Walk' -ErrorAction SilentlyContinue) {
    Write-Host "Big Walk is already running; not launching a second copy." -ForegroundColor Yellow
    return
}

# Gale is single-instance: with its window already open, a command-line invocation hands the
# arguments to the running copy and nothing launches. Close it first.
$running = Get-Process -Name 'gale' -ErrorAction SilentlyContinue
if ($running) {
    if ($KeepGale) {
        Write-Host "Gale is already running. It will swallow these arguments and nothing will launch." -ForegroundColor Yellow
        Write-Host "Close Gale, or drop -KeepGale to let this script close it." -ForegroundColor Yellow
        return
    }
    Write-Host "Closing the running Gale instance (it blocks the command line)..." -ForegroundColor DarkGray
    $running | Stop-Process -Force
    Start-Sleep -Seconds 2
}

Write-Host "Launching $slug / $ProfileName through Gale..." -ForegroundColor Cyan
Write-Host "  $gale --game $slug --profile $ProfileName --launch --no-gui" -ForegroundColor DarkGray
Start-Process -FilePath $gale -ArgumentList @('--game', $slug, '--profile', $ProfileName, '--launch', '--no-gui')

if ($NoWait) { return }

# Gale starts the game, but Big Walk opens on a mic-check screen that needs a click before
# it reaches the main menu. Nothing here can get past that. Once you do, Dev.AutoHost (if
# enabled) takes it into the session without further input.
Write-Host "Click past the mic-check screen; the mod hosts from the main menu." -ForegroundColor DarkGray
& "$PSScriptRoot\game-state.ps1" -WaitFor Started -TimeoutSeconds $TimeoutSeconds
