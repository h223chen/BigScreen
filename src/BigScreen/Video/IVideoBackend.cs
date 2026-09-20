using System;
using UnityEngine;

namespace BigScreen.Video;

/// <summary>
/// Minimal playback surface the controller needs. Kept abstract so a second backend
/// (an ffmpeg pipe decoder, see docs/ARCHITECTURE.md) can be dropped in if Unity's
/// VideoPlayer turns out to be stripped from the game build or cannot play a stream.
/// </summary>
internal interface IVideoBackend : IDisposable
{
    /// <summary>The texture to put on the screen. Stable for the backend's lifetime.</summary>
    Texture Texture { get; }

    bool IsLoaded { get; }      // a URL has been given and prepare has started
    bool IsReady { get; }       // prepared: seek/play are meaningful
    bool IsPlaying { get; }
    double Time { get; }
    double Duration { get; }    // 0 when unknown
    string Error { get; }       // non-null once the backend gave up on the current URL

    /// <summary>
    /// Starts preparing a stream. <paramref name="knownDurationSeconds"/> is the length the
    /// resolver reported, or 0 if nobody knows; a backend may use it when the player itself
    /// does not report a length.
    /// </summary>
    void Load(string directUrl, double knownDurationSeconds);
    void Play();
    void Pause();
    void Seek(double seconds);
    void Stop();
    void SetVolume(float volume01);

    /// <summary>Called once per frame from the main thread.</summary>
    void Tick();
}
