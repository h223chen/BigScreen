using System;
using UnityEngine;

namespace BigScreen.World;

/// <summary>
/// Fades the crosshair out of the way once you have stood still long enough, and brings it
/// straight back the moment you move.
///
/// This is the visual half of <see cref="SleepGuard"/>. Standing still to watch something is
/// exactly when a dot in the middle of the picture is most annoying, and it is also when the
/// crosshair is least useful - you are not aiming at anything.
///
/// The delay is short (5 s by default) rather than matching the sleep timer: this only applies
/// while you are stood in front of a playing screen, and the crosshair returns the instant you
/// move, so there is nothing to be gained by making you wait. Setting the delay to 0 follows
/// PlayerSleeper.timeTilSleep instead, for parity with when sleep would have taken over.
///
/// We cannot ask the game how long it has been since you moved, because SleepGuard is busy
/// resetting that very timer. So we watch the player's own position and look direction and
/// keep our own idle clock.
/// </summary>
internal static class CrosshairGuard
{
    /// <summary>Whether the crosshair is hidden by us right now. Reported on the diag line.</summary>
    internal static bool Hidden { get; private set; }

    /// <summary>
    /// The idle delay currently in force, for the diag line. Without this there is no way to
    /// tell "the crosshair is broken" from "you have not stood still for long enough yet".
    /// </summary>
    internal static float DelaySeconds
    {
        get { try { return HideDelay(); } catch { return -1f; } }
    }

    /// <summary>
    /// Where <see cref="DelaySeconds"/> came from: the config, the game's own timeTilSleep, or
    /// the fallback. The fallback is worth telling apart - it means we never managed to read
    /// the game's value, so "matches the sleep delay" is not actually being honoured.
    /// </summary>
    internal static string DelaySource => _delaySource;
    private static string _delaySource = "?";

    /// <summary>Seconds since the player last moved, looked around or pressed anything.</summary>
    internal static float SecondsIdle => _lastMoveAt < 0f ? 0f : Time.unscaledTime - _lastMoveAt;

    // Enough to ignore controller stick drift and the sub-millimetre jitter of standing on
    // uneven ground, while still catching a deliberate nudge.
    private const float MovedMeters = 0.03f;
    private const float LookedDegrees = 0.15f;

    // Used only if the game's own timeTilSleep cannot be read.
    private const float FallbackDelaySeconds = 30f;

    private static Vector3 _lastPosition;
    private static Vector3 _lastForward;
    private static bool _havePose;
    private static float _lastMoveAt = -1f;

    // Only ever restore what we actually hid, so a crosshair the game hid for its own reasons
    // is not switched back on by us.
    private static bool _hiddenByUs;
    private static bool _warned;

    internal static void Reset()
    {
        try { Show(); } catch { /* leaving the lobby; nothing useful to do */ }
        Hidden = false;
        _hiddenByUs = false;
        _havePose = false;
        _lastMoveAt = -1f;
    }

    internal static void Tick(Vector3 screenPosition, bool haveScreen, bool playing)
    {
        try
        {
            ComfortMode mode = Plugin.HideCrosshair.Value;
            if (mode == ComfortMode.Off ||
                (mode == ComfortMode.WhileWatching && !SleepGuard.IsWatching(screenPosition, haveScreen, playing)))
            {
                Show();
                return;
            }

            // Moving resets the clock and brings the crosshair back the same frame, which is
            // the whole point: it has to be there before you need to aim with it.
            if (PlayerMoved())
            {
                _lastMoveAt = Time.unscaledTime;
                Show();
                return;
            }

            if (_lastMoveAt < 0f) { _lastMoveAt = Time.unscaledTime; return; }
            if (Time.unscaledTime - _lastMoveAt >= HideDelay()) Hide();
        }
        catch (Exception e)
        {
            if (!_warned)
            {
                _warned = true;
                Plugin.Log.LogWarning($"Crosshair hiding disabled; it could not be reached: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Position or look direction changed, or a key is down. Watching the pose rather than the
    /// input devices means a controller, a mouse and a shove from another player all count,
    /// without caring which input system produced them.
    /// </summary>
    private static bool PlayerMoved()
    {
        if (!BigScreenController.TryGetLocalPlayer(out var pc)) return false;

        var position = pc.transform.position;
        var cam = pc.cameraTransform != null ? pc.cameraTransform : pc.transform;
        var forward = cam.forward;

        if (!_havePose)
        {
            _havePose = true;
            _lastPosition = position;
            _lastForward = forward;
            return true;
        }

        bool moved = (position - _lastPosition).sqrMagnitude > MovedMeters * MovedMeters
                     || Vector3.Angle(_lastForward, forward) > LookedDegrees
                     || Input.anyKey;

        _lastPosition = position;
        _lastForward = forward;
        return moved;
    }

    /// <summary>
    /// The configured delay, or the game's own sleep delay when that is left at 0. Read fresh
    /// rather than cached: it is a per-character value and costs one field read a frame.
    /// </summary>
    private static float HideDelay()
    {
        float configured = Plugin.HideCrosshairDelay.Value;
        if (configured > 0f) { _delaySource = "cfg"; return configured; }

        if (BigScreenController.TryGetLocalPlayer(out var pc))
        {
            var sleeper = pc.sleeper;
            if (sleeper != null && sleeper.timeTilSleep > 0f) { _delaySource = "game"; return sleeper.timeTilSleep; }
        }
        _delaySource = "fallback";
        return FallbackDelaySeconds;
    }

    private static void Hide()
    {
        if (Hidden) return;
        var target = HideTarget();
        if (target == null || !target.activeSelf) return;

        DescribeTargetOnce(target);
        target.SetActive(false);
        Hidden = true;
        _hiddenByUs = true;
    }

    // hideTransform is a name from the game's assembly, not a contract. If it turns out to be
    // something other than the parent of the crosshair graphics, this line is what says so.
    private static bool _described;

    private static void DescribeTargetOnce(GameObject target)
    {
        if (_described || !Plugin.Diagnostics.Value) return;
        _described = true;
        var parent = target.transform.parent;
        Plugin.Log.LogInfo($"[diag] hiding crosshair via '{target.name}' " +
                           $"(parent='{(parent != null ? parent.name : "none")}', " +
                           $"children={target.transform.childCount})");
    }

    private static void Show()
    {
        if (!Hidden) return;
        Hidden = false;

        if (!_hiddenByUs) return;
        _hiddenByUs = false;

        var target = HideTarget();
        if (target != null) target.SetActive(true);
    }

    /// <summary>
    /// The game's own "hide the crosshair" transform, which parents the normal, holding and
    /// windup variants. Toggling that one object leaves every crosshair state consistent,
    /// where disabling the Crosshair component would freeze it mid-animation.
    /// </summary>
    private static GameObject HideTarget()
    {
        var crosshair = Crosshair.instance;
        if (crosshair == null) return null;

        var hide = crosshair.hideTransform;
        return hide != null ? hide.gameObject : null;
    }
}
