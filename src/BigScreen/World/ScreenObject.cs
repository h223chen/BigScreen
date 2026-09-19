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

    private Material _screenMaterial;
    private static readonly Color IdleColor = new Color(0.03f, 0.03f, 0.05f, 1f);

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

        // Frame: a slightly larger dark quad just behind the picture.
        var frame = MakeQuad("Frame", w + 0.16f, h + 0.16f, new Vector3(0f, h * 0.5f + 0.08f + 0.6f, 0.02f));
        frame.transform.SetParent(Root.transform, false);
        var frameMat = MakeMaterial(null, new Color(0.08f, 0.07f, 0.06f, 1f));
        frame.GetComponent<MeshRenderer>().material = frameMat;

        // Legs so it looks like it stands on the ground rather than floating.
        var leg = MakeQuad("Leg", 0.25f, 0.6f, new Vector3(0f, 0.3f, 0.02f));
        leg.transform.SetParent(Root.transform, false);
        leg.GetComponent<MeshRenderer>().material = frameMat;

        // Picture.
        var picture = MakeQuad("Picture", w, h, new Vector3(0f, h * 0.5f + 0.08f + 0.6f, 0f));
        picture.transform.SetParent(Root.transform, false);
        _screenMaterial = MakeMaterial(Texture, Color.white);
        picture.GetComponent<MeshRenderer>().material = _screenMaterial;

        // Audio: positional, gentle linear falloff, no doppler (the screen never moves).
        Audio = Root.AddComponent<AudioSource>();
        Audio.playOnAwake = false;
        Audio.spatialBlend = 1f;
        Audio.rolloffMode = AudioRolloffMode.Linear;
        Audio.minDistance = Mathf.Max(2f, w * 0.75f);
        Audio.maxDistance = Plugin.AudioMaxDistance.Value;
        Audio.dopplerLevel = 0f;
        Audio.spread = 45f;
        Audio.volume = Plugin.Volume.Value;
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

    public void ApplyAudioConfig()
    {
        if (Audio == null) return;
        Audio.maxDistance = Plugin.AudioMaxDistance.Value;
        Audio.volume = Plugin.Volume.Value;
    }

    // --- Construction helpers -------------------------------------------------------

    /// <summary>A quad whose front face points toward local -Z, centred at <paramref name="localPos"/>.</summary>
    private static GameObject MakeQuad(string name, float width, float height, Vector3 localPos)
    {
        var go = new GameObject(name);
        go.transform.localPosition = localPos;

        float hw = width * 0.5f, hh = height * 0.5f;
        var vertices = new Vector3[]
        {
            new Vector3(-hw, -hh, 0f),
            new Vector3( hw, -hh, 0f),
            new Vector3( hw,  hh, 0f),
            new Vector3(-hw,  hh, 0f),
        };
        var uv = new Vector2[]
        {
            new Vector2(0f, 0f),
            new Vector2(1f, 0f),
            new Vector2(1f, 1f),
            new Vector2(0f, 1f),
        };
        var normals = new Vector3[] { -Vector3.forward, -Vector3.forward, -Vector3.forward, -Vector3.forward };
        // Same convention as Unity's built-in Quad: the front face points toward local -Z,
        // so a viewer standing on the -Z side sees the picture the right way round. The
        // controller therefore orients the screen with its +Z pointing AWAY from the player.
        var triangles = new int[] { 0, 2, 1, 0, 3, 2 };

        var mesh = new Mesh();
        mesh.name = "BigScreen." + name;
        mesh.vertices = (Il2CppStructArray<Vector3>)vertices;
        mesh.uv = (Il2CppStructArray<Vector2>)uv;
        mesh.normals = (Il2CppStructArray<Vector3>)normals;
        mesh.triangles = (Il2CppStructArray<int>)triangles;
        mesh.RecalculateBounds();

        go.AddComponent<MeshFilter>().mesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;
        return go;
    }

    private static readonly string[] ShaderCandidates =
    {
        "Universal Render Pipeline/Unlit",
        "Unlit/Texture",
        "Unlit/Color",
        "Sprites/Default",
        "UI/Default",
        "Universal Render Pipeline/Lit",
        "Standard",
        "Legacy Shaders/Diffuse",
    };

    private static Shader _cachedShader;
    private static Material _cachedSceneTemplate;

    /// <summary>
    /// Finds a shader that exists in this build. Unlit is ideal (the screen is visible at
    /// night and never washed out by lighting); if nothing by name exists we clone a
    /// material that is already rendering in the scene, which is guaranteed to work in
    /// whatever pipeline the game uses.
    /// </summary>
    private static Material MakeMaterial(Texture tex, Color tint)
    {
        Material mat = null;

        if (_cachedShader == null)
        {
            foreach (var name in ShaderCandidates)
            {
                try
                {
                    var s = Shader.Find(name);
                    if (s != null) { _cachedShader = s; Plugin.Log.LogInfo($"Screen shader: {name}"); break; }
                }
                catch { }
            }
        }

        if (_cachedShader != null)
        {
            mat = new Material(_cachedShader);
        }
        else
        {
            if (_cachedSceneTemplate == null)
            {
                try
                {
                    var renderers = UnityEngine.Object.FindObjectsOfType<MeshRenderer>();
                    foreach (var r in renderers)
                    {
                        var m = r != null ? r.sharedMaterial : null;
                        if (m != null && m.shader != null) { _cachedSceneTemplate = m; break; }
                    }
                }
                catch { }
            }
            if (_cachedSceneTemplate == null)
                throw new InvalidOperationException("No usable shader found in this build (see docs/ARCHITECTURE.md 'Risk 3').");
            mat = new Material(_cachedSceneTemplate);
            Plugin.Log.LogWarning($"No known shader found; cloned scene material '{_cachedSceneTemplate.name}' ({_cachedSceneTemplate.shader.name}).");
        }

        mat.name = "BigScreen.Mat";
        // Cover both built-in ("_MainTex/_Color") and URP ("_BaseMap/_BaseColor") property names.
        if (tex != null)
        {
            mat.mainTexture = tex;
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);
            if (mat.HasProperty("_EmissionMap"))
            {
                mat.SetTexture("_EmissionMap", tex);
                mat.SetColor("_EmissionColor", Color.white);
                mat.EnableKeyword("_EMISSION");
            }
        }
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", tint);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", tint);
        return mat;
    }

    public void Dispose()
    {
        try
        {
            if (Root != null) UnityEngine.Object.Destroy(Root);
            if (Texture != null) { Texture.Release(); UnityEngine.Object.Destroy(Texture); }
        }
        catch (Exception e) { Plugin.Log.LogDebug($"Screen dispose: {e.Message}"); }
        Root = null;
        Texture = null;
        Audio = null;
    }
}
