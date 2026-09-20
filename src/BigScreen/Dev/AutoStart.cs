using System;
using BigScreen.Util;
using Mirror;
using UnityEngine;

namespace BigScreen.Dev;

/// <summary>
/// Development helper: host a save slot and load a video without clicking through the menus.
/// Off unless Dev.AutoHost or Dev.AutoLoadUrl is set.
///
/// This drives the game's own menu code, so it is the most likely part of the mod to break on
/// a game update. Everything is wrapped and logged; a failure here leaves you at the menu
/// rather than taking the game down.
///
/// The host path mirrors what the menus do:
///   SaveManager.GetAllSaveDatasInFolder() -> pick by slotName
///   HostMenuSelect.ActionSelectSaveData(save)   (falls back to SaveManager.instance.currentData)
///   PlayerCountSwapper.playerCount = ...
///   NetworkMinder.StartHost()
/// </summary>
internal static class AutoStart
{
    private static bool _hostDone;
    private static bool _loadDone;
    private static bool _placeDone;
    private static bool _spawnDone;
    private static float _menuSeenAt = -1f;
    private static float _sessionSeenAt = -1f;

    /// <summary>Called once per frame from the controller.</summary>
    public static void Tick(BigScreenController controller)
    {
        try
        {
            bool inSession = NetworkServer.active || NetworkClient.active;
            if (inSession)
            {
                _menuSeenAt = -1f;
                if (_sessionSeenAt < 0f) _sessionSeenAt = Time.unscaledTime;
                TickSpawn();
                TickAutoPlaceScreen(controller);
                TickAutoLoad(controller);
            }
            else
            {
                _sessionSeenAt = -1f;
                _loadDone = false;
                _placeDone = false;
                _spawnDone = false;
                TickAutoDismiss();
                TickAutoHost();
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"AutoStart failed: {e}");
            _hostDone = true;   // do not retry a throwing path every frame
        }
    }

    // --- Intro screens ----------------------------------------------------------------

