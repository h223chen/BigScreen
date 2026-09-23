<#
.SYNOPSIS
    Builds a shareable mod bundle under dist\.
.DESCRIPTION
    Produces a Thunderstore-format zip, which is also exactly what Gale and r2modman accept
    as a local mod import - so one artifact serves both testers and a real release.

    Layout inside the zip (all paths at the root, payload mirroring the profile):
        manifest.json, icon.png, README.md, CHANGELOG.md
        BepInEx/plugins/BigScreen/BigScreen.dll

    The zip is written with System.IO.Compression rather than Compress-Archive: Windows
    PowerShell 5.1 writes backslash path separators for nested entries, which is invalid per
    the ZIP spec and which mod managers can refuse or extract as one oddly-named file.

    Everything is checked before packing - version agreement across the three places that
    carry it, icon dimensions, payload presence - because a bundle that is wrong in one of
    those ways still zips cleanly and only fails once a tester tries to install it.
.PARAMETER Configuration
    Release (default) or Debug.
.PARAMETER IncludeYtDlp
    Bundle yt-dlp.exe (~17 MB) instead of letting the mod download it on first use. Useful
    for a tester whose network blocks the GitHub release download.
.PARAMETER SkipBuild
    Package whatever is already built, without rebuilding.
.EXAMPLE
    .\scripts\package.ps1
    .\scripts\package.ps1 -IncludeYtDlp
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$IncludeYtDlp,
    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\common.ps1"

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repo = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $repo 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null

if (-not $SkipBuild) { & "$PSScriptRoot\build.ps1" -Configuration $Configuration }

# --- Gather and cross-check the version -------------------------------------------------
# CLAUDE.md requires these three to agree. Packaging is the last point where a mismatch can
# be caught cheaply: after this it ships, and the mod would log a different version than the
# one the package claims.
[xml]$proj = Get-Content (Join-Path $repo 'src\BigScreen\BigScreen.csproj')
$version = ($proj.Project.PropertyGroup.Version | Where-Object { $_ }) -as [string]
$desc    = ($proj.Project.PropertyGroup.Description | Where-Object { $_ }) -as [string]
if (-not $version) { throw "No <Version> in BigScreen.csproj." }

$pluginSrc = Get-Content (Join-Path $repo 'src\BigScreen\Plugin.cs') -Raw
if ($pluginSrc -notmatch 'Version\s*=\s*"([^"]+)"') { throw "Could not find Plugin.Version in Plugin.cs." }
$pluginVersion = $Matches[1]

$manifestPath = Join-Path $repo 'thunderstore\manifest.json'
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json

if ($pluginVersion -ne $version) {
    throw "Version mismatch: Plugin.Version is '$pluginVersion' but the csproj says '$version'. Make them agree before packaging."
}
if ($manifest.version_number -ne $version) {
    Write-Host "manifest.json said $($manifest.version_number); updating to $version." -ForegroundColor DarkGray
}

# Thunderstore caps the description at 250 characters.
if ($desc -and $desc.Length -gt 250) { $desc = $desc.Substring(0, 250) }

# --- Warn if the tree does not match what is committed ----------------------------------
# A bundle handed to testers should be traceable to a commit; otherwise a bug report cannot
# be tied back to source.
$commit = '(unknown)'
try {
    $commit = (& git -C $repo rev-parse --short HEAD 2>$null)
    $dirty = (& git -C $repo status --porcelain 2>$null)
    if ($dirty) {
        Write-Host "WARNING: uncommitted changes - this bundle will not match any commit." -ForegroundColor Yellow
    }
} catch { }

# --- Stage ------------------------------------------------------------------------------
$stage = Join-Path $dist 'stage'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
$payload = Join-Path $stage 'BepInEx\plugins\BigScreen'
New-Item -ItemType Directory -Force $payload | Out-Null

$dll = Join-Path $repo "src\BigScreen\bin\$Configuration\BigScreen.dll"
if (-not (Test-Path $dll)) { throw "Built DLL missing: $dll  (drop -SkipBuild, or build first)" }
Copy-Item $dll $payload -Force

