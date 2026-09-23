# BigScreen

Watch YouTube together in Big Walk. Put a big screen anywhere on the island, paste a link,
and everyone in your lobby who has the mod sees and hears the same video at the same moment.
The sound comes from the screen, so it gets quieter as you walk away.

> **Alpha.** Video plays at up to 360p for now. Syncing between several players is the
> least-tested part, so please report anything odd (see *Reporting problems* below).

## Who needs to install it

- **The host must have it.** The host keeps everyone's screen in sync.
- **Every guest who wants to watch must have it.**
- Players without the mod can still join. They see and hear nothing, and nothing breaks for them.

Works on the Steam version of Big Walk, on Windows or on Linux/Steam Deck through Proton.
Not available on macOS, PlayStation or Switch.

## Install with a mod manager (recommended)

1. Install [Gale](https://github.com/Kesomannen/gale) or [r2modman](https://thunderstore.io/package/ebkr/r2modman/).
2. Choose **Big Walk** and create or select a profile.
3. Search for **BigScreen** and install it. The mod manager installs BepInEx for you.
4. Launch the game from the mod manager, not from Steam. In r2modman the button is
   **Start modded**. In Gale it is **Launch game**.

The first modded launch takes a few minutes while BepInEx sets itself up. The game looks
frozen during this. Let it finish.

## Install manually

1. Download **BepInExPack IL2CPP** from its
   [Thunderstore page](https://thunderstore.io/c/big-walk/p/BepInEx/BepInExPack_IL2CPP/)
   with the **Manual Download** button.
2. Open the zip and copy everything inside its `BepInExPack` folder into your Big Walk folder,
   next to `Big Walk.exe`. To find that folder, right-click Big Walk in Steam, then
   **Manage > Browse local files**.
3. Linux and Steam Deck only: in Steam, open Big Walk's **Properties** and set the launch option
   to `WINEDLLOVERRIDES="winhttp=n,b" %command%`.
4. Start the game once and wait for it to reach the main menu. This first launch takes a few
   minutes. Then quit.
5. Download BigScreen and copy the `BepInEx` folder from the zip into your Big Walk folder.
   You should end up with `Big Walk\BepInEx\plugins\BigScreen\BigScreen.dll`.
6. Start the game normally from Steam.

To uninstall, delete the `BigScreen` folder inside `BepInEx\plugins`. To remove all mods,
use Steam's **Verify integrity of game files**.

## How to use

1. Host or join a walk.
2. Press **F8** to open the BigScreen panel.
3. Click **Place screen here**. A screen appears a few steps in front of you, with a small
   control podium beside it.
4. Copy a YouTube link in your browser. Walk up to the podium, look at the long **paste bar**
   and left-click it. The video loads for everyone a few seconds later.

The podium has **skip back 10 s**, **play/pause** and **skip forward 10 s** buttons. Look at a
button and left-click it, or press your controller's interact button. The lamp on the podium
shows what is happening:

| Lamp | Meaning |
| --- | --- |
| Grey | Nothing loaded |
| Amber | Loading the video |
| Green | Ready or playing |
| Red | Something went wrong. Open the F8 panel to read the error. |

The F8 panel does everything the podium does, and more. You can type a link and press
**Load**, seek in bigger steps, move or raise the screen, and set your own volume. Your volume
only affects you. The host can turn off guest control in the panel.

The first time anyone loads a video, BigScreen downloads **yt-dlp** (about 18 MB) from its
official GitHub page. yt-dlp is the free tool that finds the video stream on YouTube.

## Troubleshooting

**Nothing happens when I press F8.** The mod is not loading. Make sure you started the game
through your mod manager, not from Steam. For a manual install, check that
`BepInEx\LogOutput.log` exists in the game folder and contains a line with `BigScreen`.

**Red lamp, "Requested format is not available".** YouTube changed something. Open the F8
panel, click **Update yt-dlp**, then load the video again.

**Red lamp, "Sign in to confirm you're not a bot".** YouTube is blocking anonymous requests.
Open `BepInEx\config\dev.h223chen.bigscreen.cfg` and set `CookiesFromBrowser` to the browser
you use for YouTube, for example `CookiesFromBrowser = firefox`. Restart the game. yt-dlp then
uses your normal YouTube login, and nothing is sent anywhere else.

**yt-dlp fails to download.** A firewall or antivirus may be blocking it. Download `yt-dlp.exe`
yourself from [yt-dlp's releases page](https://github.com/yt-dlp/yt-dlp/releases/latest) and
put it in the same folder as `BigScreen.dll`.

**My friend's screen is out of sync or missing.** Check that the host has BigScreen installed
and that everyone is on the same BigScreen version. Leaving and rejoining the lobby resyncs.

**It is too loud or too quiet across the camp.** Use your volume slider in the F8 panel. To
change how far the sound carries, edit `MaxDistance` and `FullVolumeRadius` in the config file.

## Settings

Settings live in `BepInEx\config\dev.h223chen.bigscreen.cfg`, created after the first launch.
With a mod manager, open it from the manager's config editor. Useful ones:

| Setting | Default | What it does |
| --- | --- | --- |
| `ToggleUI` | F8 | Key that opens the panel |
| `Volume` | 0.8 | Your own volume |
| `MaxDistance` | 25 | Meters at which the screen goes silent |
| `FullVolumeRadius` | 1.5 | Meters within which the screen plays at full volume |
| `WidthMeters` | 4 | Screen size |
| `ShowConsole` | true | Show the control podium next to the screen |
| `GuestsCanControl` | true | Host only: let guests with the mod control playback |
| `CookiesFromBrowser` | empty | Browser to borrow YouTube login from, if YouTube blocks you |

## Known limitations

- Videos play at up to 360p. Higher quality needs a new video decoder and is planned.
- Live streams are untested.
- The screen and podium are only visible to players with the mod.

## Reporting problems

Open an issue on [GitHub](https://github.com/h223chen/BigScreen/issues). Please attach
`BepInEx\LogOutput.log` from the game folder or your mod manager profile, and say whether you
were the host or a guest.

Not affiliated with House House or Panic. Uses [yt-dlp](https://github.com/yt-dlp/yt-dlp).
Source code is MIT licensed.
