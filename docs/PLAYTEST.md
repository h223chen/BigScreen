# First-launch checklist

Work through this in order; each step either confirms an assumption or points at the exact
place to change.

> **Status as of 2026-09-19** (Unity 6000.3.17f1, BepInEx 6.0.0-be.755):
>
> | Section | State |
> | --- | --- |
> | 0. Before building | **done** - interop generated, `UnityEngine.VideoModule.dll` present |
> | 1. Build and load | **done** - `BigScreen v0.1.0 loaded.`, no exceptions, both Harmony patches applied |
> | 2. Solo hosted lobby | **mostly done** - F8 panel, Mirror handlers, screen place/render/remove, lobby teardown all verified. **Video playback still unverified.** Shader resolved to `Sprites/Default`. |
> | 3. Two modded players | **not started** - needs a second machine or Steam account |
> | 4. Rough edges | not started |
>
> Known open items: the F8 panel's UX is poor and clicks are unreliable (see
> `TASK_LIST.md`); one unexplained game freeze occurred and has not recurred.
> `.\scripts\game-state.ps1` reports live game/mod/plugin state and the last session's log.

## 0. Before building

- [ ] BepInExPack IL2CPP installed (Gale/r2modman profile) and the game launched once with it.
- [ ] `BepInEx\interop\` contains ~150 DLLs, including `Mirror.dll`, `Assembly-CSharp.dll`.
- [ ] **`BepInEx\interop\UnityEngine.VideoModule.dll` exists.** If not: Risk 1 in
      ARCHITECTURE.md; the Unity video backend cannot work on this build and the ffmpeg backend
      must be implemented first.
- [ ] Optional but recommended: install UnityExplorer (IL2CPP build) into the same profile.

## 1. Build and load

- [ ] `.\scripts\build.ps1 -Deploy` succeeds. Compile errors here almost always mean an interop
      member is named differently than assumed. Open the interop DLL in ILSpy/dnSpy (or search
      it with UnityExplorer) and fix the name. Likely candidates:
      `PlayerCharacter.cameraTransform`, `PlayerNetworking.isLocalPlayer`,
      `WorldManager.localPlayerCharacter`, `HouseNetworkManager.OnStopHost/OnStopClient`,
      `NetworkServer.handlers`, `NetworkConnection.Send(ArraySegment<byte>, int)`.
- [ ] Launch; `LogOutput.log` shows `BigScreen v0.1.0 loaded.` and the config file appears at
      `BepInEx\config\dev.h223chen.bigscreen.cfg`.
- [ ] No exception from `Load()` (a failed `RegisterTypeInIl2Cpp` or Harmony patch is logged
      as a warning and the plugin keeps going).

## 2. Solo, in a hosted lobby (host = you)

- [ ] F8 opens the panel; the cursor is free; F8 closes it and the cursor locks again.
      Role reads `Host (modded guests: 0)`.
- [ ] Log shows `Server message handler registered.` and `Client message handler registered.`
      If instead you see "Could not hook into Mirror's message handlers": Risk 2. Check the
      exception text for the delegate signature and adjust `MirrorChannel.Convert`.
- [ ] **Place screen here**: a dark 16:9 panel with a frame appears 4 m in front of you, facing
      you, standing on legs. Log shows `Screen shader: <name>`.
      - Pink or invisible: Risk 3. Use UnityExplorer to find a shader name the game uses and put
        it first in `ScreenObject.ShaderCandidates`.
      - Facing away / mirrored: flip the sign in `UserPlaceScreen` (`fwd` vs `-fwd`) or the
        triangle winding in `ScreenObject.MakeQuad`.
- [ ] Paste a short YouTube URL, **Load**. Expected log sequence:
      `Resolving ...` -> (first time) `Downloading yt-dlp ...` -> `Resolved '<title>' (Ns)` ->
      `VideoPlayer preparing stream...` -> `Stream ready: WxH, Ns.` -> picture and sound.
      - yt-dlp errors are shown in red in the panel. "Requested format is not available":
        click **Update yt-dlp**, or loosen `YtDlp.FormatSelector`.
      - `Timed out preparing the stream`: Unity refused the URL. Copy the direct URL from the
        log into VLC to confirm it plays; if VLC plays it and Unity does not, the codec is the
        problem (try forcing `-f 18` in the selector). If nothing plays, the URL expired.
      - Picture but no sound: check `AudioSource` in UnityExplorer (spatialBlend, volume) and
        whether `VideoPlayer.audioTrackCount` is 0 (audio-less format chosen).
- [ ] Play/Pause, +5s/-5s, Restart all work; `drift` in the panel stays under ~0.3 s.
- [ ] Walk away: sound fades by `Audio.MaxDistance`. Walk around behind it: no picture on the
      back (single-sided), which is fine.
- [ ] Leave to the main menu: screen disappears, log shows `Reset (network stopped)` or
      `Reset (left lobby)`.

## 3. Two modded players

- [ ] Guest joins after the host placed a screen: guest log shows `Sent Hello to host.`, host
      log shows `Peer N has BigScreen v0.1.0`. Guest's panel role reads `Guest (connected to host)`.
      The screen appears at the same spot on the guest.
- [ ] Host loads a video: guest resolves it too and starts within a few seconds; `drift` on
      both stays within tolerance. Pause on the host pauses the guest on the same frame
      (within a network round trip).
- [ ] Host seeks: guest follows (one re-seek, no thrash).
- [ ] Guest tries **Load** with guest control off: red message, host unaffected. Host turns
      guest control on: the guest's Load / Play / seek now work and everyone follows.
- [ ] Host places a screen before the guest joins vs after: both orders work (the Hello reply
      carries full state).
- [ ] A **vanilla** third player in the lobby sees nothing, hears nothing, and their log (if
      they have BepInEx at all) shows at most nothing. Confirm the host never sends to them:
      `Modded peers` count excludes them.
- [ ] Guest leaves: host's `Modded peers` drops on the next heartbeat (`Modded peer N left.`).

## 4. Rough edges to note for the next iteration

- Time from Load to first frame on each machine (yt-dlp + prepare).
- Whether the screen survives the game's sub-scene streaming when you walk far away and back.
- CPU/GPU cost with the panel open (IMGUI) and while playing.
- Any `Unknown message id` warnings in *anyone's* log: a message reached someone it should not.
