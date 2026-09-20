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
