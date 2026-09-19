<#
.SYNOPSIS
    Zips the BepInEx core + interop assemblies the plugin compiles against, so GitHub
    Actions can build without a copy of the game. Re-run whenever Big Walk updates
    (interop assemblies are regenerated per game version) and upload the zip as the
    `build-refs.zip` asset on the `build-refs` GitHub release:

        gh release create build-refs --title "Build references" --notes "Interop refs for CI" 
        gh release upload build-refs --clobber dist\build-refs.zip

    The zip contains only proxy/stub assemblies (no game logic), which is what other
    Big Walk mod repos do as well - but it is still derived from the game, so keep it
    as a release asset rather than committing it to git.
#>
[CmdletBinding()]
param([string]$Out = "$PSScriptRoot\..\dist")
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\common.ps1"
Add-Type -AssemblyName System.IO.Compression.FileSystem

$bep = Get-BepInExPath
$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("bigscreen_refs_" + [System.IO.Path]::GetRandomFileName())
try {
    foreach ($sub in @('core', 'interop')) {
        New-Item -ItemType Directory -Force (Join-Path $staging $sub) | Out-Null
        Copy-Item (Join-Path $bep "$sub\*.dll") (Join-Path $staging "$sub\") -Force
    }
    New-Item -ItemType Directory -Force $Out | Out-Null
    $zip = Join-Path (Resolve-Path $Out) 'build-refs.zip'
    if (Test-Path $zip) { Remove-Item $zip -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($staging, $zip)
    $n = (Get-ChildItem (Join-Path $staging 'interop') -File).Count
    Write-Host "Wrote $zip ($n interop assemblies)"
} finally {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}
