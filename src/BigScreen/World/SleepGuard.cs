using System;
using UnityEngine;

namespace BigScreen.World;

/// <summary>
/// Keeps the idle "sleep" from creeping in while you stand still watching the screen.
///
/// Big Walk sends a player to sleep after PlayerSleeper.timeTilSleep seconds without input,
/// which dims the view. That is right when someone has wandered off from the keyboard and
/// wrong when they are deliberately standing still watching a video.
///
/// We reset the game's own idle timer through PlayerSleeper.RecordAction - the same call real
/// input makes - rather than holding its preventSleeping flag down. Nothing has to be put back
/// afterwards: stop calling it and the timer simply resumes from that moment, so an uninstalled
/// mod, a thrown exception or a renamed member all degrade to stock behaviour instead of
/// leaving a player who can never sleep again. It also leaves forceSleeping alone, so scripted
/// sleep still happens.
/// </summary>
internal static class SleepGuard
{
    /// <summary>Whether the last tick held the idle timer back. Reported on the diag line.</summary>
    internal static bool Suppressing { get; private set; }

    // RecordAction only has to land more often than timeTilSleep, which is tens of seconds.
    // Twice a second is plenty and keeps the interop traffic off the per-frame path.
    private const float IntervalSeconds = 0.5f;
    private static float _nextTick;

    // The member names here come from the game's assembly, so a game update can remove them.
    // One warning is enough; after that we go quiet and leave sleep alone.
    private static bool _warned;

    internal static void Reset()
    {
        Suppressing = false;
        _nextTick = 0f;
    }

    /// <param name="screenPosition">Where the screen stands. Ignored unless <paramref name="haveScreen"/>.</param>
    /// <param name="haveScreen">Whether a screen exists in the world right now.</param>
    /// <param name="playing">Whether the shared state says the video is playing.</param>
    internal static void Tick(Vector3 screenPosition, bool haveScreen, bool playing)
    {
        try
        {
            ComfortMode mode = Plugin.KeepAwake.Value;
            if (mode == ComfortMode.Off) { Suppressing = false; return; }

            if (mode == ComfortMode.WhileWatching && !IsWatching(screenPosition, haveScreen, playing))
            {
                Suppressing = false;
                return;
            }

            Suppressing = true;
            if (Time.unscaledTime < _nextTick) return;
            _nextTick = Time.unscaledTime + IntervalSeconds;
            PokeIdleTimer();
        }
        catch (Exception e)
        {
            Suppressing = false;
            if (!_warned)
            {
                _warned = true;
                Plugin.Log.LogWarning($"Keep-awake disabled; the sleep timer could not be reached: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Near the screen with something playing on it. Distance is to the screen's centre, so
    /// the radius is a sphere around it rather than a viewing cone - standing off to one side
    /// still counts, which is what you want for something people listen to as much as watch.
    /// </summary>
    internal static bool IsWatching(Vector3 screenPosition, bool haveScreen, bool playing)
    {
        if (!haveScreen || !playing) return false;
        if (!BigScreenController.TryGetLocalPlayer(out var pc)) return false;

        float radius = Plugin.KeepAwakeRadius.Value;
        return (pc.transform.position - screenPosition).sqrMagnitude <= radius * radius;
    }

    private static void PokeIdleTimer()
    {
        if (!BigScreenController.TryGetLocalPlayer(out var pc)) return;

        var sleeper = pc.sleeper;
        if (sleeper == null) return;

        // Already asleep for a scripted reason: leave it be, resetting the idle timer would
        // not clear forceSleeping anyway.
        if (sleeper.forceSleeping) return;

        sleeper.RecordAction();
    }
}