if ($IncludeYtDlp) {
    # Reuse the copy the mod already downloaded into the dev profile rather than fetching
    # another one; it is the same binary from the same release.
    $ytdlp = $null
    try {
        $bep = Get-BepInExPath
        if ($bep) { $ytdlp = Join-Path $bep 'plugins\BigScreen\yt-dlp.exe' }
    } catch { }

    if ($ytdlp -and (Test-Path $ytdlp)) {
        Copy-Item $ytdlp $payload -Force
        Write-Host "bundled yt-dlp.exe ($([int]((Get-Item $ytdlp).Length / 1MB)) MB)" -ForegroundColor DarkGray
    } else {
        Write-Host "WARNING: -IncludeYtDlp given but no yt-dlp.exe found; the mod will download it on first use." -ForegroundColor Yellow
    }
}

$manifest.version_number = $version
if ($desc) { $manifest.description = $desc }
$manifest | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $stage 'manifest.json') -Encoding UTF8

Copy-Item (Join-Path $repo 'README.md') (Join-Path $stage 'README.md') -Force
if (Test-Path (Join-Path $repo 'CHANGELOG.md')) { Copy-Item (Join-Path $repo 'CHANGELOG.md') $stage -Force }

# Thunderstore requires the icon to be exactly 256x256; it rejects the upload otherwise.
$icon = Join-Path $repo 'thunderstore\icon.png'
if (-not (Test-Path $icon)) {
    & python (Join-Path $repo 'thunderstore\make-icon.py') $icon
    if ($LASTEXITCODE -ne 0) { throw "Icon generation failed (needs Python + Pillow, or drop a 256x256 icon.png in thunderstore\)." }
}
$img = [System.Drawing.Image]::FromFile($icon)
try {
    if ($img.Width -ne 256 -or $img.Height -ne 256) {
        throw "icon.png must be exactly 256x256; it is $($img.Width)x$($img.Height)."
    }
} finally { $img.Dispose() }
Copy-Item $icon (Join-Path $stage 'icon.png') -Force

# --- Pack -------------------------------------------------------------------------------
$zip = Join-Path $dist "BigScreen-$version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }

# Entries are written one at a time with explicit forward-slash names. Neither
# Compress-Archive nor ZipFile.CreateFromDirectory can be used here: on .NET Framework, which
# is what Windows PowerShell 5.1 runs on, both emit Windows separators for nested entries.
# The ZIP spec requires '/', and a mod manager handed 'BepInEx\plugins\...' either rejects the
# package or extracts one file with a backslash in its name. The verification below fails the
# build if this ever regresses.
$stream = [System.IO.File]::Open($zip, [System.IO.FileMode]::Create)
try {
    $archive = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem $stage -Recurse -File) {
            $relative = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
            $entry = $archive.CreateEntry($relative, [System.IO.Compression.CompressionLevel]::Optimal)
            $target = $entry.Open()
            try {
                $source = [System.IO.File]::OpenRead($file.FullName)
                try { $source.CopyTo($target) } finally { $source.Dispose() }
            } finally { $target.Dispose() }
        }
    } finally { $archive.Dispose() }
} finally { $stream.Dispose() }

Remove-Item $stage -Recurse -Force

# --- Verify what was actually written ---------------------------------------------------
$required = @('manifest.json', 'icon.png', 'README.md', "BepInEx/plugins/BigScreen/BigScreen.dll")
$archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
try {
    $names = $archive.Entries | ForEach-Object { $_.FullName }
    foreach ($r in $required) {
        if ($names -notcontains $r) { throw "Packaged zip is missing '$r'. Entries: $($names -join ', ')" }
    }
    # Backslashes here are the Compress-Archive bug this script exists to avoid.
    $bad = $names | Where-Object { $_ -like '*\*' }
    if ($bad) { throw "Zip contains backslash separators, which mod managers reject: $($bad -join ', ')" }
    $size = [int]((Get-Item $zip).Length / 1KB)
} finally { $archive.Dispose() }

Write-Host ""
Write-Host "packaged -> $zip  (${size} KB, commit $commit)" -ForegroundColor Green
Write-Host ""
Write-Host "Give testers the zip and these steps:" -ForegroundColor Cyan
Write-Host "  Gale      : Profile -> Import -> Local mod -> pick the zip"
Write-Host "  r2modman  : Settings -> Import local mod -> pick the zip"
Write-Host "  By hand   : unzip the BepInEx folder over the profile's BepInEx folder"
Write-Host ""
Write-Host "They need BepInExPack_IL2CPP in the profile; the mod downloads yt-dlp itself on first use." -ForegroundColor DarkGray
Write-Host "To publish instead: https://thunderstore.io/c/big-walk/create/ (docs/MODDING-PRIMER.md, 'Publishing')."
