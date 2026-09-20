using System;
using UnityEngine;

namespace BigScreen.UI;

/// <summary>
/// Immediate-mode (IMGUI) control panel. IMGUI is the one UI toolkit that needs no
/// assets shipped with the mod, which is why every Big Walk mod uses it for overlays.
/// All state lives on the controller; this class only draws and forwards clicks.
///
/// The default IMGUI window background is partly transparent, which is unreadable over the
/// red starting area. We draw our own opaque background instead, and keep error text amber
/// rather than red so it stays legible against that same area.
/// </summary>
internal sealed class ControlPanel
{
    private readonly BigScreenController _c;
    private Rect _window = new Rect(40f, 40f, 720f, 560f);
    private string _urlInput = "";

    private GUIStyle _windowStyle;
    private GUIStyle _label;
    private GUIStyle _error;
    private GUIStyle _button;
    private GUIStyle _textField;
    private GUIStyle _toggle;
    private Texture2D _bgTexture;
    private int _builtForFontSize = -1;

    private const int WindowId = 0x8153;

    // How far one Left/Right/Back/Forward click slides the screen. Height uses a finer step:
    // getting the height wrong is more obvious than being a few centimetres off sideways.
    private const float NudgeMeters = 0.25f;

    // A plain progressive H.264/AAC MP4. Loading this skips yt-dlp entirely (see
    // YtDlp.LooksLikeDirectMedia), so it tests the screen and VideoPlayer on their own.
    private const string TestMp4Url = "https://samplelib.com/mp4/sample-5s.mp4";

    public ControlPanel(BigScreenController controller)
    {
        _c = controller;
    }

    /// <summary>A 1x1 texture used as a flat background fill.</summary>
    private static Texture2D SolidTexture(Color color)
    {
        var t = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        t.SetPixel(0, 0, color);
        t.Apply();
        // Keep Unity from unloading it between scenes; we never destroy it.
        t.hideFlags = HideFlags.HideAndDontSave;
        return t;
    }

    /// <summary>
    /// Builds the styles. Re-runs if the configured font size changed, so the size can be
    /// tuned from the config file without restarting the game.
    /// </summary>
    private void EnsureStyles()
    {
        int size = Mathf.Clamp(Plugin.UiFontSize.Value, 8, 40);
        if (_windowStyle != null && _builtForFontSize == size) return;
        _builtForFontSize = size;

        if (_bgTexture == null) _bgTexture = SolidTexture(new Color(0.05f, 0.05f, 0.07f, 0.97f));

        _windowStyle = new GUIStyle(GUI.skin.window)
        {
            fontSize = size + 2,
            padding = new RectOffset(12, 12, size + 14, 12),
        };
        _windowStyle.normal.background = _bgTexture;
        _windowStyle.onNormal.background = _bgTexture;
        _windowStyle.normal.textColor = Color.white;
        _windowStyle.onNormal.textColor = Color.white;

        _label = new GUIStyle(GUI.skin.label) { fontSize = size, wordWrap = true };
        _label.normal.textColor = new Color(0.92f, 0.92f, 0.94f);

        // Amber, not red: the starting area is mostly red and red-on-red disappears.
        _error = new GUIStyle(_label) { fontSize = size, wordWrap = true };
        _error.normal.textColor = new Color(1f, 0.78f, 0.25f);

        _button = new GUIStyle(GUI.skin.button) { fontSize = size };
        _textField = new GUIStyle(GUI.skin.textField) { fontSize = size };
        _toggle = new GUIStyle(GUI.skin.toggle) { fontSize = size };
    }

    private float ButtonHeight => _builtForFontSize + 16f;

    // Built once and kept alive for the life of the panel.
    //
    // Casting a managed method to GUI.WindowFunction creates an IL2CPP delegate wrapping a
    // managed trampoline. Doing that inline in Draw() made a new one every frame while the
    // panel was open, with no managed reference kept. Unity holds the native side across the
    // call, so once the GC collected one the process died with an access violation on the
    // main thread - intermittent, and only ever with the panel open.
    //
    // MirrorChannel keeps its converted handlers alive for the same reason.
    private GUI.WindowFunction _drawWindow;

    public void Draw()
    {
        EnsureStyles();
        _drawWindow ??= (GUI.WindowFunction)DrawWindow;
        _window = GUI.Window(WindowId, _window, _drawWindow,
                             "BigScreen - watch together", _windowStyle);
    }

    private void DrawWindow(int id)
    {
        try
        {
            DrawBody();
        }
        catch (Exception e)
        {
            GUILayout.Label("UI error: " + e.Message, _error);
        }
        GUI.DragWindow(new Rect(0f, 0f, 10000f, ButtonHeight));
    }

