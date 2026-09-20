using System;
using UnityEngine;
using UnityEngine.Bindings;
using UnityEngine.Video;

namespace BigScreen.Video;

/// <summary>
/// Playback through Unity's built-in VideoPlayer, rendering into a RenderTexture and
/// routing audio through a 3D AudioSource so it is positional in the world.
///
/// On Windows the VideoPlayer decodes via Media Foundation, which handles a progressive
/// H.264/AAC MP4 over HTTP (what yt-dlp's format 18 / "best[ext=mp4][acodec!=none]" gives
/// us). It does NOT handle HLS/DASH manifests, WebM/VP9 over HTTP, or separate audio and
/// video streams - hence the strict format selector in the config.
///
/// Events on IL2CPP objects are awkward to subscribe to from managed code, so instead of
/// prepareCompleted/errorReceived we poll isPrepared with a timeout.
/// </summary>
internal sealed class UnityVideoBackend : IVideoBackend
{
    private readonly GameObject _host;
    private readonly AudioSource _audio;
    private readonly RenderTexture _rt;
    private VideoPlayer _player;

    private bool _loaded;
    private bool _wasReady;
    private float _prepareStarted;
    private float _volume = 1f;
    private string _error;

    private const float PrepareTimeoutSeconds = 45f;

    public UnityVideoBackend(GameObject host, AudioSource audio, RenderTexture target)
    {
        _host = host;
        _audio = audio;
        _rt = target;
    }

    public Texture Texture => _rt;
    public bool IsLoaded => _loaded;
    public bool IsReady => _loaded && _error == null && _player != null && _player.isPrepared;
    public bool IsPlaying => IsReady && _player.isPlaying;
    public double Time => IsReady ? _player.time : 0.0;
    public double Duration => IsReady ? _player.length : 0.0;
    public string Error => _error;

    public void Load(string directUrl)
    {
        Stop();
        _error = null;

        try
        {
            Util.Trace.Write("Load enter");
            _player = _host.GetComponent<VideoPlayer>();
            if (_player == null) _player = _host.AddComponent<VideoPlayer>();

            Util.Trace.Write("Load: VideoPlayer component ready");
            _player.playOnAwake = false;
            _player.isLooping = false;
            _player.skipOnDrop = true;
            _player.waitForFirstFrame = true;
            _player.renderMode = VideoRenderMode.RenderTexture;
            _player.targetTexture = _rt;
            _player.aspectRatio = VideoAspectRatio.FitInside;

            // Audio: hand track 0 to our positional AudioSource. Must be configured before Prepare().
            Util.Trace.Write("Load: value setters done, audio next");
            _player.audioOutputMode = VideoAudioOutputMode.AudioSource;
            _player.controlledAudioTrackCount = 1;
            _player.EnableAudioTrack(0, true);
            _player.SetTargetAudioSource(0, _audio);
            _audio.volume = _volume;

            Util.Trace.Write("Load: audio wired, source next");
            _player.source = VideoSource.Url;
            Util.Trace.Write("VideoPlayer: before SetUrl");
            SetUrl(_player, directUrl);
            Util.Trace.Write("VideoPlayer: SetUrl returned, before Prepare");
            _player.Prepare();

            _loaded = true;
            _wasReady = false;
            _prepareStarted = UnityEngine.Time.unscaledTime;
            Util.Trace.Write("VideoPlayer: Prepare returned"); Plugin.Log.LogInfo("VideoPlayer preparing stream...");
        }
        catch (Exception e)
        {
            _error = "VideoPlayer setup failed: " + e.Message;
            Plugin.Log.LogError(_error + "\n" + e);
            _loaded = false;
        }
    }

    // Setting VideoPlayer.url through the interop proxy throws on Unity 6:
    //
    //   MissingMethodException: Method not found:
    //     '!0 ByRef Il2CppSystem.ReadOnlySpan`1.GetPinnableReference()'
    //     at UnityEngine.Video.VideoPlayer.set_url(String value)
    //
    // Unity 6 binds a string property in two halves: a managed set_url that packs the string
    // into a ManagedSpanWrapper, and set_url_Injected that takes that struct and does the real
    // work. Only the packing half is broken - it calls ReadOnlySpan.GetPinnableReference, which
    // Il2CppSystem.ReadOnlySpan does not have. The injected half is fine.
    //
    // So pack the struct here and call the injected method. Il2CppInterop generates it as a
    // public static method taking the instance pointer, so this is an ordinary C# call - no
    // method lookup and no il2cpp_runtime_invoke. ManagedSpanWrapper wants UTF-16 characters,
    // which is exactly what a pinned C# string is.
    //
    // Any other Unity string setter can hit the same problem; the same fix applies.
    // The first attempt passed player.Pointer as _unity_self and killed the process with an
    // access violation. That is the IL2CPP managed object pointer; Unity's injected bindings
    // want the native C++ object, which UnityEngine.Object keeps in m_CachedPtr. The interop
    // assembly has a proxy for the private GetCachedPtr(), so ask it rather than reading the
    // field by offset.
    private static unsafe void SetUrl(VideoPlayer player, string url)
    {
        url ??= "";

        // Il2CppInterop regenerates every member as public, so this is a direct call.
        IntPtr self = player.GetCachedPtr();
        Util.Trace.Write($"SetUrl: native=0x{self.ToInt64():x} managed=0x{player.Pointer.ToInt64():x} len={url.Length}");

        // A destroyed Unity object has a null native pointer, and handing that to the binding
        // is another crash rather than an exception.
        if (self == IntPtr.Zero)
            throw new InvalidOperationException("The VideoPlayer's native object is gone.");

        fixed (char* chars = url)
        {
            var span = new ManagedSpanWrapper(chars, url.Length);
            VideoPlayer.set_url_Injected(self, ref span);
        }
    }

    public void Play()
    {
        if (!IsReady) return;
        _player.Play();
    }

    public void Pause()
    {
        if (!IsReady) return;
        _player.Pause();
    }

    public void Seek(double seconds)
    {
        if (!IsReady) return;
        double len = _player.length;
        if (len > 0 && seconds > len - 0.25) seconds = Math.Max(0, len - 0.25);
        if (seconds < 0) seconds = 0;
        _player.time = seconds;
    }

    public void Stop()
    {
        if (_player != null)
        {
            try { _player.Stop(); } catch { /* not prepared yet */ }
        }
        _loaded = false;
        _wasReady = false;
    }

    public void SetVolume(float volume01)
    {
        _volume = Mathf.Clamp01(volume01);
        if (_audio != null) _audio.volume = _volume;
    }

    public void Tick()
    {
        if (!_loaded || _error != null || _player == null) return;

        bool ready = _player.isPrepared;
        if (ready && !_wasReady)
        {
            _wasReady = true;
            Plugin.Log.LogInfo($"Stream ready: {_player.width}x{_player.height}, {_player.length:F1}s.");
        }
        else if (!ready && UnityEngine.Time.unscaledTime - _prepareStarted > PrepareTimeoutSeconds)
        {
            _error = "Timed out preparing the stream. The URL may have expired, the format may be unsupported by " +
                     "Unity's VideoPlayer, or the network is slow. Try loading again.";
            Plugin.Log.LogWarning(_error);
        }
    }

    public void Dispose()
    {
        Stop();
        if (_player != null)
        {
            try { UnityEngine.Object.Destroy(_player); } catch { }
            _player = null;
        }
    }
}
