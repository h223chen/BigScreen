using System;
using System.Threading;
using System.Threading.Tasks;
using BigScreen.Net;
using BigScreen.Resolve;
using BigScreen.UI;
using BigScreen.Util;
using BigScreen.Video;
using BigScreen.World;
using Mirror;
using UnityEngine;

namespace BigScreen;

/// <summary>
/// The one MonoBehaviour. Runs every frame for the life of the game and:
///   - drives the <see cref="SyncSession"/> (host state / client follow),
///   - builds and tears down the in-world <see cref="ScreenObject"/>,
///   - resolves page URLs to stream URLs (yt-dlp, off-thread) and feeds the video backend,
///   - keeps local playback locked to the host's timeline (drift correction),
///   - draws the control panel.
///
/// Everyone, host included, runs the same "follow the state" logic; the host is simply
/// the only one allowed to change the state (and the one who forwards guest requests).
/// </summary>
public class BigScreenController : MonoBehaviour
{
    public BigScreenController(IntPtr ptr) : base(ptr) { }

    internal static BigScreenController Instance { get; private set; }

    internal SyncSession Session { get; } = new SyncSession();
    internal string StatusLine { get; private set; } = "Idle.";
    internal string LastError { get; private set; }
    internal double LastDrift { get; private set; }
    internal double LocalVideoTime => _video != null && _video.IsReady ? _video.Time : 0.0;
    internal double LocalVideoDuration => _video != null && _video.IsReady ? _video.Duration : 0.0;
    internal bool CanControl => Session.IsHost || (Session.IsConnectedClient && Session.State.GuestsCanControl);

    private ControlPanel _panel;
    private bool _panelVisible;
    private CursorLockMode _priorLock = CursorLockMode.Locked;
    private bool _priorCursorVisible;

    private ScreenObject _screen;
    private IVideoBackend _video;

    // What the local video backend currently has loaded, keyed by the page URL from state.
    private string _loadedPageUrl;
    private string _resolvingPageUrl;
    private CancellationTokenSource _resolveCts;
    private bool _pendingInitialSeek;
    private float _lastSeekAt = -100f;
    private float _nextDiag;
    private bool _wasInSession;

    private const float MinSecondsBetweenSeeks = 2.5f;

    private void Awake()
    {
        Instance = this;
        _panel = new ControlPanel(this);
        Session.StateChanged += OnStateChanged;
        Session.RequestReceived += OnGuestRequest;
    }

