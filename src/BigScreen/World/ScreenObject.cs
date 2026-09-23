using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;

namespace BigScreen.World;

/// <summary>
/// The physical screen: a 16:9 quad showing the video texture, a dark frame behind it,
/// and a positional AudioSource. Purely local - every modded client builds its own copy
/// at the position the host broadcast. Nothing here is a networked Mirror object, which
/// keeps vanilla clients completely unaffected.
///
/// Meshes are built by hand instead of GameObject.CreatePrimitive because built-in
/// primitive meshes may be stripped from an IL2CPP build. Shaders are looked up by name
/// with a fallback to cloning any material already rendering in the scene, since only
/// shaders the game itself uses survive the build.
/// </summary>
internal sealed class ScreenObject : IDisposable
{
    public GameObject Root { get; private set; }
    public AudioSource Audio { get; private set; }
    public RenderTexture Texture { get; private set; }
    /// <summary>The in-world controls beside the screen. Null when disabled or unbuildable.</summary>
    public ControlConsole Console { get; private set; }

    private Material _screenMaterial;
    private GameObject _frame;
    private GameObject _leg;
    private GameObject _picture;
    private float _pictureHeight;
    // What the rolloff curve and layout were last built for; NaN so the first apply runs.
    private float _falloffFull = float.NaN;
    private float _falloffMax = float.NaN;
    private float _appliedClearance = float.NaN;
    private static readonly Color IdleColor = new Color(0.03f, 0.03f, 0.05f, 1f);

    // Lowest the console panel may sit above the placement point, so it stays out of the floor.
    private const float MinConsoleHeight = 0.35f;

    public static ScreenObject Create(Vector3 position, float yawDegrees, float widthMeters, int texWidth, int texHeight)
    {
        var s = new ScreenObject();
        s.Build(position, yawDegrees, widthMeters, texWidth, texHeight);
        return s;
    }

    private void Build(Vector3 position, float yawDegrees, float widthMeters, int texWidth, int texHeight)
    {
        float w = widthMeters;
        float h = widthMeters * 9f / 16f;

        Root = new GameObject("BigScreen.Screen");
        UnityEngine.Object.DontDestroyOnLoad(Root);
        Root.transform.position = position;
        Root.transform.rotation = Quaternion.Euler(0f, yawDegrees, 0f);

        Texture = new RenderTexture(texWidth, texHeight, 0);
        Texture.name = "BigScreen.RT";
        Texture.Create();
        ClearToIdle();

        _pictureHeight = h;

        // Frame: a slightly larger dark quad just behind the picture.
        _frame = Primitives.MakeQuad("Frame", w + 0.16f, h + 0.16f, Vector3.zero);
        _frame.transform.SetParent(Root.transform, false);
        var frameMat = Primitives.MakeMaterial(null, new Color(0.08f, 0.07f, 0.06f, 1f));
        _frame.GetComponent<MeshRenderer>().material = frameMat;

        // Leg so it looks like it stands on the ground rather than floating. Built 1 m tall
        // and scaled to the configured clearance, so the stand always reaches the ground.
        _leg = Primitives.MakeQuad("Leg", 0.25f, 1f, Vector3.zero);
        _leg.transform.SetParent(Root.transform, false);
        _leg.GetComponent<MeshRenderer>().material = frameMat;

        // Picture.
        _picture = Primitives.MakeQuad("Picture", w, h, Vector3.zero);
        _picture.transform.SetParent(Root.transform, false);
        _screenMaterial = Primitives.MakeMaterial(Texture, Color.white);
        _picture.GetComponent<MeshRenderer>().material = _screenMaterial;

        // Layout is applied by the caller, which knows the host's clearance. Until then the
        // parts sit at the origin of Root.

        // Parented to Root, so moving the screen carries the console with it. A failure here
        // must not cost you the screen: the video is the point, the console is a convenience.
        if (Plugin.ShowConsole.Value)
        {
            try
            {
                Console = ControlConsole.Create(Root.transform, Plugin.ConsoleOffset.Value,
                    Mathf.Max(_appliedClearance + Plugin.ConsoleHeight.Value, MinConsoleHeight));
            }
            catch (Exception e)
            {
                Console = null;
                Plugin.Log.LogWarning($"Could not build the control console ({e.Message}); use F8 instead.");
            }
        }

        // Audio: positional, no doppler (the screen never moves). The falloff shape is set
        // up separately so a config change can re-apply it without rebuilding the screen.
        Audio = Root.AddComponent<AudioSource>();
        Audio.playOnAwake = false;
        Audio.spatialBlend = 1f;
        Audio.dopplerLevel = 0f;
        Audio.spread = 45f;
        Audio.volume = Plugin.Volume.Value;
        ApplyAudioConfig();
    }

    public void MoveTo(Vector3 position, float yawDegrees)
    {
        if (Root == null) return;
        Root.transform.position = position;
        Root.transform.rotation = Quaternion.Euler(0f, yawDegrees, 0f);
    }

    public void ClearToIdle()
    {
        if (Texture == null) return;
        var prev = RenderTexture.active;
        RenderTexture.active = Texture;
        GL.Clear(true, true, IdleColor);
        RenderTexture.active = prev;
    }