    private void DrawBody()
    {
        var session = _c.Session;
        bool inLobby = session.IsHost || session.IsConnectedClient;
        bool canControl = _c.CanControl;

        // --- Status ---------------------------------------------------------------
        if (!inLobby)
        {
            GUILayout.Label("Not in a lobby. Host or join a walk first.", _label);
        }
        else
        {
            string role = session.IsHost ? $"Host (modded guests: {session.ModdedPeerCount})"
                                         : (session.HelloSent ? "Guest (connected to host)" : "Guest (waiting for host...)");
            GUILayout.Label($"Role: {role}", _label);
            if (!session.IsHost && !session.State.GuestsCanControl)
                GUILayout.Label("The host has not enabled guest control; you can watch and adjust your own volume.", _label);
        }
        GUILayout.Label(_c.StatusLine, _label);
        if (!string.IsNullOrEmpty(_c.LastError))
            GUILayout.Label(_c.LastError, _error);
        GUILayout.Space(8f);

        // --- Screen placement -------------------------------------------------------
        GUILayout.BeginHorizontal();
        GUI.enabled = inLobby && canControl;
        if (GUILayout.Button(_c.Session.State.HasScreen ? "Move screen here" : "Place screen here", _button, GUILayout.Height(ButtonHeight)))
            _c.UserPlaceScreen();
        GUI.enabled = inLobby && canControl && _c.Session.State.HasScreen;
        if (GUILayout.Button("Remove screen", _button, GUILayout.Height(ButtonHeight)))
            _c.UserRemoveScreen();
        GUI.enabled = inLobby;
        if (GUILayout.Button("Set spawn here", _button, GUILayout.Height(ButtonHeight)))
            _c.UserSetSpawnHere();
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        // Height is local geometry rather than shared state, so this works whether or not
        // the host has handed out control. Editing the config file mid-game does nothing;
        // BepInEx does not re-read it, so these buttons are the way to dial the height in.
        GUILayout.BeginHorizontal();
        GUILayout.Label($"Screen height: {Plugin.ScreenGroundClearance.Value:F2} m", _label,
                        GUILayout.Width(_builtForFontSize * 13f));
        GUI.enabled = _c.Session.State.HasScreen;
        if (GUILayout.Button("Lower", _button, GUILayout.Width(_builtForFontSize * 6f), GUILayout.Height(ButtonHeight)))
            _c.UserNudgeScreenHeight(-0.1f);
        if (GUILayout.Button("Raise", _button, GUILayout.Width(_builtForFontSize * 6f), GUILayout.Height(ButtonHeight)))
            _c.UserNudgeScreenHeight(+0.1f);
        GUI.enabled = true;
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();

        // Sliding the screen moves it for everyone, so unlike the height this needs control.
        // Directions are relative to the screen's own facing, not the world axes.
        var screenPos = _c.Session.State.ScreenPosition;
        GUILayout.BeginHorizontal();
        GUILayout.Label(_c.Session.State.HasScreen
                            ? $"Screen at ({screenPos.x:F1}, {screenPos.y:F1}, {screenPos.z:F1})"
                            : "Screen at (none placed)",
                        _label, GUILayout.Width(_builtForFontSize * 13f));
        GUI.enabled = inLobby && canControl && _c.Session.State.HasScreen;
        if (GUILayout.Button("Left", _button, GUILayout.Width(_builtForFontSize * 5f), GUILayout.Height(ButtonHeight)))
            _c.UserNudgeScreen(-NudgeMeters, 0f);
        if (GUILayout.Button("Right", _button, GUILayout.Width(_builtForFontSize * 5f), GUILayout.Height(ButtonHeight)))
            _c.UserNudgeScreen(+NudgeMeters, 0f);
        if (GUILayout.Button("Back", _button, GUILayout.Width(_builtForFontSize * 5f), GUILayout.Height(ButtonHeight)))
            _c.UserNudgeScreen(0f, -NudgeMeters);
        if (GUILayout.Button("Forward", _button, GUILayout.Width(_builtForFontSize * 7f), GUILayout.Height(ButtonHeight)))
            _c.UserNudgeScreen(0f, +NudgeMeters);
        GUI.enabled = true;
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();
        GUILayout.Space(8f);

        // --- Video ---------------------------------------------------------------------
        GUILayout.Label("YouTube URL (or anything yt-dlp supports):", _label);
        GUILayout.BeginHorizontal();
        GUI.enabled = inLobby && canControl;
        _urlInput = GUILayout.TextField(_urlInput ?? "", _textField, GUILayout.ExpandWidth(true), GUILayout.Height(ButtonHeight));
        if (GUILayout.Button("Load", _button, GUILayout.Width(90f), GUILayout.Height(ButtonHeight)))
            _c.UserLoad(_urlInput);
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        // A duplicate of the Test MP4 button at the top of the panel. If clicking this one
        // is fine and the bottom one still kills the game, the position matters rather than
        // the button; if both crash, it is the button.
        GUI.enabled = inLobby && canControl;
        var st = session.State;
        if (!string.IsNullOrEmpty(st.VideoUrl))
        {
            GUILayout.Label($"Now: {(string.IsNullOrEmpty(st.Title) ? st.VideoUrl : st.Title)}", _label);
            double pos = _c.LocalVideoTime;
            double len = _c.LocalVideoDuration;
            GUILayout.Label($"{Fmt(pos)} / {(len > 0 ? Fmt(len) : "?")}   {(st.Playing ? "playing" : "paused")}   drift {_c.LastDrift:+0.00;-0.00}s", _label);
        }

        GUILayout.BeginHorizontal();
        GUI.enabled = inLobby && canControl && !string.IsNullOrEmpty(st.VideoUrl);
        if (GUILayout.Button(st.Playing ? "Pause" : "Play", _button, GUILayout.Height(ButtonHeight))) _c.UserTogglePlay();
        if (GUILayout.Button("-30s", _button, GUILayout.Height(ButtonHeight))) _c.UserSeekRelative(-30);
        if (GUILayout.Button("-5s", _button, GUILayout.Height(ButtonHeight))) _c.UserSeekRelative(-5);
        if (GUILayout.Button("+5s", _button, GUILayout.Height(ButtonHeight))) _c.UserSeekRelative(5);
        if (GUILayout.Button("+30s", _button, GUILayout.Height(ButtonHeight))) _c.UserSeekRelative(30);
        if (GUILayout.Button("Restart", _button, GUILayout.Height(ButtonHeight))) _c.UserSeekTo(0);
        if (GUILayout.Button("Stop", _button, GUILayout.Height(ButtonHeight))) _c.UserStop();
        GUI.enabled = true;
        GUILayout.EndHorizontal();
        GUILayout.Space(8f);

        // --- Local settings ----------------------------------------------------------------
        GUILayout.BeginHorizontal();
        GUILayout.Label("My volume", _label, GUILayout.Width(_builtForFontSize * 7f));
        float v = GUILayout.HorizontalSlider(Plugin.Volume.Value, 0f, 1f, GUILayout.Width(220f));
        if (Math.Abs(v - Plugin.Volume.Value) > 0.001f) { Plugin.Volume.Value = v; _c.ApplyLocalAudioConfig(); }
        GUILayout.Label($"{(int)(v * 100)}%", _label, GUILayout.Width(_builtForFontSize * 4f));
        GUILayout.EndHorizontal();

        if (session.IsHost)
        {
            bool g = GUILayout.Toggle(Plugin.GuestsCanControl.Value, " Let modded guests control playback", _toggle);
            if (g != Plugin.GuestsCanControl.Value) Plugin.GuestsCanControl.Value = g;
            bool a = GUILayout.Toggle(Plugin.AutoPlay.Value, " Auto-play when a video is ready", _toggle);
            if (a != Plugin.AutoPlay.Value) Plugin.AutoPlay.Value = a;
        }

        GUILayout.Space(8f);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Update yt-dlp", _button, GUILayout.Width(_builtForFontSize * 10f), GUILayout.Height(ButtonHeight))) _c.UserUpdateYtDlp();
        if (GUILayout.Button("Resync", _button, GUILayout.Width(_builtForFontSize * 7f), GUILayout.Height(ButtonHeight)))
            _c.ForceResync();
        GUI.enabled = inLobby && canControl;
        if (GUILayout.Button("Test MP4", _button, GUILayout.Width(_builtForFontSize * 8f), GUILayout.Height(ButtonHeight)))
        {
            // Loads a short MP4 with no yt-dlp involved, to check the screen on its own.
            GUIUtility.keyboardControl = 0;
            _c.UserLoad(TestMp4Url);
        }
        GUI.enabled = true;
        GUILayout.FlexibleSpace();
        if (GUILayout.Button($"Close [{Plugin.ToggleUiKey.Value}]", _button, GUILayout.Width(_builtForFontSize * 10f), GUILayout.Height(ButtonHeight))) _c.SetPanelVisible(false);
        GUILayout.EndHorizontal();
    }

    private static string Fmt(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds)) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}
