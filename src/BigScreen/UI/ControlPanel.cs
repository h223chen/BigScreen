using System;
using UnityEngine;

namespace BigScreen.UI;

/// <summary>
/// Immediate-mode (IMGUI) control panel. IMGUI is the one UI toolkit that needs no
/// assets shipped with the mod, which is why every Big Walk mod uses it for overlays.
/// All state lives on the controller; this class only draws and forwards clicks.
/// </summary>
internal sealed class ControlPanel
{
    private readonly BigScreenController _c;
    private Rect _window = new Rect(40f, 40f, 560f, 420f);
    private string _urlInput = "";
    private Vector2 _scroll;
    private GUIStyle _wrap;

    private const int WindowId = 0x8153;

    public ControlPanel(BigScreenController controller)
    {
        _c = controller;
    }

    public void Draw()
    {
        _wrap ??= new GUIStyle(GUI.skin.label) { wordWrap = true };
        _window = GUI.Window(WindowId, _window, (GUI.WindowFunction)DrawWindow, "BigScreen - watch together");
    }

    private void DrawWindow(int id)
    {
        try
        {
            DrawBody();
        }
        catch (Exception e)
        {
            GUILayout.Label("UI error: " + e.Message, _wrap);
        }
        GUI.DragWindow(new Rect(0f, 0f, 10000f, 20f));
    }

    private void DrawBody()
    {
        var session = _c.Session;
        bool inLobby = session.IsHost || session.IsConnectedClient;
        bool canControl = _c.CanControl;

        // --- Status ---------------------------------------------------------------
        if (!inLobby)
        {
            GUILayout.Label("Not in a lobby. Host or join a walk first.", _wrap);
        }
        else
        {
            string role = session.IsHost ? $"Host (modded guests: {session.ModdedPeerCount})"
                                         : (session.HelloSent ? "Guest (connected to host)" : "Guest (waiting for host...)");
            GUILayout.Label($"Role: {role}");
            if (!session.IsHost && !session.State.GuestsCanControl)
                GUILayout.Label("The host has not enabled guest control; you can watch and adjust your own volume.", _wrap);
        }
        GUILayout.Label(_c.StatusLine, _wrap);
        if (!string.IsNullOrEmpty(_c.LastError))
        {
            var prev = GUI.color;
            GUI.color = new Color(1f, 0.6f, 0.6f);
            GUILayout.Label(_c.LastError, _wrap);
            GUI.color = prev;
        }
        GUILayout.Space(6f);

        // --- Screen placement -------------------------------------------------------
        GUILayout.BeginHorizontal();
        GUI.enabled = inLobby && canControl;
        if (GUILayout.Button(_c.Session.State.HasScreen ? "Move screen here" : "Place screen here", GUILayout.Height(28f)))
            _c.UserPlaceScreen();
        GUI.enabled = inLobby && canControl && _c.Session.State.HasScreen;
        if (GUILayout.Button("Remove screen", GUILayout.Height(28f)))
            _c.UserRemoveScreen();
        GUI.enabled = true;
        GUILayout.EndHorizontal();
        GUILayout.Space(6f);

        // --- Video ---------------------------------------------------------------------
        GUILayout.Label("YouTube URL (or anything yt-dlp supports):");
        GUILayout.BeginHorizontal();
        GUI.enabled = inLobby && canControl;
        _urlInput = GUILayout.TextField(_urlInput ?? "", GUILayout.ExpandWidth(true));
        if (GUILayout.Button("Load", GUILayout.Width(70f)))
            _c.UserLoad(_urlInput);
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        var st = session.State;
        if (!string.IsNullOrEmpty(st.VideoUrl))
        {
            GUILayout.Label($"Now: {(string.IsNullOrEmpty(st.Title) ? st.VideoUrl : st.Title)}", _wrap);
            double pos = _c.LocalVideoTime;
            double len = _c.LocalVideoDuration;
            GUILayout.Label($"{Fmt(pos)} / {(len > 0 ? Fmt(len) : "?")}   {(st.Playing ? "playing" : "paused")}   drift {_c.LastDrift:+0.00;-0.00}s");
        }

        GUILayout.BeginHorizontal();
        GUI.enabled = inLobby && canControl && !string.IsNullOrEmpty(st.VideoUrl);
        if (GUILayout.Button(st.Playing ? "Pause" : "Play", GUILayout.Height(28f))) _c.UserTogglePlay();
        if (GUILayout.Button("-30s", GUILayout.Height(28f))) _c.UserSeekRelative(-30);
        if (GUILayout.Button("-5s", GUILayout.Height(28f))) _c.UserSeekRelative(-5);
        if (GUILayout.Button("+5s", GUILayout.Height(28f))) _c.UserSeekRelative(5);
        if (GUILayout.Button("+30s", GUILayout.Height(28f))) _c.UserSeekRelative(30);
        if (GUILayout.Button("Restart", GUILayout.Height(28f))) _c.UserSeekTo(0);
        if (GUILayout.Button("Stop", GUILayout.Height(28f))) _c.UserStop();
        GUI.enabled = true;
        GUILayout.EndHorizontal();
        GUILayout.Space(6f);

        // --- Local settings ----------------------------------------------------------------
        GUILayout.BeginHorizontal();
        GUILayout.Label("My volume", GUILayout.Width(80f));
        float v = GUILayout.HorizontalSlider(Plugin.Volume.Value, 0f, 1f, GUILayout.Width(200f));
        if (Math.Abs(v - Plugin.Volume.Value) > 0.001f) { Plugin.Volume.Value = v; _c.ApplyLocalAudioConfig(); }
        GUILayout.Label($"{(int)(v * 100)}%", GUILayout.Width(40f));
        GUILayout.EndHorizontal();

        if (session.IsHost)
        {
            bool g = GUILayout.Toggle(Plugin.GuestsCanControl.Value, " Let modded guests control playback");
            if (g != Plugin.GuestsCanControl.Value) Plugin.GuestsCanControl.Value = g;
            bool a = GUILayout.Toggle(Plugin.AutoPlay.Value, " Auto-play when a video is ready");
            if (a != Plugin.AutoPlay.Value) Plugin.AutoPlay.Value = a;
        }

        GUILayout.Space(6f);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Update yt-dlp", GUILayout.Width(120f))) _c.UserUpdateYtDlp();
        if (GUILayout.Button("Resync", GUILayout.Width(80f))) _c.ForceResync();
        GUILayout.FlexibleSpace();
        if (GUILayout.Button($"Close [{Plugin.ToggleUiKey.Value}]", GUILayout.Width(110f))) _c.SetPanelVisible(false);
        GUILayout.EndHorizontal();
    }

    private static string Fmt(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds)) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}