    /// <summary>
    /// Positions the picture, frame and leg for the configured ground clearance - how far
    /// the bottom edge of the picture sits above the point the screen was placed at.
    ///
    /// Re-applied every frame (cheap: it returns immediately unless the value changed) so
    /// the height can be dialled in from the config file while the game runs, without
    /// re-placing the screen or restarting.
    /// </summary>
    /// <param name="clearance">
    /// Height of the bottom edge above the placement point. Comes from the shared state, so
    /// a guest uses the host's value rather than their own config.
    /// </param>
    public void ApplyLayout(float clearance)
    {
        if (Root == null || _picture == null) return;

        if (Mathf.Approximately(clearance, _appliedClearance)) return;
        _appliedClearance = clearance;

        // Frame overhangs the picture by 0.08 m, so the picture centre sits that much above
        // the bottom of the frame.
        float centreY = clearance + 0.08f + _pictureHeight * 0.5f;
        _picture.transform.localPosition = new Vector3(0f, centreY, 0f);
        _frame.transform.localPosition = new Vector3(0f, centreY, 0.02f);

        // With the screen sitting on or below its anchor there is no gap for a stand.
        bool standVisible = clearance > 0.05f;
        _leg.SetActive(standVisible);
        if (standVisible)
        {
            _leg.transform.localPosition = new Vector3(0f, clearance * 0.5f, 0.02f);
            _leg.transform.localScale = new Vector3(1f, clearance, 1f);
        }

        // Keep the controls with the screen. Floored so that sinking the screen into the
        // ground does not bury the console with it.
        float consoleY = Mathf.Max(clearance + Plugin.ConsoleHeight.Value, MinConsoleHeight);
        Console?.SetHeight(consoleY);

        Plugin.Log.LogInfo($"Screen layout: bottom edge {clearance:F2}m above the placement point, "
                           + $"picture centre {centreY:F2}m, top {(clearance + 0.08f + _pictureHeight):F2}m, "
                           + $"console {consoleY:F2}m.");
    }

    public void ApplyAudioConfig()
    {
        if (Audio == null) return;
        Audio.volume = Plugin.Volume.Value;

        float max = Plugin.AudioMaxDistance.Value;
        // Full-volume radius has to stay meaningfully inside max distance, or there is no
        // room left for the falloff.
        float full = Mathf.Clamp(Plugin.AudioFullVolumeRadius.Value, 0.5f, max * 0.5f);

        // The volume slider re-applies this on every drag tick, so rebuilding the curve each
        // time would be pointless work and a log line per frame.
        if (Mathf.Approximately(full, _falloffFull) && Mathf.Approximately(max, _falloffMax)) return;
        _falloffFull = full;
        _falloffMax = max;

        Audio.minDistance = full;
        Audio.maxDistance = max;

        if (!TryApplyCustomFalloff(full, max))
        {
            // Same 1/distance shape, but Unity stops attenuating at maxDistance instead of
            // reaching zero, so distant players keep hearing a faint version of the video.
            Audio.rolloffMode = AudioRolloffMode.Logarithmic;
        }
    }

    /// <summary>
    /// Volume at a given distance: full inside <paramref name="full"/>, then 1/distance -
    /// how sound actually behaves - rescaled so it reaches exactly zero at
    /// <paramref name="max"/> rather than trailing off forever.
    ///
    /// Unity's built-in modes each get one half of this right. Linear reaches silence but is
    /// far too loud up close: with the old 3 m / 40 m settings a player standing 4 m from the
    /// screen still heard 97% of full volume, which is what made the sound feel like it was
    /// everywhere rather than coming from the screen. Logarithmic has the right shape but
    /// never reaches zero.
    /// </summary>
    private static float FalloffAt(float distance, float full, float max)
    {
        if (distance <= full) return 1f;
        if (distance >= max) return 0f;

        float floorTerm = full / max;            // what 1/distance still gives at max distance
        return ((full / distance) - floorTerm) / (1f - floorTerm);
    }

    /// <summary>
    /// Installs <see cref="FalloffAt"/> as the AudioSource's custom rolloff curve.
    ///
    /// Unity samples a custom rolloff curve over normalised distance, where time 1 is
    /// maxDistance (not minDistance to maxDistance). Sampling densely rather than computing
    /// tangents keeps the shape accurate and avoids the overshoot auto-tangents produce on a
    /// curve this steep.
    ///
    /// Returns false if the interop call is not available on this build, so the caller can
    /// fall back to a built-in mode rather than leaving the screen silent.
    /// </summary>
    private bool TryApplyCustomFalloff(float full, float max)
    {
        const int Samples = 24;

        try
        {
            var curve = new AnimationCurve();
            for (int i = 0; i <= Samples; i++)
            {
                float t = i / (float)Samples;
                curve.AddKey(t, FalloffAt(t * max, full, max));
            }
            // Pin the corner where full volume ends, which the even spacing above may miss.
            curve.AddKey(full / max, 1f);

            Audio.rolloffMode = AudioRolloffMode.Custom;
            Audio.SetCustomCurve(AudioSourceCurveType.CustomRolloff, curve);

            Plugin.Log.LogInfo(
                $"Audio falloff: 100% within {full:F1}m, {FalloffAt(4f, full, max) * 100f:F0}% at 4m, " +
                $"{FalloffAt(8f, full, max) * 100f:F0}% at 8m, silent at {max:F0}m.");
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"Custom audio falloff unavailable ({e.Message}); using logarithmic rolloff.");
            return false;
        }
    }

    // --- Construction helpers -------------------------------------------------------

    public void Dispose()
    {
        try
        {
            Console?.Dispose();
            Console = null;
            if (Root != null) UnityEngine.Object.Destroy(Root);
            if (Texture != null) { Texture.Release(); UnityEngine.Object.Destroy(Texture); }
        }
        catch (Exception e) { Plugin.Log.LogDebug($"Screen dispose: {e.Message}"); }
        Root = null;
        Texture = null;
        Audio = null;
    }
}
