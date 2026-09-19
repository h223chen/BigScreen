using System;
using System.Collections.Concurrent;

namespace BigScreen.Util;

/// <summary>
/// Anything that touches Unity or IL2CPP objects must run on the game's main thread.
/// Background work (yt-dlp, downloads) posts its results here and the controller drains
/// the queue once per frame.
/// </summary>
internal static class MainThread
{
    private static readonly ConcurrentQueue<Action> Queue = new();

    public static void Post(Action action)
    {
        if (action != null) Queue.Enqueue(action);
    }

    public static void Drain()
    {
        while (Queue.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception e) { Plugin.Log.LogError($"Main-thread task failed: {e}"); }
        }
    }
}
