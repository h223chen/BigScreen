<#
.SYNOPSIS
    Builds a Thunderstore-ready zip under dist\.
.DESCRIPTION
    A Thunderstore package is a flat zip with manifest.json, icon.png (exactly 256x256),
    README.md and the payload laid out as it lands in the profile: BepInEx\plugins\BigScreen\.
.PARAMETER Configuration
    Release (default) or Debug.
#>
[CmdletBinding()]
param([string]$Configuration = 'Release')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\common.ps1"

$repo = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $repo 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null

& "$PSScriptRoot\build.ps1" -Configuration $Configuration

[xml]$proj = Get-Content (Join-Path $repo 'src\BigScreen\BigScreen.csproj')
$version = ($proj.Project.PropertyGroup.Version | Where-Object { $_ }) -as [string]
$desc    = ($proj.Project.PropertyGroup.Description | Where-Object { $_ }) -as [string]
if ($desc.Length -gt 250) { $desc = $desc.Substring(0, 250) }

$stage = Join-Path $dist 'stage'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$payload = Join-Path $stage 'BepInEx\plugins\BigScreen'
New-Item -ItemType Directory -Force $payload | Out-Null

$dll = Join-Path $repo "src\BigScreen\bin\$Configuration\BigScreen.dll"
if (-not (Test-Path $dll)) { throw "Built DLL missing: $dll" }
Copy-Item $dll $payload -Force

$manifest = Get-Content (Join-Path $repo 'thunderstore\manifest.json') -Raw | ConvertFrom-Json
$manifest.version_number = $version
$manifest.description = $desc
$manifest | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $stage 'manifest.json') -Encoding UTF8

Copy-Item (Join-Path $repo 'README.md') (Join-Path $stage 'README.md') -Force
if (Test-Path (Join-Path $repo 'CHANGELOG.md')) { Copy-Item (Join-Path $repo 'CHANGELOG.md') $stage -Force }

$icon = Join-Path $repo 'thunderstore\icon.png'
if (-not (Test-Path $icon)) {
    & python (Join-Path $repo 'thunderstore\make-icon.py') $icon
    if ($LASTEXITCODE -ne 0) { throw "Icon generation failed (needs Python + Pillow, or drop a 256x256 icon.png in thunderstore\)." }
}
Copy-Item $icon (Join-Path $stage 'icon.png') -Force

$zip = Join-Path $dist "BigScreen-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$stage\*" -DestinationPath $zip
Remove-Item $stage -Recurse -Force
Write-Host "packaged -> $zip" -ForegroundColor Green
Write-Host "Upload at https://thunderstore.io/c/big-walk/create/ (see docs/MODDING-PRIMER.md, 'Publishing')."