    private void Update()
    {
        MainThread.Drain();

        try
        {
            HandleInput();
            Session.Tick();
            Dev.AutoStart.Tick(this);

            bool inSession = NetworkServer.active || NetworkClient.active;
            if (_wasInSession && !inSession) ResetSession("left lobby");
            _wasInSession = inSession;

            if (inSession)
            {
                ReconcileScreen();
                ReconcileVideo();
                FollowTimeline();
            }

            if (Plugin.Diagnostics.Value && Time.unscaledTime >= _nextDiag)
            {
                _nextDiag = Time.unscaledTime + 2f;
                Plugin.Log.LogInfo($"[diag] host={Session.IsHost} client={NetworkClient.isConnected} peers={Session.ModdedPeerCount} " +
                                   $"rev={Session.State.Revision} screen={(_screen != null)} url={Session.State.VideoUrl} " +
                                   $"playing={Session.State.Playing} ready={_video?.IsReady} t={LocalVideoTime:F1} drift={LastDrift:F2} " +
                                   $"nettime={SafeNetTime():F1} conv_failed={MirrorChannel.ConversionFailed}");
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Update failed: {e}");
        }
    }

    /// <summary>
    /// Unity calls this from native code, so an exception must never leave it. Our type is
    /// injected into IL2CPP, and letting a managed exception unwind into native Unity frames
    /// takes the process down with a fatal access violation rather than a logged error.
    /// The same applies to Update; both swallow and log instead.
    /// </summary>
    private void OnGUI()
    {
        if (!_panelVisible) return;

        // Logged before GUI.Window is called. Clicking Test MP4 produces no output at all,
        // not even the first line of the window callback, while every other button in the
        // same panel logs normally. If this line is also missing for that click, the fault
        // is above our GUI code entirely.
        // Log every event that is not routine drawing. The narrower MouseDown/MouseUp filter
        // never fired here even though the same check works inside the window callback, so
        // this records whatever type actually arrives - including none, if Event.current is
        // null at this level.
        var ev = Event.current;
        if (ev == null) Plugin.Log.LogInfo("OnGUI: Event.current is null");
        else if (ev.type != EventType.Layout && ev.type != EventType.Repaint
                 && ev.type != EventType.MouseMove && ev.type != EventType.MouseDrag)
            Plugin.Log.LogInfo($"OnGUI: {ev.type} at {ev.mousePosition}");

        try
        {
            _panel.Draw();
        }
        catch (Exception e)
        {
            Plugin.Log.LogError($"Panel draw failed: {e}");
        }
    }

    // --- Input / panel ---------------------------------------------------------------

    private void HandleInput()
    {
        if (Input.GetKeyDown(Plugin.ToggleUiKey.Value)) SetPanelVisible(!_panelVisible);

        // The game re-locks the cursor every frame while playing; reassert while open.
        if (_panelVisible)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }

    internal void SetPanelVisible(bool visible)
    {
        if (visible == _panelVisible) return;
        _panelVisible = visible;
        if (visible)
        {
            _priorLock = Cursor.lockState;
            _priorCursorVisible = Cursor.visible;
        }
        else
        {
            Cursor.lockState = _priorLock;
            Cursor.visible = _priorCursorVisible;
        }
    }

    // --- User actions (host applies directly; guests send requests) -------------------

    internal void UserPlaceScreen()
    {
        if (!TryGetLocalPlayerPose(out var pos, out var fwd)) { LastError = "Could not find your character."; return; }
        // Logged so a spawn point can be picked from a spot you actually walked to.
        Plugin.Log.LogInfo($"Your position: ({pos.x:F2}, {pos.y:F2}, {pos.z:F2})  facing ({fwd.x:F2}, {fwd.y:F2}, {fwd.z:F2})");
        // 4 m in front of the player, facing them. The quad's picture faces local -Z, so we
        // point +Z away from the player.
        var screenPos = pos + fwd * 4f;
        float yaw = Quaternion.LookRotation(new Vector3(fwd.x, 0f, fwd.z).normalized, Vector3.up).eulerAngles.y;
        var req = new Request { Action = Protocol.Action.Place, Position = screenPos, Yaw = yaw };
        Apply(req);
    }

    internal void UserRemoveScreen() => Apply(new Request { Action = Protocol.Action.Remove });

    /// <summary>
    /// Writes where you are standing into Dev.SpawnPosition, so an unattended run starts
    /// from a spot you picked rather than wherever the game drops you.
    /// </summary>
    internal void UserSetSpawnHere()
    {
        if (!TryGetLocalPlayerPose(out var pos, out _)) { LastError = "Could not find your character."; return; }

        var text = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                                 "{0:F2},{1:F2},{2:F2}", pos.x, pos.y, pos.z);
        Plugin.SpawnPosition.Value = text;
        try { Plugin.Instance.Config.Save(); } catch (Exception e) { Plugin.Log.LogWarning($"Saving the config failed: {e.Message}"); }

        StatusLine = "Spawn set to " + text;
        Plugin.Log.LogInfo("Spawn set to " + text);
    }

    internal void UserLoad(string pageUrl)
    {
        // Breadcrumbs: the game has died between this click and StartResolve without
        // logging anything, so each step writes a line. Logging is set to flush on every
        // write, so the last line in the log is the last step that completed.
        Util.Trace.Write($"UserLoad enter: {pageUrl}");
        pageUrl = (pageUrl ?? "").Trim();
        if (!YtDlp.LooksLikeUrl(pageUrl)) { LastError = "Enter a full URL starting with https://"; return; }
        LastError = null;
        if (!Session.State.HasScreen)
        {
            // Be helpful: loading with no screen places one in front of you first.
            UserPlaceScreen();
        }
        Util.Trace.Write("UserLoad: about to Apply");
        Apply(new Request { Action = Protocol.Action.Load, Text = pageUrl });
        Util.Trace.Write("UserLoad: Apply returned");
    }

    internal void UserTogglePlay() => Apply(new Request { Action = Session.State.Playing ? Protocol.Action.Pause : Protocol.Action.Play });
    internal void UserSeekRelative(double delta) => Apply(new Request { Action = Protocol.Action.SeekTo, Value = Session.State.ExpectedVideoTime(SafeNetTime()) + delta });
    internal void UserSeekTo(double seconds) => Apply(new Request { Action = Protocol.Action.SeekTo, Value = seconds });
    internal void UserStop() => Apply(new Request { Action = Protocol.Action.Stop });

