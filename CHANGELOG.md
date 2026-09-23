# Changelog

## 1.0.0 - alpha

First version verified end to end in game: a YouTube link plays on a shared screen with
positional audio, driven from an in-world control podium.

### Added
- In-world control console beside the screen: skip back/forward 10 s, play/pause, and a
  paste bar that loads whatever YouTube link is on the clipboard. Aimed at and pressed with
  the game's own interact button, on mouse or controller.
- Status lamp on the console: grey idle, amber while resolving, green ready, red failed.
  Colour is the only feedback channel available - the mod ships no font.
- Screen positioning from the panel: raise/lower, and slide left/right/forward/back along
  the screen's own axes. Placement is remembered across restarts.
- `YtDlp.CookiesFromBrowser`, for when YouTube blocks anonymous requests with
  "Sign in to confirm you're not a bot".
- `Comfort.KeepAwake`, which holds off the game's idle sleep - the dimming that creeps in
  when you stand still - while a video is playing and you are within
  `Comfort.KeepAwakeRadius` (15 m) of the screen. Sleep still works normally away from the
  screen. Set it to `Always` to hold it off for the whole lobby, or `Off` for stock
  behaviour. Local only: it resets your own idle timer and changes nothing for other
  players.
- `Comfort.HideCrosshair`, which takes the crosshair off the picture once you have stood
  still for `Comfort.HideCrosshairDelaySeconds` (5 s), and brings it back the instant you move
  or look around. Same conditions as `KeepAwake`: only in front of a playing screen, and only
  within `Comfort.KeepAwakeRadius`. Set the delay to 0 to follow the game's own sleep timer
  instead.

### Changed
- Audio falls off as 1/distance rescaled to reach silence at `MaxDistance`, instead of
  linear. Standing 4 m from the screen was previously 97% of full volume.
- Guests with the mod can control playback by default.

### Known limitations
- YouTube serves only one muxed format to the client yt-dlp can still reach, so playback is
  capped at 360p. Higher resolutions are separate video and audio streams, which Unity's
  VideoPlayer cannot take. Lifting this needs the ffmpeg backend in `docs/ARCHITECTURE.md`.
- Two modded players staying in sync has not been tested; there has only ever been one
  machine. See milestone 3 in `docs/HANDOFF.md`.

## 0.1.0 (unreleased)

- Initial scaffold: BepInEx 6 IL2CPP plugin, Mirror-based sync channel, yt-dlp resolver,
  Unity VideoPlayer backend, in-world screen with positional audio, IMGUI control panel.
- Not yet verified in-game. See docs/PLAYTEST.md.
