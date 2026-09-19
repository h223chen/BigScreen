# BigScreen (Big Walk mod) - agent notes

Read `docs/HANDOFF.md` first. It is the ordered task list for whoever picks this project
up on a machine that has Big Walk, BepInEx and the .NET SDK installed.

## What this is
A BepInEx 6 (IL2CPP) plugin for Big Walk (Unity 6, Mirror networking). It places a shared
screen in the world and plays a YouTube video on it for every player with the mod, in sync.
Design and rationale: `docs/ARCHITECTURE.md`. Background for engineers new to Unity/modding:
`docs/MODDING-PRIMER.md`.

## Current state
- All code in `src/BigScreen/` was written without access to the game. It parses as C# but
  has never been compiled against the game's interop assemblies nor run in-game.
- Expect a handful of interop naming fixes on the first build. `docs/HANDOFF.md` lists the
  likely suspects and how to check them.

## Build / deploy
```powershell
.\scripts\build.ps1            # dotnet build against the auto-detected BepInEx folder
.\scripts\build.ps1 -Deploy    # + copy DLL into <BepInEx>\plugins\BigScreen (game must be closed)
.\scripts\package.ps1          # Thunderstore zip in dist\
```
BepInEx folder resolution: `/p:BepInExPath`, else `Config.Build.user.props`, else Gale profile,
r2modman profile, game folder. See `Directory.Build.props`.

## Conventions
- Only touch Unity/IL2CPP objects on the main thread; background work posts to `Util/MainThread`.
- Poll IL2CPP state (e.g. `VideoPlayer.isPrepared`) instead of subscribing to IL2CPP events.
- Wrap every game call in try/catch with a log line; a game update must degrade, not crash.
- Never create networked (Mirror-spawned) objects; the screen is local-only on modded clients.
- Call Mirror's `NetworkWriterExtensions` / `NetworkReaderExtensions` statically.
- Keep `Plugin.Version`, `<Version>` in the csproj, and `thunderstore/manifest.json` in sync.
- Do not commit anything from `BepInEx\interop` or decompiled game output.

## Logs
BepInEx log: `<BepInEx>\LogOutput.log`. Our lines are prefixed `[Info   :BigScreen]` etc.
Set `Debug.Diagnostics = true` in `BepInEx\config\dev.h223chen.bigscreen.cfg` for a status line
every 2 s.
