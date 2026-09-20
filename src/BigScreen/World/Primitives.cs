using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;

namespace BigScreen.World;

/// <summary>
/// Mesh and material construction shared by the screen and the control console.
///
/// Meshes are built by hand instead of GameObject.CreatePrimitive because built-in
/// primitive meshes may be stripped from an IL2CPP build. Shaders are looked up by name
/// with a fallback to cloning any material already rendering in the scene, since only
/// shaders the game itself uses survive the build.
///
/// Every face points toward local -Z, matching Unity's built-in Quad, so a viewer standing
/// on the -Z side sees it the right way round.
/// </summary>
internal static class Primitives
{
    /// <summary>A flat rectangle centred on <paramref name="localPos"/>.</summary>
    public static GameObject MakeQuad(string name, float width, float height, Vector3 localPos)
    {
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
        // Wound so the front face points toward local -Z.
        var triangles = new int[] { 0, 2, 1, 0, 3, 2 };
        return Build(name, localPos, vertices, uv, triangles);
    }

    /// <summary>
    /// An isosceles triangle, used for the play and skip symbols. Pointing right means the
    /// apex is at +X; pointing left mirrors it, which also reverses the winding so the face
    /// still looks toward local -Z.
    /// </summary>
    public static GameObject MakeTriangle(string name, float width, float height, Vector3 localPos, bool pointsRight)
    {
        float hw = width * 0.5f, hh = height * 0.5f;
        float baseX = pointsRight ? -hw : hw;
        float apexX = pointsRight ? hw : -hw;

        var vertices = new Vector3[]
        {
            new Vector3(baseX, -hh, 0f),
            new Vector3(baseX,  hh, 0f),
            new Vector3(apexX,  0f, 0f),
        };
        var uv = new Vector2[] { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 0.5f) };
        var triangles = pointsRight ? new int[] { 0, 1, 2 } : new int[] { 0, 2, 1 };
        return Build(name, localPos, vertices, uv, triangles);
    }

    private static GameObject Build(string name, Vector3 localPos, Vector3[] vertices, Vector2[] uv, int[] triangles)
    {
        var go = new GameObject(name);
        go.transform.localPosition = localPos;

        var normals = new Vector3[vertices.Length];
        for (int i = 0; i < normals.Length; i++) normals[i] = -Vector3.forward;

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
    public static Material MakeMaterial(Texture tex, Color tint)
    {
        Material mat;

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
        SetTint(mat, tint);
        return mat;
    }

    /// <summary>
    /// Forces a draw order on stacked coplanar quads.
    ///
    /// The shader we end up with on this build is Sprites/Default, which is ZWrite Off in the
    /// Transparent queue. Without depth writes, Unity orders transparent renderers by camera
    /// distance, so quads a few millimetres apart swap order as the player walks and appear to
    /// flicker in and out. An explicit queue per layer pins the order regardless of distance.
    /// </summary>
    public static void SetDrawOrder(Material mat, int layer)
    {
        if (mat == null) return;
        // 3000 is Unity's Transparent queue; higher draws later, so on top.
        mat.renderQueue = 3000 + layer;
    }

    /// <summary>Recolours a material built by <see cref="MakeMaterial"/>, for hover feedback.</summary>
    public static void SetTint(Material mat, Color tint)
    {
        if (mat == null) return;
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", tint);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", tint);
    }
}
