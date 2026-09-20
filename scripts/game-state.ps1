<#
.SYNOPSIS
    Reports whether Big Walk is running, whether it is running MODDED, and what BigScreen logged.
.DESCRIPTION
    Answers the questions that otherwise require asking the user:
      - is the game running (and therefore locking plugin DLLs)?
      - was it launched through the mod loader, or plain from Steam?
      - did our plugin load, and has it logged anything since?

    "Modded" is decided by comparing BepInEx's LogOutput.log write time against the game
    process start time. BepInEx recreates that log on every modded launch and writes to it
    continuously, so a log older than the current process means the loader never ran - which
    is exactly what a Steam launch looks like. Process module inspection is used as a stronger
    confirmation when the OS lets us read it.
.PARAMETER Json
    Emit a JSON object instead of human-readable text.
.PARAMETER WaitFor
    Block until the game reaches this state, then report. 'Started' additionally waits for the
    loader to come up, so it returns once the game is confirmed modded rather than merely running.
.PARAMETER TimeoutSeconds
    Give up waiting after this long. Default 600.
.EXAMPLE
    .\scripts\game-state.ps1
    .\scripts\game-state.ps1 -Json
    .\scripts\game-state.ps1 -WaitFor Stopped   # returns when it is safe to deploy
#>
[CmdletBinding()]
param(
    [switch]$Json,
    [ValidateSet('Started', 'Stopped')]
    [string]$WaitFor,
    [int]$TimeoutSeconds = 600
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\common.ps1"

function Read-SharedText {
    # The game holds LogOutput.log open, so a plain Get-Content fails. Open with full sharing.
    param([string]$Path)
    if (-not (Test-Path $Path)) { return '' }
    try {
        $fs = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try {
            $sr = New-Object IO.StreamReader($fs)
            try { return $sr.ReadToEnd() } finally { $sr.Dispose() }
        } finally { $fs.Dispose() }
    } catch { return '' }
}

function Get-GameState {
    $proc = Get-Process -Name 'Big Walk' -ErrorAction SilentlyContinue | Select-Object -First 1

    $bepinex = $null
    try { $bepinex = Get-BepInExPath } catch { $bepinex = $null }
    $logPath = if ($bepinex) { Join-Path $bepinex 'LogOutput.log' } else { $null }

    $state = [ordered]@{
        running        = [bool]$proc
        pid            = $null
        uptimeSeconds  = $null
        modded         = $null
        moddedEvidence = 'game not running'
        pluginLoaded   = $null
        pluginVersion  = $null
        deploySafe     = -not [bool]$proc
        bepinexPath    = $bepinex
        logPath        = $logPath
        bigScreenLines = @()
        errors         = @()
    }

    # Read the log whether or not the game is running - when it is closed, this is the
    # post-mortem of the session that just ended, which is usually what we want to see.
    $text = Read-SharedText $logPath
    if ($text) {
        $lines = @($text -split "`n" | Where-Object { $_.Trim() })
        $state.bigScreenLines = @($lines | Where-Object { $_ -match ': BigScreen\]' } | ForEach-Object { $_.Trim() })
        # Anchored and case-sensitive: BepInEx lines start with the level tag, and a
        # case-insensitive '\[Error' also matches payload text like 'ErrorCode=[errors.com...'.
        $state.errors = @($lines | Where-Object { $_.Trim() -cmatch '^\[(Error|Fatal)' } | ForEach-Object { $_.Trim() } | Select-Object -Last 5)

        $loaded = $state.bigScreenLines | Where-Object { $_ -match 'BigScreen v([\d\.]+) loaded' } | Select-Object -Last 1
        $state.pluginLoaded = [bool]$loaded
        if ($loaded -and $loaded -match 'BigScreen v([\d\.]+) loaded') { $state.pluginVersion = $Matches[1] }
    }
    else {
        $state.pluginLoaded = $false
    }

    if (-not $proc) { return $state }

    $state.pid = $proc.Id
    $startTime = $null
    try { $startTime = $proc.StartTime } catch { $startTime = $null }
    if ($startTime) { $state.uptimeSeconds = [math]::Round(((Get-Date) - $startTime).TotalSeconds) }

    # Strong signal: is the loader's shim actually mapped into the process?
    $shimLoaded = $null
    try {
        $shimLoaded = [bool]($proc.Modules | Where-Object { $_.ModuleName -eq 'winhttp.dll' -and $_.FileName -notlike "$env:WINDIR*" })
    } catch { $shimLoaded = $null }   # 32/64-bit or permission mismatch; fall back to the log

    if (-not $logPath -or -not (Test-Path $logPath)) {
        $state.modded = $false
        $state.moddedEvidence = 'no LogOutput.log found'
    }
    elseif ($startTime -and (Get-Item $logPath).LastWriteTime -lt $startTime) {
        $state.modded = $false
        $state.moddedEvidence = 'LogOutput.log predates this process - launched outside the mod loader?'
    }
    else {
        $state.modded = $true
        $state.moddedEvidence = if ($shimLoaded) { 'doorstop shim mapped + log is current' } else { 'log is current' }
    }

    if ($shimLoaded -eq $false -and $state.modded) {
        $state.moddedEvidence += ' (warning: doorstop shim not found in module list)'
    }

    return $state
}

if ($WaitFor) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        $s = Get-GameState
        $done = switch ($WaitFor) {
            'Started' { $s.running -and $s.modded -eq $true }
            'Stopped' { -not $s.running }
        }
        if ($done) { break }
        Start-Sleep -Seconds 3
    }
    if ((Get-Date) -ge $deadline) { Write-Warning "Timed out after ${TimeoutSeconds}s waiting for '$WaitFor'." }
}

$state = Get-GameState

if ($Json) {
    $state | ConvertTo-Json -Depth 4
    return
}

# --- human-readable ---------------------------------------------------------------
if (-not $state.running) {
    Write-Host "Big Walk  : not running" -ForegroundColor Green
    Write-Host "Deploy    : SAFE"
    if ($state.bigScreenLines.Count) { Write-Host "Log below is from the last session." -ForegroundColor DarkGray }
} else {
    Write-Host "Big Walk  : running (pid $($state.pid), up $($state.uptimeSeconds)s)"
    if ($state.modded) {
        Write-Host "Modded    : YES - $($state.moddedEvidence)" -ForegroundColor Green
    } else {
        Write-Host "Modded    : NO  - $($state.moddedEvidence)" -ForegroundColor Red
        Write-Host "            Launch through Gale's 'Start modded', not Steam."
    }
    $v = if ($state.pluginVersion) { "v$($state.pluginVersion)" } else { '' }
    if ($state.pluginLoaded) {
        Write-Host "BigScreen : loaded $v" -ForegroundColor Green
    } else {
        Write-Host "BigScreen : NOT loaded" -ForegroundColor Red
    }
    Write-Host "Deploy    : BLOCKED (game locks plugin DLLs)" -ForegroundColor Yellow
}

if ($state.bigScreenLines.Count) {
    Write-Host ""
    Write-Host "--- BigScreen log ($($state.bigScreenLines.Count) lines) ---"
    $state.bigScreenLines | Select-Object -Last 15 | ForEach-Object { Write-Host "  $_" }
}

if ($state.errors.Count) {
    Write-Host ""
    Write-Host "--- recent errors ---" -ForegroundColor Red
    $state.errors | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
}
