# Shared helpers for BigScreen build/deploy scripts.
Set-StrictMode -Version Latest

function Import-DotEnv {
    <#
      Loads KEY=VALUE lines from .env in the repo root into this process. Real environment
      variables win, so a shell override still beats the file. .env is per-machine and
      gitignored; .env.template is committed and lists the supported keys.
    #>
    param([string]$Path = (Join-Path (Split-Path $PSScriptRoot -Parent) '.env'))

    if (-not (Test-Path $Path)) { return }
    foreach ($line in Get-Content $Path) {
        $trimmed = $line.Trim()
        if (-not $trimmed -or $trimmed.StartsWith('#')) { continue }

        $split = $trimmed.IndexOf('=')
        if ($split -lt 1) { continue }

        $key = $trimmed.Substring(0, $split).Trim()
        $value = $trimmed.Substring($split + 1).Trim().Trim('"')
        if (-not [Environment]::GetEnvironmentVariable($key)) {
            [Environment]::SetEnvironmentVariable($key, $value)
        }
    }
}

# Every script that dot-sources this file gets the .env values.
Import-DotEnv

function Get-BigWalkPath {
    <#
      Resolves the Big Walk install directory by reading Steam's libraryfolders.vdf
      and looking for the game in each library. Falls back to $env:BIGWALK_PATH.
    #>
    if ($env:BIGWALK_PATH -and (Test-Path $env:BIGWALK_PATH)) { return $env:BIGWALK_PATH }

    $steamRoots = @(
        "${env:ProgramFiles(x86)}\Steam",
        "$env:ProgramFiles\Steam"
    ) | Where-Object { $_ -and (Test-Path $_) }

    foreach ($root in $steamRoots) {
        $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (-not (Test-Path $vdf)) { continue }
        $libs = Select-String -Path $vdf -Pattern '"path"\s+"(.+?)"' -AllMatches |
                ForEach-Object { $_.Matches.Groups[1].Value -replace '\\\\', '\' }
        foreach ($lib in $libs) {
            $candidate = Join-Path $lib 'steamapps\common\Big Walk'
            if (Test-Path (Join-Path $candidate 'Big Walk.exe')) { return $candidate }
        }
    }
    return $null
}

function Get-BepInExPath {
    <#
      The BepInEx folder we compile against and deploy into. Mod-manager profiles win
      over a loader installed straight into the game folder, because that is what most
      players (and the Thunderstore pack) use. Override with $env:BIGSCREEN_BEPINEX.
    #>
    if ($env:BIGSCREEN_BEPINEX -and (Test-Path $env:BIGSCREEN_BEPINEX)) { return $env:BIGSCREEN_BEPINEX }

    $candidates = @(
        "$env:APPDATA\com.kesomannen.gale\big-walk\profiles\Default\BepInEx",
        "$env:APPDATA\r2modmanPlus-local\BigWalk\profiles\Default\BepInEx"
    )
    $game = Get-BigWalkPath
    if ($game) { $candidates += (Join-Path $game 'BepInEx') }

    foreach ($c in $candidates) {
        if (Test-Path (Join-Path $c 'interop\Assembly-CSharp.dll')) { return $c }
    }
    throw "No BepInEx folder with generated interop assemblies found. Install BepInExPack_IL2CPP (via Gale/r2modman or manually), launch Big Walk once, then retry. Or set BIGSCREEN_BEPINEX."
}

function Get-GalePath {
    <#
      The Gale mod manager executable. Gale can launch a profile from the command line,
      which is the only way to start the game modded without clicking through its window.
      Override with GALE_PATH in .env or the environment.
    #>
    if ($env:GALE_PATH -and (Test-Path $env:GALE_PATH)) { return $env:GALE_PATH }

    $candidates = @(
        "$env:ProgramFiles\gale\gale.exe",
        "${env:ProgramFiles(x86)}\gale\gale.exe",
        "$env:LOCALAPPDATA\Programs\gale\gale.exe"
    ) | Where-Object { $_ }

    foreach ($c in $candidates) {
        if (Test-Path $c) { return $c }
    }
    throw "Gale not found. Copy .env.template to .env and set GALE_PATH, or install Gale from https://github.com/Kesomannen/gale."
}

function Get-GaleGameSlug {
    # Gale's own folder name for the game, as used by --game. Its data lives in
    # %APPDATA%\com.kesomannen.gale\<slug>.
    if ($env:GALE_GAME_SLUG) { return $env:GALE_GAME_SLUG }
    return 'big-walk'
}

function Get-GaleProfileName {
    if ($env:GALE_PROFILE) { return $env:GALE_PROFILE }
    return 'Default'
}

function Assert-GameClosed {
    if (Get-Process -Name 'Big Walk' -ErrorAction SilentlyContinue) {
        throw "Big Walk is running. Close it first - the game locks plugin DLLs while it runs."
    }
}
