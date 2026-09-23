# BigScreen

Watch YouTube together in Big Walk. The host gets a big screen in the starting area, anyone
can paste a link, and everyone in your lobby who has the mod sees and hears the same video at
the same moment. The sound comes from the screen, so it gets quieter as you walk away.

> **Alpha.** Video plays at up to 360p for now. Syncing has been tested with two players, so
> please report anything odd (see *Reporting problems* below).

## Who needs to install it

- **The host must have it.** The host keeps everyone's screen in sync.
- **Every guest who wants to watch must have it.**
- Players without the mod can still join. They see and hear nothing, and nothing breaks for them.

Tested only on the Steam version of Big Walk, on Windows.

## Install with Gale (recommended)

1. Install [Gale](https://github.com/Kesomannen/gale).
2. Choose **Big Walk** and create or select a profile.
3. Search for **BigScreen** and install it. Gale installs BepInEx for you.
4. Launch the game from Gale with **Launch Modded**, not from Steam.

Other Thunderstore mod managers such as r2modman may also work, but have not been tested.

The first modded launch takes a few minutes while BepInEx sets itself up. The game looks
frozen during this. Let it finish.

## Install manually

These are the standard BepInEx steps. They have not been tested with BigScreen; installing
through Gale is the tested route.

1. Download **BepInExPack IL2CPP** from its
   [Thunderstore page](https://thunderstore.io/c/big-walk/p/BepInEx/BepInExPack_IL2CPP/)
   with the **Manual Download** button.
2. Open the zip and copy everything inside its `BepInExPack` folder into your Big Walk folder,
   next to `Big Walk.exe`. To find that folder, right-click Big Walk in Steam, then
   **Manage > Browse local files**.
3. Start the game once and wait for it to reach the main menu. This first launch takes a few
   minutes. Then quit.
4. Download BigScreen and copy the `BepInEx` folder from the zip into your Big Walk folder.
   You should end up with `Big Walk\BepInEx\plugins\BigScreen\BigScreen.dll`.
5. Start the game normally from Steam.

To uninstall, delete the `BigScreen` folder inside `BepInEx\plugins`.

## How to use

1. Host or join a walk. A few seconds after the host arrives, a screen appears in the starting
   area with a small control podium beside it.
2. Copy a YouTube link in your browser. Walk up to the podium, look at the long **paste bar**
   and left-click it. The video loads for everyone a few seconds later.

The podium has **skip back 10 s**, **play/pause** and **skip forward 10 s** buttons. Look at a
button and left-click it, or press your controller's interact button. The lamp on the podium
shows what is happening:

| Lamp | Meaning |
| --- | --- |
| Grey | Nothing loaded |
| Amber | Loading the video |
| Green | Ready or playing |
| Red | Something went wrong. Press **F8** to open the panel and read the error. |

Press **F8** to open the BigScreen panel. It does everything the podium does, and more. You can
type a link and press **Load**, seek in bigger steps, move the screen to where you are standing
with **Move screen here**, and set your own volume. Your volume only affects you. The host can
also raise or lower the screen, and can turn off guest control. The host's screen comes back at
the same spot the next time they host.

The first time you watch a video, BigScreen downloads **yt-dlp** from its official GitHub page.
yt-dlp is the free tool that finds the video stream on YouTube. Each player's game does this
for itself.

## Troubleshooting

**Nothing happens when I press F8.** The mod is not loading. Make sure you started the game
with **Launch Modded** in Gale, not from Steam. For a manual install, check that
`BepInEx\LogOutput.log` exists in the game folder and contains a line with `BigScreen`.

**Red lamp, "Requested format is not available".** YouTube may have changed something. Open the
F8 panel, click **Update yt-dlp**, then load the video again. If that does not help, please
report it.

**Red lamp, "Sign in to confirm you're not a bot".** YouTube is blocking anonymous requests.
Open `BepInEx\config\dev.h223chen.bigscreen.cfg` and set `CookiesFromBrowser` to the browser
you use for YouTube, for example `CookiesFromBrowser = firefox`, then restart the game. yt-dlp
then requests the video using your YouTube login from that browser. This has not been tested
yet, so please report whether it works for you.

**yt-dlp fails to download.** A firewall or antivirus may be blocking it. Download `yt-dlp.exe`
yourself from [yt-dlp's releases page](https://github.com/yt-dlp/yt-dlp/releases/latest) and
put it in the same folder as `BigScreen.dll`.

**My friend's screen is out of sync or missing.** Check that the host has BigScreen installed
and that everyone is on the same BigScreen version. Leaving and rejoining the lobby resyncs.

**It is too loud or too quiet across the camp.** Use your volume slider in the F8 panel. To
change how far the sound carries, edit `MaxDistance` and `FullVolumeRadius` in the config file.

## Settings

Settings live in `BepInEx\config\dev.h223chen.bigscreen.cfg`, created after the first launch.
In Gale you can open it from the config editor. Useful ones:

| Setting | Default | What it does |
| --- | --- | --- |
| `ToggleUI` | F8 | Key that opens the panel |
| `Volume` | 0.8 | Your own volume |
| `MaxDistance` | 25 | Meters at which the screen goes silent |
| `FullVolumeRadius` | 1.5 | Meters within which the screen plays at full volume |
| `WidthMeters` | 4 | Screen width in meters. Everyone sees the host's value. |
| `ShowConsole` | true | Show the control podium next to the screen |
| `GuestsCanControl` | true | Host only: let guests with the mod control playback |
| `KeepAwake` | WhileWatching | Stop the game putting you to sleep while you watch |
| `HideCrosshair` | WhileWatching | Hide the crosshair while you stand still watching |
| `CookiesFromBrowser` | empty | Browser to borrow your YouTube login from, if YouTube blocks you |

## Known limitations

- Videos play at up to 360p. Higher quality needs a new video decoder and is planned.
- Live streams are untested.
- The screen and podium are only visible to players with the mod.

## Reporting problems

Open an issue on [GitHub](https://github.com/h223chen/BigScreen/issues). Please attach
`BepInEx\LogOutput.log` from the game folder or your Gale profile, and say whether you were the
host or a guest.

Not affiliated with House House or Panic. Uses [yt-dlp](https://github.com/yt-dlp/yt-dlp).
Source code is MIT licensed.
