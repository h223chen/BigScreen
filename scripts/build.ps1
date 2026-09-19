<#
.SYNOPSIS
    Builds BigScreen.dll and (optionally) copies it into the BepInEx plugins folder.
.PARAMETER Deploy
    Copy the built DLL into <BepInEx>\plugins\BigScreen afterwards. The game must be closed.
.PARAMETER Configuration
    Release (default) or Debug.
.EXAMPLE
    .\scripts\build.ps1 -Deploy
#>
[CmdletBinding()]
param(
    [switch]$Deploy,
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\common.ps1"

$repo = Split-Path $PSScriptRoot -Parent
$bep  = Get-BepInExPath
Write-Host "BepInEx: $bep"

if ($Deploy) { Assert-GameClosed }

$proj = Join-Path $repo 'src\BigScreen\BigScreen.csproj'
$deployFlag = if ($Deploy) { 'true' } else { 'false' }
& dotnet build $proj -c $Configuration -v m "/p:BepInExPath=$bep" "/p:DeployToProfile=$deployFlag"
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

if ($Deploy) {
    Write-Host ""
    Write-Host "Deployed. Launch Big Walk and watch $bep\LogOutput.log for 'BigScreen' lines." -ForegroundColor Green
}
