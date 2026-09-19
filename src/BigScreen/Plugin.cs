using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace BigScreen;

/// <summary>
/// BigScreen: a shared in-world screen for watching YouTube together.
///
/// Entry point. BepInEx constructs this once at game start (before any scene loads),
/// we bind config, register our MonoBehaviour with the IL2CPP runtime and spin up a
/// persistent host object that drives everything else from Update()/OnGUI().
/// </summary>
[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "dev.h223chen.bigscreen";
    public const string Name = "BigScreen";
    public const string Version = "0.1.0";

    internal static Plugin Instance { get; private set; }
    internal static ManualLogSource Log;

    // --- Config -----------------------------------------------------------------------
    internal static ConfigEntry<KeyCode> ToggleUiKey;
    internal static ConfigEntry<float> Volume;
    internal static ConfigEntry<float> AudioMaxDistance;
    internal static ConfigEntry<float> ScreenWidthMeters;
    internal static ConfigEntry<int> RenderWidth;
    internal static ConfigEntry<int> RenderHeight;
    internal static ConfigEntry<string> YtDlpPath;
    internal static ConfigEntry<bool> YtDlpAutoDownload;
    internal static ConfigEntry<string> FormatSelector;
    internal static ConfigEntry<float> DriftTolerance;
    internal static ConfigEntry<bool> GuestsCanControl;
    internal static ConfigEntry<bool> AutoPlay;
    internal static ConfigEntry<bool> Diagnostics;

    private Harmony _harmony;

    public override void Load()
    {
        Instance = this;
        Log = base.Log;

        ToggleUiKey = Config.Bind("Keys", "ToggleUI", KeyCode.F8,
            "Opens/closes the BigScreen control panel.");

        Volume = Config.Bind("Audio", "Volume", 0.8f,
            new ConfigDescription("Local playback volume for the screen's audio (0-1). Only affects you.",
                new AcceptableValueRange<float>(0f, 1f)));
        AudioMaxDistance = Config.Bind("Audio", "MaxDistance", 40f,
            new ConfigDescription("Distance in meters at which the screen's audio fades to silence.",
                new AcceptableValueRange<float>(5f, 200f)));

        ScreenWidthMeters = Config.Bind("Screen", "WidthMeters", 4f,
            new ConfigDescription("Physical width of the screen in the world (16:9, so height follows).",
                new AcceptableValueRange<float>(1f, 20f)));
        RenderWidth = Config.Bind("Screen", "RenderWidth", 1280,
            "Width of the texture the video is decoded into. 1280x720 is plenty for a 4 m screen.");
        RenderHeight = Config.Bind("Screen", "RenderHeight", 720,
            "Height of the texture the video is decoded into.");

        YtDlpPath = Config.Bind("YtDlp", "Path", "",
            "Full path to yt-dlp.exe. Leave empty to use BepInEx\\plugins\\BigScreen\\yt-dlp.exe " +
            "(downloaded automatically when AutoDownload is on).");
        YtDlpAutoDownload = Config.Bind("YtDlp", "AutoDownload", true,
            "Download yt-dlp.exe from its official GitHub releases the first time it is needed.");
        FormatSelector = Config.Bind("YtDlp", "FormatSelector",
            "best[ext=mp4][vcodec^=avc1][acodec!=none][height<=720]/best[ext=mp4][acodec!=none]/18/best[acodec!=none][protocol^=http]",
            "yt-dlp -f expression. Unity's VideoPlayer needs a single progressive MP4 (H.264+AAC) " +
            "that already contains audio, so the default only picks muxed formats. Change with care.");

        DriftTolerance = Config.Bind("Sync", "DriftToleranceSeconds", 1.0f,
            new ConfigDescription("How far your playback may drift from the host's timeline before we re-seek.",
                new AcceptableValueRange<float>(0.25f, 10f)));
        GuestsCanControl = Config.Bind("Sync", "GuestsCanControl", false,
            "Host only: let guests with the mod load videos, play/pause, seek and move the screen.");
        AutoPlay = Config.Bind("Sync", "AutoPlay", true,
            "Host only: start playing as soon as a loaded video is ready.");

        Diagnostics = Config.Bind("Debug", "Diagnostics", false,
            "Write a verbose status line to the BepInEx log every couple of seconds.");

        // MonoBehaviours written in managed code must be registered with the IL2CPP
        // domain before Unity will accept them via AddComponent.
        ClassInjector.RegisterTypeInIl2Cpp<BigScreenController>();

        var host = new GameObject("BigScreen");
        host.hideFlags = HideFlags.HideAndDontSave;
        UnityEngine.Object.DontDestroyOnLoad(host);
        host.AddComponent<BigScreenController>();

        // Tear down cleanly when the player leaves a lobby. The controller also polls
        // Mirror's state as a fallback in case these hooks are missing after a game update.
        _harmony = new Harmony(Guid);
        TryPatchPostfix(typeof(HouseNetworkManager), "OnStopHost", nameof(Patches.NetworkStoppedPostfix));
        TryPatchPostfix(typeof(HouseNetworkManager), "OnStopClient", nameof(Patches.NetworkStoppedPostfix));

        Log.LogInfo($"{Name} v{Version} loaded. Press {ToggleUiKey.Value} in a lobby to open the panel.");
    }

    private void TryPatchPostfix(Type type, string method, string postfixName)
    {
        try
        {
            var original = AccessTools.Method(type, method, Type.EmptyTypes);
            if (original == null)
            {
                Log.LogWarning($"Could not find {type.Name}.{method} to patch (game update?). Falling back to polling.");
                return;
            }
            _harmony.Patch(original, postfix: new HarmonyMethod(typeof(Patches), postfixName));
        }
        catch (Exception e)
        {
            Log.LogWarning($"Patch of {type.Name}.{method} failed: {e.Message}. Falling back to polling.");
        }
    }

    public override bool Unload()
    {
        BigScreenController.Instance?.ResetSession("plugin unload");
        _harmony?.UnpatchSelf();
        return true;
    }
}

internal static class Patches
{
    internal static void NetworkStoppedPostfix()
    {
        try { BigScreenController.Instance?.ResetSession("network stopped"); }
        catch (Exception e) { Plugin.Log.LogWarning($"Reset on network stop failed: {e.Message}"); }
    }
}
