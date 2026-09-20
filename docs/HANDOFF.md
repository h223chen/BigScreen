# Handoff: next steps for an agent with the game installed

> **Milestones 1 and most of 2 are done (2026-09-19).** This document is kept for the record;
> read the status notes below before following any step literally.
>
> - **Milestone 1 complete.** Builds against Big Walk (Unity 6000.3.17f1,
>   BepInEx 6.0.0-be.755), deploys, loads.
> - **Every interop assumption in the table below was CORRECT.** The predicted "handful of
>   naming fixes" did not materialise - not one member name was wrong. The two compile errors
>   were toolchain artefacts, not API mismatches (see `ARCHITECTURE.md`, "Decisions taken
>   during the first real run").
> - **Milestone 2 mostly done.** Plugin loads, Mirror handlers register, screen places,
>   renders and tears down. Video playback is the one step still unverified.
> - **All three risks resolved.** Risks 1 and 3 did not occur; Risk 2 occurred in the form its
>   fallback already handled. Each is annotated in `ARCHITECTURE.md`.
> - Use `.\scripts\game-state.ps1` to check whether the game is running, whether it is running
>   modded, and what BigScreen logged - rather than asking the user.

## Session log: 2026-09-19, 20:00-23:40 (second playtest)

**Where it stands: video playback is still unverified, blocked by a reproducible crash. The
game dies with a fatal access violation when the Test MP4 button is clicked.** Nine crash
dumps are retained in `%LOCALAPPDATA%\CrashDumps` covering 22:06-23:32; earlier ones have
rotated out. Every one has the identical signature:

```
Faulting module : coreclr.dll 6.0.722.32202
Exception code  : 0xc0000005   (access violation)
Fault offset    : 0x00000000001d1fdd
Description     : The process was terminated due to an internal error in the .NET Runtime
```

### Confirmed working

- **yt-dlp resolution.** Needed `--extractor-args youtube:player_client=android`; without it
  YouTube returns only storyboard images and every format selector fails with "Requested
  format is not available". Exposed as `YtDlp.ExtractorArgs`. Verified: resolved a 682 s video.
- **Auto-host.** `SaveManager.GetAllSaveDatasInFolder()` -> match on `slotName` ->
  `HostMenuSelect.ActionSelectSaveData` -> `PlayerCountSwapper.playerCount` ->
  `NetworkMinder.StartHost()`. Takes you from the host menu into a session.
- **Intro screens.** `SplashMenu.ActionContinue()`, `MicCheckMenu.ActionContinue()`,
  `TitleMenu.GoToHostMenu()`. With `Dev.AutoDismissMenus` the launch is click-free.
- **Spawn teleport.** `PlayerCharacter.grease.Teleport(Vector3, Quaternion, bool)`. The panel's
  "Set spawn here" button writes the current position into `Dev.SpawnPosition`.
- **Screen placement, rendering and lobby teardown.** Placing repeatedly and clicking panel
  buttons does not crash.

### The crash

What is established:

- It happens on the **main thread** (the dump's faulting thread bottoms out in
  `Big Walk.exe` -> `UnityPlayer.dll` -> WinMain).
- `coreclr.dll` only ever runs BepInEx and plugin code - the game itself is IL2CPP in
  `GameAssembly.dll` - so the fault is in mod-land, not the game.
- It is **not** a managed exception. `OnGUI` is now wrapped and logs nothing before dying.
- The trigger is **clicking Test MP4**, not placing a screen and not yt-dlp.
- It dies **before the first statement of `UserLoad`** executes. `UserLoad:` never reaches the
  log, and the deployed DLL is verified to contain that string.

Ruled out, so do not spend time here again:

1. **yt-dlp / subprocess handling.** Five crashes correlated with `Resolving`, which looked
   convincing, but one crash happened with no yt-dlp running at all. The async-to-threads
   rewrite in `YtDlp.cs` was aimed at this and did not fix anything. It is still worth keeping:
   the old code read stdout to completion before stderr, which deadlocks once yt-dlp fills the
   stderr pipe.
2. **The IMGUI window delegate.** `GUI.Window` was being handed a freshly cast
   `GUI.WindowFunction` every frame with no managed reference kept. Caching it is correct and
   was kept, but it did not fix the crash.
3. **Symbolicating the fault address.** `coreclr.dll+0x1d1fdd` sits next to
   `EEPolicy::HandleFatalError`, so it is almost certainly inside the runtime's own
   fatal-error path - where the process was when it died, not where the bug is. No function
   symbol covers it. Do not chase this address.

### Current hypothesis, fix deployed but untested

The Test MP4 button wrote `_urlInput = TestMp4Url` before calling `UserLoad`. That replaces the
text field's contents while IMGUI's `TextEditor` still holds cursor and selection indices from
the previous string. Indices past the end of the new string make native IMGUI read out of
bounds, which is an access violation rather than a catchable exception. It fits: only this
button crashes, "Load" (which reads `_urlInput` but never writes it) does not.

The button no longer writes `_urlInput`, clears `GUIUtility.keyboardControl` first, and logs
`Test MP4 clicked.` as its first statement. **If that line does not appear on the next run, the
handler is not the problem and this hypothesis is wrong.**

Note that `set_url_Injected` - the Unity 6 string-setter workaround in `UnityVideoBackend` -
**has still never executed**. Every run dies upstream of it. Watch for `VideoPlayer: setting
url.` followed by `VideoPlayer: url set, preparing.`; if it stops between those two, the
`ManagedSpanWrapper` call is the next suspect.

### Tooling added

- `scripts/launch.ps1` - build, deploy and launch through Gale's CLI
  (`--game big-walk --profile Default --launch --no-gui`). Gale is single-instance: with its
  window open the command line is a no-op, so the script closes it first.
- `.env` / `.env.template` - per-machine paths (`GALE_PATH`, `GALE_PROFILE`, ...), loaded by
  `scripts/common.ps1`. Real environment variables win.
- `src/BigScreen/Dev/AutoStart.cs` - the `[Dev]` config block. All of it defaults to off.
- `BepInEx.cfg` has `InstantFlushing = true`, without which a hard crash loses the log tail.
  This is what made the breadcrumbs usable; keep it on while debugging.

### Next steps

1. Launch, F8, Place screen, Test MP4. Read `LogOutput.log`. Either `Test MP4 clicked.` appears
   (hypothesis alive, crash moved elsewhere) or it does not (hypothesis dead).
2. If playback is reached, confirm the picture and audio by eye - the log saying
   `Stream ready` does not mean the quad renders correctly.
3. Nothing is committed. Working tree has changes to `BigScreenController`, `ControlPanel`,
   `UnityVideoBackend`, `YtDlp`, `Plugin`, `common.ps1`, `.gitignore`, `CLAUDE.md`, plus new
   `Dev/AutoStart.cs`, `Util/CompilerShims.cs`, `scripts/launch.ps1`, `.env.template`.
4. `dotnet-dump` was installed globally for the crash analysis; remove with
   `dotnet tool uninstall -g dotnet-dump` if unwanted.

Everything below assumes a Windows machine with Big Walk (Steam), a mod manager (Gale or
r2modman) with BepInExPack IL2CPP installed in a profile, the game launched once with that
profile so `BepInEx\interop\` exists, and .NET SDK 8+ on PATH. Nothing here has been run yet;
the previous session had no game and no .NET SDK, so the code is unverified beyond a syntax
parse. Treat every step as "confirm or fix", and commit after each milestone.

Branch to work on: `claude/hopeful-fermi-gc2zz4` (or whatever the user says).

## Milestone 1: it compiles

1. Confirm prerequisites:
   ```powershell
   dotnet --version
   dir "$env:APPDATA\com.kesomannen.gale\big-walk\profiles\Default\BepInEx\interop" | measure   # or the r2modman path
   ```
   If the interop folder is elsewhere, create `Config.Build.user.props` from the template and
   set `BepInExPath`.
2. Check the single biggest assumption:
   `Test-Path "<BepInEx>\interop\UnityEngine.VideoModule.dll"`.
   - Missing: stop and report. The Unity video backend cannot work on this build. The plan
     for that case is the ffmpeg backend in `docs/ARCHITECTURE.md`, "Risk 1". Implement it
     behind `Video/IVideoBackend.cs` before continuing (and make `UnityVideoBackend.cs`
     compile-conditional or remove it).
3. Build: `.\scripts\build.ps1`. Fix compile errors. They will be interop member names that
   differ from what was assumed. To check a name, open the interop DLL in ILSpy (`dotnet tool
   install -g ilspycmd`, then `ilspycmd -t <TypeName> "<BepInEx>\interop\Assembly-CSharp.dll"`)
   or grep: `ilspycmd "<dll>" | Select-String "cameraTransform"`. Assumptions made, by file:

   | File | Assumed member | Source of the assumption |
   | --- | --- | --- |
   | `BigScreenController.cs` | `WorldManager.localPlayerCharacter`, `PlayerCharacter.allPlayerCharacters`, `PlayerCharacter.playerNetworking.isLocalPlayer`, `PlayerCharacter.cameraTransform` | used by EnderBuoy/BigDJ mods; `cameraTransform` from big-walk-practice notes |
   | `Plugin.cs` | `HouseNetworkManager.OnStopHost()`, `OnStopClient()` | big-walk-practice patches the same methods |
   | `Net/MirrorChannel.cs` | `NetworkServer.handlers`, `NetworkClient.handlers` (Il2Cpp `Dictionary<ushort, NetworkMessageDelegate>`), `NetworkConnection.Send(Il2CppSystem.ArraySegment<byte>, int)`, `NetworkWriterPool.Get/Return`, `NetworkWriter.ToArraySegment()`, `NetworkMessageDelegate(NetworkConnectionToClient, NetworkReader, int)` | Mirror master source; interop exposes internals as public |
   | `Net/Protocol.cs` | `NetworkWriterExtensions.Write{Byte,Int,Bool,Float,Double,String,Vector3}`, matching readers | Mirror source; Moderation-Improvements calls `NetworkReaderExtensions.ReadString` statically |
   | `Video/UnityVideoBackend.cs` | `UnityEngine.Video.VideoPlayer` members: `source,url,renderMode,targetTexture,audioOutputMode,controlledAudioTrackCount,EnableAudioTrack,SetTargetAudioSource,Prepare,isPrepared,isPlaying,time,length,width,height,aspectRatio,waitForFirstFrame,skipOnDrop` | Unity API |
   | `World/ScreenObject.cs` | `Mesh.vertices/uv/normals/triangles` accept `Il2CppStructArray<T>` casts; `Shader.Find`; `Material.HasProperty/SetTexture/SetColor/EnableKeyword`; `GL.Clear`; `UnityEngine.Rendering.ShadowCastingMode` | Unity API + Il2CppInterop array conversions |
   | `UI/ControlPanel.cs` | `GUI.Window(int, Rect, GUI.WindowFunction, string)` with a method-group cast, `GUILayout.*` with a single `GUILayoutOption` | copied from the DevMenu overlay in bigwalk-mods |

4. Commit: "Compiles against Big Walk <game version from LogOutput.log boot line>".

## Milestone 2: it loads and the screen appears (solo, hosted lobby)

Follow `docs/PLAYTEST.md` sections 1 and 2. In particular:

- `BigScreen v0.1.0 loaded.` in `LogOutput.log`; no exception from `Load()`.
- `Server message handler registered.` / `Client message handler registered.` If you see
  "Could not hook into Mirror's message handlers", read the exception: it is the
  `NetworkMessageDelegate` signature (`MirrorChannel.Convert`) or the handlers dictionary type.
  Fix or, if hopeless, implement the Dissonance text fallback from ARCHITECTURE.md "Risk 2".
- F8 opens the panel with a free cursor. "Place screen here" shows a panel 4 m ahead, facing
  you. Log line `Screen shader: <name>`. Pink/invisible: install UnityExplorer (IL2CPP) in the
  profile, inspect any flat game object's material, and put its shader name at the top of
  `ScreenObject.ShaderCandidates`. Mirrored/back-facing: flip `fwd` in
  `BigScreenController.UserPlaceScreen` or the winding in `ScreenObject.MakeQuad`.
- Load a short YouTube URL. Expected log chain is in PLAYTEST.md. If yt-dlp fails with
  "Requested format is not available", press "Update yt-dlp" then retry; if it still fails,
  run yt-dlp by hand: `yt-dlp -F <url>` and adjust `YtDlp.FormatSelector` in the config so a
  muxed MP4 (video+audio, `avc1`/`mp4a`) is selected. Format `18` should always exist.
- If `Stream ready` appears but the picture is black: check the RenderTexture is assigned
  (`VideoPlayer.targetTexture`) and the material's texture property name (`_MainTex` vs
  `_BaseMap`) in UnityExplorer.
- Commit: "Screen and playback verified solo".

## Milestone 3: two modded players stay in sync

Follow `docs/PLAYTEST.md` section 3. Needs a second PC or a second Steam account (Family
Sharing works). Watch `drift` in the panel on both machines; it should settle under ~0.3 s and
not re-seek repeatedly (log `Re-synced:` lines). Tune `DriftToleranceSeconds` and
`MinSecondsBetweenSeeks` in `BigScreenController` if it thrashes. Verify a vanilla third
player is untouched. Commit: "Two-player sync verified".

## Milestone 4: release

1. Upload build references so CI works: `.\scripts\export-build-refs.ps1`, then
   `gh release create build-refs --title "Build references" --notes "Interop refs for CI"` and
   `gh release upload build-refs --clobber dist\build-refs.zip`. Re-run the workflow.
2. Update `CHANGELOG.md`, bump versions (csproj, `Plugin.Version`, manifest), `.\scripts\package.ps1`.
3. Publish per `docs/MODDING-PRIMER.md` section 5.

## Things deliberately left out of v0.1

- ffmpeg backend (only needed if VideoModule is stripped, or for >720p / non-MP4 sources).
- ModSettingsMenu integration (in-game config UI used by most Big Walk mods).
- Playlist/queue, subtitles, persistence of the screen position.
- Live streams (no duration; drift logic untested).

## How to report back

For each milestone, note in the commit message or a short `docs/STATUS.md`: game version
(boot line in `LogOutput.log`), BepInEx pack version, what worked, what was changed and why,
and any log excerpts for failures that were not fixed.
