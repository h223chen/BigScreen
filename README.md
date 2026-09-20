# BigScreen

A shared, in-world screen for [Big Walk](https://store.steampowered.com/app/1478500/Big_Walk/).
The host places a screen somewhere on the island, pastes a YouTube link, and everyone with
the mod sees and hears the same video at the same moment. Sit around it, heckle, walk off
and let the sound fade behind you.

> **Status: alpha, runs in-game.** As of 2026-09-19 the mod compiles against Big Walk
> (Unity 6000.3.17f1, BepInEx 6.0.0-be.755), loads, hooks Mirror, and places a working screen
> that syncs and tears down correctly. Video playback is the remaining unverified step.
> All three risks in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) are now resolved or retired
> and annotated with what actually happened; [docs/PLAYTEST.md](docs/PLAYTEST.md) tracks what
> is still unverified.

## How it works, in one paragraph

Big Walk is a Unity (IL2CPP) game networked with Mirror. BigScreen is a BepInEx plugin.
Every player who wants to watch installs it. The host owns a tiny piece of shared state
(where the screen is, which video, playing or paused, and a timeline anchor); it is sent
to modded guests over the game's own Mirror connection on a private message id. Each
player resolves the YouTube link to a direct stream URL locally with `yt-dlp` and plays it
with Unity's built-in VideoPlayer onto a quad in the world, with positional audio. Everyone
keeps their playback locked to the host's timeline using Mirror's shared clock. Players
without the mod are unaffected: they see nothing and receive nothing.

## Requirements

- Big Walk on Steam (Windows, or Linux through Proton)
- [BepInExPack IL2CPP](https://thunderstore.io/c/big-walk/p/BepInEx/BepInExPack_IL2CPP/)
  (installed for you by a mod manager such as Gale or r2modman)
- `yt-dlp.exe`. BigScreen downloads it automatically the first time it is needed
  (from the official yt-dlp GitHub releases), or set `YtDlp.Path` in the config.

## Install

**With a mod manager (recommended):** in Gale or r2modman, select Big Walk, create a profile,
install `BepInExPack_IL2CPP`, then import the BigScreen zip (or install it from Thunderstore
once it is published). Launch the game from the manager.

**Manually:** install BepInExPack IL2CPP into the game folder, launch the game once (the first
launch takes a few minutes while it generates interop files), then drop `BigScreen.dll` into
`BepInEx\plugins\BigScreen\`.

Everyone who wants to watch needs the mod. The host does not need to do anything special.

## Use

1. Host or join a walk.
2. Press **F8** to open the panel.
3. **Place screen here** puts a screen 4 m in front of you, facing you.
4. Paste a YouTube URL and press **Load**. Everyone with the mod resolves and starts the
   video in sync a few seconds later.
5. Play/pause and seek from the panel. Guests can only control playback if the host ticks
   "Let modded guests control playback".

Your own volume slider is local. The audio is positional: stand close to hear it, walk away
and it fades (configurable range).

## Configuration

`BepInEx\config\dev.h223chen.bigscreen.cfg` (created on first launch). Highlights:

| Section | Key | Default | Meaning |
| --- | --- | --- | --- |
| Keys | ToggleUI | F8 | Open/close the panel |
| Audio | Volume / MaxDistance | 0.8 / 40 | Local volume; distance at which the screen goes silent |
| Screen | WidthMeters | 4 | Physical screen width (16:9) |
| YtDlp | Path / AutoDownload | "" / true | Where yt-dlp.exe lives; download it if missing |
| YtDlp | FormatSelector | muxed-MP4 chain | yt-dlp `-f` expression. Unity needs a single H.264+AAC MP4 |
| Sync | DriftToleranceSeconds | 1.0 | Re-seek when you are further than this from the host's timeline |
| Sync | GuestsCanControl / AutoPlay | false / true | Host-side permissions |
| Debug | Diagnostics | false | Verbose log line every 2 s |

## Building from source

Prerequisites: .NET SDK 8 or newer, Big Walk launched at least once with BepInEx so that
`BepInEx\interop` exists (in your mod-manager profile or the game folder).

```powershell
.\scripts\build.ps1            # build src\BigScreen -> src\BigScreen\bin\Release\BigScreen.dll
.\scripts\build.ps1 -Deploy    # build and copy into <BepInEx>\plugins\BigScreen (game must be closed)
.\scripts\package.ps1          # Thunderstore-ready zip in dist\
```

The build auto-detects the Gale profile, the r2modman profile, and the Steam game folder in
that order. Anything else: copy `Config.Build.user.props.template` to
`Config.Build.user.props` and set `BepInExPath`.

New to Unity or game modding? Start with [docs/MODDING-PRIMER.md](docs/MODDING-PRIMER.md).

## Docs

- [docs/MODDING-PRIMER.md](docs/MODDING-PRIMER.md): how Big Walk modding works, the
  toolchain, the dev loop, publishing.
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): design, sync protocol, risks, roadmap.
- [docs/MIRROR-MESSAGING.md](docs/MIRROR-MESSAGING.md): how the networking works and why it
  bypasses Mirror's public message API, for readers new to Mirror.
- [docs/TASK_LIST.md](docs/TASK_LIST.md): planned improvements, and what the game's own
  interaction systems offer.
- [docs/PLAYTEST.md](docs/PLAYTEST.md): what to verify on the first in-game runs.

## Credits

Built on the shoulders of the Big Walk modding community, in particular
[dougwithseismic/bigwalk-mods](https://github.com/dougwithseismic/bigwalk-mods) (toolchain and
reverse-engineering guide), [iameli/big-walk-practice](https://github.com/iameli/big-walk-practice)
(Mirror ownership notes, CI pattern) and the Trifocals mods (in-game patterns for
players, audio and effects). BepInEx, Il2CppInterop, Harmony, Mirror and yt-dlp do the heavy
lifting. Not affiliated with House House or Panic.

## License

MIT. See `LICENSE`.
