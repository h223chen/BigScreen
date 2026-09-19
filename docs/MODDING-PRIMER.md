# Big Walk modding, for software engineers who have never touched Unity

This is the "what is actually involved" document. It assumes you are comfortable with
C#, build systems and networking in general, and know nothing about Unity or game modding.

## 1. What you are modding

| Concern | What Big Walk uses | Why it matters to us |
| --- | --- | --- |
| Engine | Unity 6 (6000.3.x) | Everything is GameObjects with Components; MonoBehaviours get `Update()` called every frame |
| Scripting backend | **IL2CPP** | The game's C# was compiled to C++ then native code. There is no `Assembly-CSharp.dll` to edit or reference. |
| Networking | **Mirror** (host is server + client), transports over Steam / Epic Online Services | Custom sync must ride on Mirror's connection. Host-authoritative. |
| Voice | Dissonance | Not used by BigScreen (we do not push video audio through voice chat) |
| Platform | Steam app id 1478500, Windows x64 (Proton works on Linux) | The mod loader is Windows-only |

The game updates often. Anything we reference by name inside the game (class `PlayerCharacter`,
field `cameraTransform`, method `HouseNetworkManager.OnStopClient`) can move. The community
keeps notes; the ones this project leaned on are in
[dougwithseismic/bigwalk-mods/docs/modding-guide.md](https://github.com/dougwithseismic/bigwalk-mods/blob/main/docs/modding-guide.md)
and [iameli/big-walk-practice/NOTES-phase1.md](https://github.com/iameli/big-walk-practice/blob/main/NOTES-phase1.md).

## 2. The toolchain, and what each piece does

**BepInEx 6 (bleeding edge, "Unity.IL2CPP" flavour)** is the mod loader. A `winhttp.dll`
dropped next to the game exe hijacks process start ("doorstop"), boots a .NET 6 CoreCLR
runtime *inside* the game process, and loads every `BepInEx/plugins/**/*.dll` that has a
class with `[BepInPlugin]` deriving from `BasePlugin`. Our code therefore runs as normal
.NET 6, side by side with the game's native IL2CPP runtime.

**Il2CppInterop** bridges the two runtimes. On first launch BepInEx runs Cpp2IL over the game
and generates *interop assemblies* into `BepInEx/interop/`: one proxy DLL per game assembly
(`Assembly-CSharp.dll`, `Mirror.dll`, `UnityEngine.CoreModule.dll`, ...). Each proxy class is a
thin wrapper around a native object pointer. You reference these DLLs at compile time and get
IntelliSense for the game's real types; at runtime every property get/set and method call is
marshalled into the native object. This is why:

- game/engine types feel like normal C#, but calls are slower than local C# (fine for our use);
- `internal` members of the game are exposed as `public` (useful: we use Mirror's internal
  `handlers` dictionary);
- managed generics over game types do not always work (you cannot register a Mirror message
  struct you defined; see ARCHITECTURE.md);
- your own `MonoBehaviour` subclasses must be registered with
  `ClassInjector.RegisterTypeInIl2Cpp<T>()` and need an `(IntPtr) : base(ptr)` constructor;
- managed arrays passed to Unity become `Il2CppStructArray<T>` / `Il2CppReferenceArray<T>`;
- interop assemblies are regenerated per game version, so every game update means rebuild.

**Harmony** patches game methods at runtime (prefix/postfix hooks). We use it only for tidy
teardown when the player leaves a lobby.

**Cpp2IL + ILSpy** (optional, for research) dump the game's type structure to browsable C#.
Bodies mostly come out as `throw null`; real logic needs the disassembly view. You will not need
this until you want to hook something new. The community guide above has the commands. Note:
`Il2CppDumper` cannot read this game's metadata version; use Cpp2IL.

**UnityExplorer** (a BepInEx plugin, IL2CPP build) gives you an in-game object inspector with a
C# REPL. Invaluable for answering "which shader does this material use" or "what is the name of
the player's camera transform" without decompiling anything. Install it alongside BigScreen when
you playtest.

**Mod managers (Gale, r2modman)** install BepInEx and mods into a *profile* folder under
`%APPDATA%` and launch the game with doorstop pointed at that profile, leaving the Steam install
vanilla. Most players use one; so does the build script, which looks for profiles first.

## 3. Dev loop

```text
edit code  ->  .\scripts\build.ps1 -Deploy  ->  launch game (from the manager)  ->  read BepInEx\LogOutput.log
```

- The game must be closed to deploy; it locks plugin DLLs.
- The first modded launch takes several minutes (interop generation). It looks frozen. It is not.
- `LogOutput.log` in the BepInEx folder is your console. `Plugin.Log.LogInfo(...)` goes there.
  Enable `Debug.Diagnostics` in the config for a status line every two seconds.
- Exceptions inside `Update()` are caught and logged by our controller so one bad frame does not
  kill the plugin; an exception in `Load()` prevents the plugin from loading at all.
- "Verify integrity of game files" in Steam removes a manually installed loader. It does not
  touch mod-manager profiles.
- Two-player testing: you need a second PC or a second Steam account (Steam Family Sharing
  works). The practice mod linked above can spawn extra bodies for solo route testing but cannot
  simulate a second *modded client*, which is what BigScreen's sync needs.

## 4. Anatomy of this plugin

```text
src/BigScreen/
  Plugin.cs               BepInEx entry point: config, class injection, Harmony hooks
  BigScreenController.cs  the one MonoBehaviour: Update/OnGUI, orchestrates everything
  Net/MirrorChannel.cs    raw send/receive on a private Mirror message id
  Net/Protocol.cs         wire format + SyncState (the shared truth)
  Net/SyncSession.cs      host/guest roles, hello handshake, broadcast, heartbeat
  Resolve/YtDlp.cs        page URL -> stream URL, off-thread, via yt-dlp.exe
  Video/*.cs              IVideoBackend + Unity VideoPlayer implementation
  World/ScreenObject.cs   the quad, its material, and the positional AudioSource
  UI/ControlPanel.cs      IMGUI window
  Util/MainThread.cs      background -> main thread hand-off
```

Rules of thumb baked into the code that you will want to keep:

- **Only touch Unity/IL2CPP objects on the main thread.** Background work posts results to
  `MainThread` and the controller drains it in `Update()`.
- **Poll, don't subscribe.** Subscribing to IL2CPP events from managed code needs delegate
  conversion and is fragile; `isPrepared`-style polling is boring and works.
- **Every game call sits inside a try/catch that logs.** Game updates rename things; we want a
  log line, not a dead plugin.
- **Nothing we create is a networked Mirror object.** The screen exists only on modded clients,
  built from received state. Vanilla clients cannot be affected by objects they never receive.

## 5. Publishing

Thunderstore is where Big Walk mods live; Gale and r2modman install from it.

1. Create an account and a *team* at thunderstore.io (the team name becomes the package
   prefix forever, e.g. `h223chen-BigScreen`).
2. Bump `<Version>` in `src/BigScreen/BigScreen.csproj` and `Plugin.Version` (they should match;
   Thunderstore refuses to re-upload an existing version).
3. `.\scripts\package.ps1` produces `dist\BigScreen-<version>.zip` with `manifest.json`,
   a 256x256 `icon.png`, `README.md` and the DLL under `BepInEx/plugins/BigScreen/`.
4. Upload at https://thunderstore.io/c/big-walk/create/, pick the team and the `Mods` category.

`thunderstore/manifest.json` declares the dependency on `BepInEx-BepInExPack_IL2CPP-6.0.755`,
the pack players actually get from their manager. Develop and test against the same pack.

## 6. Reading list

- BepInEx IL2CPP install & troubleshooting: https://docs.bepinex.dev/master/articles/user_guide/installation/unity_il2cpp.html
- Il2CppInterop docs (class injection, arrays, delegates): https://github.com/BepInEx/Il2CppInterop/tree/master/Documentation
- Harmony: https://harmony.pardeike.net/articles/patching.html
- Mirror networking manual (messages, NetworkTime): https://mirror-networking.gitbook.io/docs/
- Unity VideoPlayer: search "Unity Manual VideoPlayer component" for the current docs
- yt-dlp README (format selection, `--print`): https://github.com/yt-dlp/yt-dlp
- Big Walk mod community on Thunderstore: https://thunderstore.io/c/big-walk/
