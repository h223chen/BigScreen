using System;
using Rewired;

namespace BigScreen.Util;

/// <summary>
/// Reads the game's own "interact" button through Rewired.
///
/// Big Walk routes input through Rewired, not Unity's legacy Input class. Legacy Input sees
/// the mouse, so left click worked, but it never sees a gamepad the way Rewired has it bound -
/// which is why the controller's interact button (left bumper by default) did nothing. Asking
/// Rewired for the action instead of polling a hardcoded button index means the console
/// responds to whatever the player has actually bound, on whatever device, including rebinds.
///
/// Everything here is defensive. Rewired throws rather than returning false when it is not
/// initialised - the game logs "Rewired is not initialized" during startup - so every call is
/// guarded and any failure degrades to "no press" rather than taking a frame down with it.
/// </summary>
internal static class GameInput
{
    // Action names to try when nothing is configured, best guess first. Rewired matches by
    // exact name, so this is a short list of what an interact action is plausibly called.
    // Big Walk calls it "use" (verified from the action list it reports at runtime). The
    // rest are fallbacks in case a game update renames it.
    private static readonly string[] CandidateActions =
    {
        "use", "Use", "interact", "Interact", "Grab", "grab",
        "Action", "UseItem", "Interaction", "Select", "Confirm",
    };

    private static bool _resolved;
    private static string _actionName;
    private static bool _loggedUnavailable;

    /// <summary>True on the frame the game's interact button is pressed, on any device.</summary>
    public static bool InteractPressed()
    {
        try
        {
            if (!ReInput.isReady) return false;

            if (!_resolved) Resolve();
            if (_actionName == null) return false;

            // Player 0 is the local player; this mod never deals with local splitscreen.
            var player = ReInput.players.GetPlayer(0);
            return player != null && player.GetButtonDown(_actionName);
        }
        catch (Exception e)
        {
            if (!_loggedUnavailable)
            {
                _loggedUnavailable = true;
                Plugin.Log.LogWarning($"Rewired input unavailable ({e.Message}); the console still responds to left click.");
            }
            return false;
        }
    }

    /// <summary>
    /// Works out what the interact action is called, once. Logs every action the game defines
    /// so the right name can be pinned in the config if the guess is wrong.
    /// </summary>
    private static void Resolve()
    {
        _resolved = true;

        var configured = Plugin.RewiredAction.Value;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            _actionName = configured.Trim();
            Plugin.Log.LogInfo($"Console: using configured Rewired action '{_actionName}'.");
            return;
        }

        LogAvailableActions();

        foreach (var name in CandidateActions)
        {
            try
            {
                if (ReInput.mapping.GetAction(name) == null) continue;
                _actionName = name;
                Plugin.Log.LogInfo($"Console: bound to Rewired action '{name}' for controller input.");
                return;
            }
            catch { }
        }

        Plugin.Log.LogWarning("Console: no Rewired interact action matched. Left click still works; " +
                              "set Keys.RewiredInteractAction to one of the actions listed above.");
    }

    /// <summary>
    /// Dumps the game's action names. Rewired's action list is data, not code, so this is the
    /// only way to learn the vocabulary; it runs once and is cheap.
    ///
    /// Walks action ids rather than the Actions collection: that collection comes across as an
    /// Il2Cpp IList proxy with no usable Count, whereas GetAction(int) is an ordinary call.
    /// Ids are small and contiguous in practice, so scanning a fixed range covers them.
    /// </summary>
    private static void LogAvailableActions()
    {
        const int MaxActionId = 128;

        try
        {
            var names = new System.Collections.Generic.List<string>();
            for (int id = 0; id < MaxActionId; id++)
            {
                InputAction a = null;
                try { a = ReInput.mapping.GetAction(id); } catch { }
                if (a != null && !string.IsNullOrEmpty(a.name)) names.Add($"{id}:{a.name}");
            }
            if (names.Count > 0)
                Plugin.Log.LogInfo($"Rewired actions ({names.Count}): {string.Join(", ", names)}");
            else
                Plugin.Log.LogInfo("Rewired reported no actions; the console stays on left click.");
        }
        catch (Exception e)
        {
            Plugin.Log.LogDebug($"Could not list Rewired actions: {e.Message}");
        }
    }
}