    /// <summary>
    /// Clicks through the screens that stand between launching and the host menu. Each of
    /// these is the method the on-screen button calls, so this is the same path a player
    /// takes, not a shortcut around it.
    ///
    /// Without this an unattended run stops at the mic check forever.
    /// </summary>
    private static void TickAutoDismiss()
    {
        if (!Plugin.AutoDismissMenus.Value) return;

        try
        {
            var splash = UnityEngine.Object.FindObjectOfType<SplashMenu>();
            if (splash != null) { splash.ActionContinue(); return; }

            var mic = UnityEngine.Object.FindObjectOfType<MicCheckMenu>();
            if (mic != null) { mic.ActionContinue(); return; }

            // Only step into the host menu if we are actually going to host.
            if (!Plugin.AutoHost.Value) return;

            var title = UnityEngine.Object.FindObjectOfType<TitleMenu>();
            if (title != null) title.GoToHostMenu();
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"Auto-dismiss failed: {e.Message}");
        }
    }

    // --- Spawn position ---------------------------------------------------------------

    /// <summary>
    /// Parses "x,y,z" or "x,y,z,yaw". Returns false for anything else, including empty.
    ///
    /// The yaw is optional so a spawn recorded before this existed still works; without it
    /// the player keeps whatever direction they happened to be facing, which makes the
    /// auto-placed screen land somewhere different on every run.
    /// </summary>
    private static bool TryParsePose(string text, out Vector3 position, out float yaw, out bool hasYaw)
    {
        position = Vector3.zero;
        yaw = 0f;
        hasYaw = false;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split(',');
        if (parts.Length != 3 && parts.Length != 4) return false;

        var culture = System.Globalization.CultureInfo.InvariantCulture;
        const System.Globalization.NumberStyles Float = System.Globalization.NumberStyles.Float;
        if (!float.TryParse(parts[0].Trim(), Float, culture, out float x)) return false;
        if (!float.TryParse(parts[1].Trim(), Float, culture, out float y)) return false;
        if (!float.TryParse(parts[2].Trim(), Float, culture, out float z)) return false;
        if (parts.Length == 4)
        {
            if (!float.TryParse(parts[3].Trim(), Float, culture, out yaw)) return false;
            hasYaw = true;
        }

        position = new Vector3(x, y, z);
        return true;
    }

    /// <summary>
    /// Moves the player to the configured spawn once per session, using the game's own
    /// teleport rather than writing to the transform, so the character controller and the
    /// terrain checks stay consistent.
    /// </summary>
    private static void TickSpawn()
    {
        if (_spawnDone) return;
        if (!TryParsePose(Plugin.SpawnPosition.Value, out var target, out float yaw, out bool hasYaw)) { _spawnDone = true; return; }
        if (Time.unscaledTime - _sessionSeenAt < Plugin.AutoLoadDelay.Value) return;

        try
        {
            var pc = WorldManager.localPlayerCharacter;
            if (pc == null) return;           // world still loading

            var grease = pc.grease;
            if (grease == null) return;

            _spawnDone = true;
            var rotation = hasYaw ? Quaternion.Euler(0f, yaw, 0f) : pc.transform.rotation;
            grease.Teleport(target, rotation, true);
            Plugin.Log.LogInfo($"Spawn: teleported to ({target.x:F2}, {target.y:F2}, {target.z:F2})"
                               + (hasYaw ? $", yaw {yaw:F1}." : ", keeping current facing (no yaw recorded)."));
        }
        catch (Exception e)
        {
            _spawnDone = true;
            Plugin.Log.LogWarning($"Spawn teleport failed: {e.Message}");
        }
    }

    // --- Hosting ----------------------------------------------------------------------

    private static void TickAutoHost()
    {
        if (_hostDone || !Plugin.AutoHost.Value) return;

        // Wait for the menu to exist and settle. Hosting too early makes the game log
        // "Starting game without a valid state IsOnline : True InLobbyLocal : False" and
        // take a slow recovery path into the world.
        //
        // HostMenuSelect only exists once the main menu scene is loaded, so its presence is
        // a better ready signal than the managers alone, which come up much earlier.
        if (NetworkManager.singleton == null || SaveManager.instance == null) return;

        HostMenuSelect menu = null;
        try { menu = UnityEngine.Object.FindObjectOfType<HostMenuSelect>(); } catch { }
        if (menu == null) return;

        if (_menuSeenAt < 0f) { _menuSeenAt = Time.unscaledTime; return; }
        if (Time.unscaledTime - _menuSeenAt < Plugin.AutoHostDelay.Value) return;

        _hostDone = true;

        var save = FindSave(Plugin.AutoHostSaveSlot.Value);
        if (save == null) return;

        SelectSave(menu, save);
        SetPlayerCount(Plugin.AutoHostPlayerCount.Value);

        Plugin.Log.LogInfo($"Auto-host: starting '{save.slotName}'.");
        NetworkMinder.StartHost();
    }

    private static SaveData FindSave(string wantedName)
    {
        var saves = SaveManager.GetAllSaveDatasInFolder();
        if (saves == null || saves.Count == 0)
        {
            Plugin.Log.LogWarning("Auto-host: no save slots found.");
            return null;
        }

        var names = new System.Collections.Generic.List<string>();
        SaveData match = null;

        for (int i = 0; i < saves.Count; i++)
        {
            var s = saves[i];
            if (s == null) continue;
            string name = s.slotName ?? "";
            names.Add(name);

            // Empty config means "whatever was played last", which is the common case.
            if (string.IsNullOrWhiteSpace(wantedName))
            {
                if (match == null || s.lastPlayedTimeAsLong > match.lastPlayedTimeAsLong) match = s;
            }
            else if (string.Equals(name.Trim(), wantedName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                match = s;
            }
        }

        if (match == null)
            Plugin.Log.LogWarning($"Auto-host: no save slot named '{wantedName}'. Found: {string.Join(", ", names)}");

        return match;
    }

    /// <summary>
    /// Prefer the menu's own selection method so the game does its usual bookkeeping; fall back
    /// to setting the current save directly if that menu is not in the scene.
    /// </summary>
    private static void SelectSave(HostMenuSelect menu, SaveData save)
    {
        try
        {
            menu.ActionSelectSaveData(save);
            return;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"Auto-host: ActionSelectSaveData failed ({e.Message}); setting currentData instead.");
        }

        SaveManager.instance.currentData = save;
    }

    private static void SetPlayerCount(int count)
    {
        try
        {
            PlayerCountSwapper.playerCount = count switch
            {
                2 => PlayerCount.PlayerCount2,
                4 => PlayerCount.PlayerCount4,
                _ => PlayerCount.PlayerCount3,
            };
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"Auto-host: could not set the player count ({e.Message}); using the game's default.");
        }
    }

    // --- Loading a video --------------------------------------------------------------

    /// <summary>
    /// Puts the screen (and its console) at the recorded pose, without waiting for a video.
    ///
    /// Deliberately independent of AutoLoadUrl. With auto-load off - the normal way to work
    /// now that URLs are pasted at the console - nothing else would place a screen, and you
    /// would have to open the panel just to get something to paste at.
    /// </summary>
    private static void TickAutoPlaceScreen(BigScreenController controller)
    {
        if (_placeDone) return;
        if (!controller.Session.IsHost) return;
        if (Time.unscaledTime - _sessionSeenAt < Plugin.AutoLoadDelay.Value) return;

        if (controller.Session.State.HasScreen) { _placeDone = true; return; }

        // No recorded pose means nobody has chosen a spot, so leave placement to the player
        // rather than dropping a screen wherever they happen to be looking.
        if (!TryParsePose(Plugin.ScreenPose.Value, out var pos, out float yaw, out bool hasYaw) || !hasYaw)
        {
            _placeDone = true;
            return;
        }

        _placeDone = true;
        Plugin.Log.LogInfo("Auto-place: putting the screen at the recorded pose.");
        controller.PlaceScreenAt(pos, yaw);
    }

    private static void TickAutoLoad(BigScreenController controller)
    {
        if (_loadDone) return;

        string url = Plugin.AutoLoadUrl.Value;
        if (string.IsNullOrWhiteSpace(url)) { _loadDone = true; return; }

        // Only the host sets state, and only once the world is actually up.
        if (!controller.Session.IsHost) return;
        if (Time.unscaledTime - _sessionSeenAt < Plugin.AutoLoadDelay.Value) return;

        // The screen has to exist before a video means anything.
        if (!controller.Session.State.HasScreen)
        {
            // A recorded screen pose is exact and survives a restart. Falling back to
            // "4 m in front of the player" puts it somewhere different on every run: the
            // game's teleport does not set camera yaw, so where the player looks at spawn
            // is not ours to control.
            if (TryParsePose(Plugin.ScreenPose.Value, out var screenPos, out float screenYaw, out bool hasYaw) && hasYaw)
                controller.PlaceScreenAt(screenPos, screenYaw);
            else
                controller.UserPlaceScreen();
            return;
        }

        _loadDone = true;
        Plugin.Log.LogInfo($"Auto-load: {url}");
        controller.UserLoad(url.Trim());
    }
}