    internal void UserUpdateYtDlp()
    {
        StatusLine = "Updating yt-dlp...";
        RunOffThread("BigScreen-ytdlp-update", () =>
        {
            var msg = YtDlp.Update(CancellationToken.None);
            MainThread.Post(() => { StatusLine = "yt-dlp: " + msg; Plugin.Log.LogInfo("yt-dlp update: " + msg); });
        });
    }

    internal void ForceResync()
    {
        _lastSeekAt = -100f;
        _pendingInitialSeek = true;
    }

    internal void ApplyLocalAudioConfig()
    {
        _screen?.ApplyAudioConfig();
        _video?.SetVolume(Plugin.Volume.Value);
    }

    private void Apply(Request req)
    {
        if (Session.IsHost)
        {
            ApplyOnHost(req);
        }
        else if (Session.IsConnectedClient)
        {
            if (!Session.State.GuestsCanControl) { LastError = "The host has not enabled guest control."; return; }
            if (!Session.SendRequest(req)) LastError = "Could not send the request to the host.";
        }
    }

    /// <summary>Host: turn a request (local or from a guest) into a state mutation.</summary>
    private void ApplyOnHost(Request req)
    {
        double now = SafeNetTime();
        switch (req.Action)
        {
            case Protocol.Action.Place:
                Session.Mutate(s => { s.HasScreen = true; s.ScreenPosition = req.Position; s.ScreenYaw = req.Yaw; });
                break;
            case Protocol.Action.Remove:
                Session.Mutate(s => { s.HasScreen = false; s.VideoUrl = ""; s.Title = ""; s.Playing = false; s.AnchorVideoTime = 0; });
                break;
            case Protocol.Action.Load:
                Session.Mutate(s =>
                {
                    s.VideoUrl = req.Text;
                    s.Title = "";
                    s.Playing = false;
                    s.AnchorVideoTime = 0;
                    s.AnchorNetTime = now;
                });
                break;
            case Protocol.Action.Play:
                if (string.IsNullOrEmpty(Session.State.VideoUrl)) return;
                Session.Mutate(s => { s.AnchorVideoTime = s.ExpectedVideoTime(now); s.AnchorNetTime = now; s.Playing = true; });
                break;
            case Protocol.Action.Pause:
                Session.Mutate(s => { s.AnchorVideoTime = s.ExpectedVideoTime(now); s.AnchorNetTime = now; s.Playing = false; });
                break;
            case Protocol.Action.SeekTo:
                Session.Mutate(s =>
                {
                    double target = Math.Max(0, req.Value);
                    double len = LocalVideoDuration;
                    if (len > 0 && target > len - 0.5) target = Math.Max(0, len - 0.5);
                    s.AnchorVideoTime = target;
                    s.AnchorNetTime = now;
                });
                break;
            case Protocol.Action.Stop:
                Session.Mutate(s => { s.VideoUrl = ""; s.Title = ""; s.Playing = false; s.AnchorVideoTime = 0; });
                break;
        }
    }

    private bool OnGuestRequest(Request req, NetworkConnectionToClient from)
    {
        Plugin.Log.LogInfo($"Guest {from?.connectionId} requested {req.Action}.");
        ApplyOnHost(req);
        return true;
    }

    private void OnStateChanged(SyncState s)
    {
        // Any change to the timeline should make us re-check our position promptly.
        _lastSeekAt = -100f;
    }

    // --- Reconciliation: make the local world match the state -------------------------------

