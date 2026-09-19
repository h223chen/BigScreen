# Shared helpers for BigScreen build/deploy scripts.
Set-StrictMode -Version Latest

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

function Assert-GameClosed {
    if (Get-Process -Name 'Big Walk' -ErrorAction SilentlyContinue) {
        throw "Big Walk is running. Close it first - the game locks plugin DLLs while it runs."
    }
}
