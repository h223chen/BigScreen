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
    // `new` because BasePlugin exposes an instance Log; ours is the static shortcut the
    // rest of the mod uses. Hiding is intentional.
    internal static new ManualLogSource Log;

    // --- Config -----------------------------------------------------------------------
    internal static ConfigEntry<KeyCode> ToggleUiKey;
    internal static ConfigEntry<int> UiFontSize;
    internal static ConfigEntry<float> Volume;
    internal static ConfigEntry<float> AudioMaxDistance;
    internal static ConfigEntry<float> AudioFullVolumeRadius;
    internal static ConfigEntry<float> ScreenWidthMeters;
    internal static ConfigEntry<float> ScreenGroundClearance;
    internal static ConfigEntry<int> RenderWidth;
    internal static ConfigEntry<int> RenderHeight;
    internal static ConfigEntry<string> YtDlpPath;
    internal static ConfigEntry<bool> YtDlpAutoDownload;
    internal static ConfigEntry<string> FormatSelector;
    internal static ConfigEntry<string> ExtractorArgs;
    internal static ConfigEntry<string> CookiesFromBrowser;
    internal static ConfigEntry<float> DriftTolerance;
    internal static ConfigEntry<bool> GuestsCanControl;
    internal static ConfigEntry<bool> AutoPlay;
    internal static ConfigEntry<bool> Diagnostics;

    // Development helpers. Off by default; see BigScreen.Dev.AutoStart.
    internal static ConfigEntry<bool> AutoHost;
    internal static ConfigEntry<string> AutoHostSaveSlot;
    internal static ConfigEntry<int> AutoHostPlayerCount;
    internal static ConfigEntry<float> AutoHostDelay;
    internal static ConfigEntry<string> AutoLoadUrl;
    internal static ConfigEntry<float> AutoLoadDelay;
    internal static ConfigEntry<string> DevDirectUrl;
    internal static ConfigEntry<bool> AutoDismissMenus;
    internal static ConfigEntry<string> SpawnPosition;
    internal static ConfigEntry<string> ScreenPose;

    private Harmony _harmony;

    public override void Load()
    {
        Instance = this;
        Log = base.Log;

        ToggleUiKey = Config.Bind("Keys", "ToggleUI", KeyCode.F8,
            "Opens/closes the BigScreen control panel.");

        UiFontSize = Config.Bind("UI", "FontSize", 16,
            new ConfigDescription("Font size for the control panel. The panel picks up a change the " +
                                  "next time it is drawn, so you can edit this while the game runs.",
                new AcceptableValueRange<int>(8, 40)));

        Volume = Config.Bind("Audio", "Volume", 0.8f,
            new ConfigDescription("Local playback volume for the screen's audio (0-1). Only affects you.",
                new AcceptableValueRange<float>(0f, 1f)));
        AudioMaxDistance = Config.Bind("Audio", "MaxDistance", 25f,
            new ConfigDescription("Distance in meters at which the screen's audio fades to silence.",
                new AcceptableValueRange<float>(5f, 200f)));
        AudioFullVolumeRadius = Config.Bind("Audio", "FullVolumeRadius", 1.5f,
            new ConfigDescription("Distance in meters within which the screen plays at full volume. This is " +
                                  "the main control over how local the sound feels: beyond it the volume " +
                                  "falls off as 1/distance, so halving this roughly halves the volume you " +
                                  "hear at any given spot. At the default 1.5 m you hear about a third of " +
                                  "full volume standing 4 m back. Raise it to fill more of the area.",
                new AcceptableValueRange<float>(0.5f, 20f)));

        ScreenWidthMeters = Config.Bind("Screen", "WidthMeters", 4f,
            new ConfigDescription("Physical width of the screen in the world (16:9, so height follows).",
                new AcceptableValueRange<float>(1f, 20f)));
        ScreenGroundClearance = Config.Bind("Screen", "GroundClearance", 0.6f,
            new ConfigDescription("How high the bottom edge of the picture sits above the spot the screen " +
                                  "was placed at, in meters. Lower this to bring the screen down; 0 puts it " +
                                  "on the ground and negative values sink it. The stand hides itself when " +
                                  "there is no gap left for it. Editing this file mid-game does nothing - " +
                                  "BepInEx does not re-read it - so use the panel's Raise/Lower buttons, " +
                                  "which write the value back here.",
                new AcceptableValueRange<float>(-2f, 5f)));
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
        ExtractorArgs = Config.Bind("YtDlp", "ExtractorArgs", "youtube:player_client=android",
            "Passed to yt-dlp as --extractor-args. YouTube's default player clients currently return no " +
            "playable formats at all (only storyboard images), which surfaces as 'Requested format is not " +
            "available'. The android client still serves format 18, the muxed H.264+AAC MP4 that Unity's " +
            "VideoPlayer needs. If YouTube blocks this client too, try tv / ios / web_safari / mweb, or " +
            "clear this to use yt-dlp's own defaults.");

        CookiesFromBrowser = Config.Bind("YtDlp", "CookiesFromBrowser", "",
            "Passed to yt-dlp as --cookies-from-browser. Leave empty to request anonymously. " +
            "YouTube blocks repeated anonymous requests from one address with 'Sign in to confirm " +
            "you're not a bot'; naming the browser you watch YouTube in (chrome, firefox, edge, " +
            "brave, vivaldi, opera, safari, chromium) lets yt-dlp reuse your signed-in session and " +
            "clears that. yt-dlp reads that browser's cookie database directly; nothing is sent " +
            "anywhere except to YouTube, exactly as your browser would.");

        DriftTolerance = Config.Bind("Sync", "DriftToleranceSeconds", 1.0f,
            new ConfigDescription("How far your playback may drift from the host's timeline before we re-seek.",
                new AcceptableValueRange<float>(0.25f, 10f)));
        GuestsCanControl = Config.Bind("Sync", "GuestsCanControl", false,
            "Host only: let guests with the mod load videos, play/pause, seek and move the screen.");
        AutoPlay = Config.Bind("Sync", "AutoPlay", true,
            "Host only: start playing as soon as a loaded video is ready.");

        Diagnostics = Config.Bind("Debug", "Diagnostics", false,
            "Write a verbose status line to the BepInEx log every couple of seconds.");

        AutoHost = Config.Bind("Dev", "AutoHost", false,
            "Host a save slot straight from the main menu, skipping Host Game / slot / player count. " +
            "Development convenience; it drives the game's own menu code and may break on a game update.");
        AutoHostSaveSlot = Config.Bind("Dev", "AutoHostSaveSlot", "",
            "Name of the save slot to host. Empty means the most recently played one. " +
            "If the name does not match, the log lists the slots that were found.");
        AutoHostPlayerCount = Config.Bind("Dev", "AutoHostPlayerCount", 3,
            new ConfigDescription("Player count for the hosted session.", new AcceptableValueRange<int>(2, 4)));
        AutoHostDelay = Config.Bind("Dev", "AutoHostDelaySeconds", 3f,
            new ConfigDescription("How long to wait after the main menu appears before hosting.",
                new AcceptableValueRange<float>(0f, 60f)));

        AutoLoadUrl = Config.Bind("Dev", "AutoLoadUrl", "",
            "Placed and loaded automatically once you are hosting. Empty disables it.");
        AutoDismissMenus = Config.Bind("Dev", "AutoDismissMenus", false,
            "Click through the splash and mic-check screens and open the host menu automatically. " +
            "Needed for an unattended test run; nothing else can get past those screens.");

        SpawnPosition = Config.Bind("Dev", "SpawnPosition", "",
            "Teleport to this pose once after entering a session, as \"x,y,z\" or \"x,y,z,yaw\". " +
            "Empty disables it. Use the panel's 'Set spawn here' button to fill this in from " +
            "where you are standing and which way you are looking. The yaw matters: the screen " +
            "is auto-placed in front of you, so without one it lands somewhere different on " +
            "every run.");

        ScreenPose = Config.Bind("Dev", "ScreenPose", "",
            "Place the screen at this exact pose on an automated run, as \"x,y,z,yaw\". Empty means " +
            "'4 m in front of wherever the player is looking', which lands somewhere different every " +
            "run. Fill it in to pin the screen to a spot you chose.");

        DevDirectUrl = Config.Bind("Dev", "DirectUrl", "",
            "Skip yt-dlp and hand this URL straight to the VideoPlayer. Must be a progressive MP4 " +
            "(H.264 + AAC). Use it to test video playback on its own, without running yt-dlp.");

        AutoLoadDelay = Config.Bind("Dev", "AutoLoadDelaySeconds", 6f,
            new ConfigDescription("How long to wait after entering the world before placing the screen and loading.",
                new AcceptableValueRange<float>(0f, 120f)));

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