    private void ReconcileScreen()
    {
        var s = Session.State;
        if (s.HasScreen && _screen == null)
        {
            try
            {
                _screen = ScreenObject.Create(s.ScreenPosition, s.ScreenYaw, Plugin.ScreenWidthMeters.Value,
                    Plugin.RenderWidth.Value, Plugin.RenderHeight.Value);
                _video = new UnityVideoBackend(_screen.Root, _screen.Audio, _screen.Texture);
                _video.SetVolume(Plugin.Volume.Value);
                _loadedPageUrl = null;
                Plugin.Log.LogInfo($"Screen placed at {s.ScreenPosition}.");
            }
            catch (Exception e)
            {
                LastError = "Could not create the screen: " + e.Message;
                Plugin.Log.LogError(LastError + "\n" + e);
                // Avoid retrying every frame.
                _screen?.Dispose(); _screen = null;
                if (Session.IsHost) Session.Mutate(x => x.HasScreen = false);
            }
        }
        else if (!s.HasScreen && _screen != null)
        {
            TearDownScreen();
        }
        else if (_screen != null && _screen.Root != null)
        {
            if ((_screen.Root.transform.position - s.ScreenPosition).sqrMagnitude > 0.0001f
                || Mathf.Abs(Mathf.DeltaAngle(_screen.Root.transform.eulerAngles.y, s.ScreenYaw)) > 0.1f)
            {
                _screen.MoveTo(s.ScreenPosition, s.ScreenYaw);
            }
        }
    }

    private void ReconcileVideo()
    {
        var s = Session.State;
        if (_video == null) return;

        string wanted = s.VideoUrl ?? "";

        if (wanted.Length == 0)
        {
            if (_loadedPageUrl != null || _resolvingPageUrl != null)
            {
                CancelResolve();
                _video.Stop();
                _screen?.ClearToIdle();
                _loadedPageUrl = null;
                StatusLine = "Screen idle.";
            }
            _video.Tick();
            return;
        }

        if (wanted != _loadedPageUrl && wanted != _resolvingPageUrl)
        {
            StartResolve(wanted);
        }

        _video.Tick();

        if (_video.Error != null && LastError != _video.Error)
        {
            LastError = _video.Error;
        }
    }

    private void StartResolve(string pageUrl)
    {
        Util.Trace.Write("StartResolve enter");
        CancelResolve();
        _resolvingPageUrl = pageUrl;
        _resolveCts = new CancellationTokenSource();
        var ct = _resolveCts.Token;
        LastError = null;

        void Complete(YtDlp.Result r) => MainThread.Post(() =>
        {
            if (ct.IsCancellationRequested || _resolvingPageUrl != pageUrl) return;
            _resolvingPageUrl = null;
            if (_video == null) return; // screen was removed while we were resolving
            if (!r.Ok)
            {
                LastError = r.Error;
                StatusLine = "Could not load video.";
                _loadedPageUrl = pageUrl; // don't retry in a loop; a new Load will change the URL/revision
                Plugin.Log.LogWarning($"Resolve failed: {r.Error}");
                return;
            }
            Plugin.Log.LogInfo($"Resolved '{r.Title}' ({r.Duration:F0}s).");
            StatusLine = $"Loading '{r.Title}'...";
            _loadedPageUrl = pageUrl;
            _pendingInitialSeek = true;
            _video.Load(r.DirectUrl);
            if (Session.IsHost && Session.State.VideoUrl == pageUrl && Session.State.Title != r.Title)
                Session.Mutate(x => x.Title = r.Title);
        });

        // A URL that already points at a media file needs no resolving, and Dev.DirectUrl forces
        // the same path for any page URL. Both skip yt-dlp completely, which also separates two
        // things that currently fail together: whether the video pipeline works, and whether
        // running yt-dlp is what kills the process.
        string directUrl = Plugin.DevDirectUrl.Value;
        if (string.IsNullOrWhiteSpace(directUrl) && YtDlp.LooksLikeDirectMedia(pageUrl))
            directUrl = pageUrl;

        if (!string.IsNullOrWhiteSpace(directUrl))
        {
            StatusLine = "Loading direct URL (yt-dlp skipped)...";
            Util.Trace.Write($"StartResolve: direct media, calling Complete: {directUrl.Trim()}");
            Complete(new YtDlp.Result { Ok = true, Title = "Direct URL", DirectUrl = directUrl.Trim() });
            return;
        }

        StatusLine = "Resolving stream with yt-dlp...";
        Plugin.Log.LogInfo($"Resolving {pageUrl}");
        RunOffThread("BigScreen-resolve", () => Complete(YtDlp.Resolve(pageUrl, ct)));
    }

    /// <summary>
    /// Runs work on a dedicated background thread rather than the runtime thread pool.
    ///
    /// The mod previously used Task.Run here. The game died twice with a fatal access violation
    /// inside coreclr.dll while a resolve was in flight, and this was the only concurrency the
    /// mod owned, so we keep our background work on threads we create ourselves. See the note
    /// on the YtDlp class. Exceptions are logged here because nothing awaits this work.
    /// </summary>
    private static void RunOffThread(string name, Action work)
    {
        var thread = new Thread(() =>
        {
            try { work(); }
            catch (Exception e) { Plugin.Log.LogError($"{name} failed: {e}"); }
        })
        { IsBackground = true, Name = name };
        thread.Start();
    }

