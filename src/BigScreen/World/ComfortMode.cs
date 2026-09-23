namespace BigScreen.World;

/// <summary>
/// When one of the comfort tweaks applies. Shared by <see cref="SleepGuard"/> and
/// <see cref="CrosshairGuard"/> so the two settings read the same way.
/// </summary>
internal enum ComfortMode
{
    /// <summary>Stock behaviour; the mod leaves this alone.</summary>
    Off,

    /// <summary>Only while a video is playing and you are standing near the screen.</summary>
    WhileWatching,

    /// <summary>Whenever you are in a lobby, screen or not.</summary>
    Always,
}