    private void CancelResolve()
    {
        try { _resolveCts?.Cancel(); } catch { }
        _resolveCts = null;
        _resolvingPageUrl = null;
    }

    /// <summary>
    /// Keep the local player on the host's timeline. Runs for host and guests alike.
    /// </summary>
    private void FollowTimeline()
    {
        var s = Session.State;
        if (_video == null || !_video.IsLoaded || string.IsNullOrEmpty(s.VideoUrl)) return;
        if (!_video.IsReady)
        {
            if (_video.Error == null) StatusLine = "Buffering...";
            return;
        }

        double now = SafeNetTime();
        double expected = s.ExpectedVideoTime(now);
        double len = _video.Duration;
        bool ended = len > 0 && expected >= len - 0.3;

        if (_pendingInitialSeek)
        {
            _pendingInitialSeek = false;
            _video.Seek(expected);
            _lastSeekAt = Time.unscaledTime;
            // Host: a freshly loaded video starts paused at 0; auto-play once ready.
            if (Session.IsHost && !s.Playing && Plugin.AutoPlay.Value && s.AnchorVideoTime <= 0.01 && !ended)
                ApplyOnHost(new Request { Action = Protocol.Action.Play });
        }

        if (ended)
        {
            if (_video.IsPlaying) _video.Pause();
            if (Session.IsHost && s.Playing)
                Session.Mutate(x => { x.Playing = false; x.AnchorVideoTime = Math.Max(0, len - 0.3); x.AnchorNetTime = now; });
            StatusLine = "Ended.";
            LastDrift = 0;
            return;
        }

        double drift = _video.Time - expected;
        LastDrift = drift;

        if (Math.Abs(drift) > Plugin.DriftTolerance.Value && Time.unscaledTime - _lastSeekAt > MinSecondsBetweenSeeks)
        {
            _video.Seek(expected);
            _lastSeekAt = Time.unscaledTime;
            Plugin.Log.LogDebug($"Re-synced: drift was {drift:F2}s.");
        }

        if (s.Playing && !_video.IsPlaying) _video.Play();
        else if (!s.Playing && _video.IsPlaying) _video.Pause();

        StatusLine = s.Playing ? "Playing." : "Paused.";
    }

    // --- Helpers ------------------------------------------------------------------------------

    private static double SafeNetTime()
    {
        try { return NetworkTime.time; }
        catch { return Time.unscaledTimeAsDouble; }
    }

    private static bool TryGetLocalPlayerPose(out Vector3 position, out Vector3 forward)
    {
        position = Vector3.zero;
        forward = Vector3.forward;
        try
        {
            PlayerCharacter pc = WorldManager.localPlayerCharacter;
            if (pc == null)
            {
                var all = PlayerCharacter.allPlayerCharacters;
                if (all != null)
                    for (int i = 0; i < all.Count; i++)
                    {
                        var p = all[i];
                        if (p != null && p.playerNetworking != null && p.playerNetworking.isLocalPlayer) { pc = p; break; }
                    }
            }
            if (pc == null) return false;

            position = pc.transform.position;
            // Prefer the camera's facing so "in front of me" matches what the player is looking at.
            var cam = pc.cameraTransform != null ? pc.cameraTransform : pc.transform;
            forward = cam.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f) forward = pc.transform.forward;
            forward.Normalize();
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"Local player lookup failed: {e.Message}");
            return false;
        }
    }

    private void TearDownScreen()
    {
        CancelResolve();
        try { _video?.Dispose(); } catch { }
        _video = null;
        try { _screen?.Dispose(); } catch { }
        _screen = null;
        _loadedPageUrl = null;
        _pendingInitialSeek = false;
    }

    /// <summary>Full reset: called when leaving a lobby or when the plugin unloads.</summary>
    internal void ResetSession(string reason)
    {
        Plugin.Log.LogInfo($"Reset ({reason}).");
        TearDownScreen();
        Session.Reset();
        StatusLine = "Idle.";
        LastError = null;
        LastDrift = 0;
    }
}
