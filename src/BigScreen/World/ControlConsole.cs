using System;
using System.Collections.Generic;
using UnityEngine;

namespace BigScreen.World;

/// <summary>
/// A physical control podium beside the screen: skip back, play/pause, skip forward, and a
/// paste bar that loads whatever YouTube URL is on the clipboard.
///
/// Why these controls and not a keyboard: the mod ships no assets, so there is no font to
/// draw letters with. Every symbol here is a mesh - a triangle for play, two bars for pause,
/// paired triangles for the skips - which needs no glyphs at all. A URL is far too long to
/// enter by clicking letters anyway, and people already have it on the clipboard from their
/// browser, so pasting is both easier and the only approach that avoids text rendering.
///
/// Aiming uses ray/plane maths rather than colliders and Physics.Raycast. A collider added
/// to the game's world would join its collision matrix and could block the player walking
/// into it; intersecting the console's own plane keeps the object inert. The trade-off is
/// no occlusion - you can aim through a wall - which is a fair price for not touching the
/// game's physics.
///
/// Local-only, like the screen: nothing here is a networked object, so a vanilla player
/// sees none of it. Pressing a button routes through the controller's normal request path,
/// so the host still decides what actually happens.
/// </summary>
internal sealed class ControlConsole : IDisposable
{
    /// <summary>
    /// What the console reports back to the player through the status lamp. With no font,
    /// colour is the only channel available for "something is happening".
    /// </summary>
    public enum Status { Idle, Working, Ready, Error }

    /// <summary>What a button does when pressed. Positions are in console-local meters.</summary>
    private sealed class Button
    {
        public string Name;
        public Rect Area;               // local x/y extents of the clickable face
        public Action<BigScreenController> Press;
        public readonly List<Material> Materials = new();
        public Color Idle;
        public Color Hover;
        public float FlashUntil;        // unscaled time; briefly lit after a press
        public Color FlashColor;
    }

    public GameObject Root { get; private set; }

    // The tilted face everything is mounted on. Separate from Root so the support post can
    // hang straight down while the panel leans back, and so aiming has one plane to test.
    private GameObject _face;
    private float _offsetMeters;
    private readonly List<Button> _buttons = new();
    private Button _hovered;
    private GameObject _playTriangle;
    private GameObject _pauseBars;
    private bool _showingPause;
    private Material _lamp;
    private Status _status = Status.Idle;
    // Last aim result, purely for diagnostics: did the ray meet the panel, and where.
    private bool _planeHit;
    private Vector2 _planeLocal;

    private static readonly Color BodyColor = new Color(0.10f, 0.10f, 0.12f, 1f);
    private static readonly Color ButtonIdle = new Color(0.20f, 0.21f, 0.25f, 1f);
    private static readonly Color ButtonHover = new Color(0.40f, 0.62f, 0.95f, 1f);
    private static readonly Color PasteIdle = new Color(0.22f, 0.24f, 0.30f, 1f);
    private static readonly Color PasteHover = new Color(0.40f, 0.62f, 0.95f, 1f);
    // Pulsed over PasteIdle while nothing is loaded, so the bar advertises itself.
    private static readonly Color PasteInvite = new Color(0.30f, 0.44f, 0.60f, 1f);

    private static readonly Color LampIdle = new Color(0.24f, 0.25f, 0.28f, 1f);
    private static readonly Color LampWorking = new Color(0.95f, 0.65f, 0.20f, 1f);
    private static readonly Color LampReady = new Color(0.35f, 0.85f, 0.45f, 1f);
    private static readonly Color LampError = new Color(0.90f, 0.30f, 0.25f, 1f);
    private static readonly Color SymbolColor = new Color(0.92f, 0.93f, 0.96f, 1f);
    private static readonly Color OkFlash = new Color(0.45f, 0.95f, 0.5f, 1f);
    private static readonly Color BadFlash = new Color(0.95f, 0.55f, 0.2f, 1f);

    // Leans the panel back from vertical so it faces up toward someone standing at it.
    private const float TiltDegrees = 30f;
    // Long enough to notice while walking away from the console.
    private const float FlashSeconds = 1.2f;
    private const float PanelWidth = 1.4f;
    private const float PanelHeight = 0.62f;

    // Draw-order layers; see Primitives.SetDrawOrder. Also separated in z so that a build
    // whose shader does write depth sorts them correctly too.
    private const int LayerBody = 0;
    private const int LayerFace = 1;
    private const int LayerSymbol = 2;
    private const float FaceZ = -0.01f;
    private const float SymbolZ = -0.02f;

    /// <summary>
    /// Builds the console beside the screen. <paramref name="parent"/> is the screen root,
    /// so the console follows the screen when it is moved without any extra bookkeeping.
    /// </summary>
    public static ControlConsole Create(Transform parent, float offsetMeters, float heightMeters)
    {
        var c = new ControlConsole();
        c.Build(parent, offsetMeters, heightMeters);
        return c;
    }

    private void Build(Transform parent, float offsetMeters, float heightMeters)
    {
        Root = new GameObject("BigScreen.Console");
        Root.transform.SetParent(parent, false);
        _offsetMeters = offsetMeters;
        Root.transform.localPosition = new Vector3(offsetMeters, heightMeters, 0f);

        // Tilted back so it reads as a podium rather than a poster. The sign matters: a
        // positive X euler pitches the nose down, which leans the top of the panel away from
        // the viewer and turns the face upward. Negative does the opposite and aims the whole
        // thing at the floor, where nothing can be seen or aimed at.
        _face = new GameObject("ConsoleFace");
        _face.transform.SetParent(Root.transform, false);
        _face.transform.localRotation = Quaternion.Euler(TiltDegrees, 0f, 0f);

        var body = Primitives.MakeQuad("ConsoleBody", PanelWidth, PanelHeight, Vector3.zero);
        body.transform.SetParent(_face.transform, false);
        var bodyMat = Primitives.MakeMaterial(null, BodyColor);
        Primitives.SetDrawOrder(bodyMat, LayerBody);
        body.GetComponent<MeshRenderer>().material = bodyMat;

        float row = 0.12f;              // vertical centre of the transport row
        float size = 0.26f;             // clickable square per transport button
        float gap = 0.32f;

        AddTransport("Back10", new Vector2(-gap, row), size, pointsRight: false, doubled: true,
                     c => c.UserSeekRelative(-10));
        AddPlayPause(new Vector2(0f, row), size);
        AddTransport("Forward10", new Vector2(gap, row), size, pointsRight: true, doubled: true,
                     c => c.UserSeekRelative(10));
        AddPasteBar(new Vector2(0f, -0.18f), PanelWidth - 0.16f, 0.20f);
        AddStatusLamp(new Vector2(0f, 0.26f), PanelWidth - 0.16f, 0.045f);

        var p = Root.transform.position;
        string key = Plugin.InteractKey.Value == KeyCode.None
            ? "use the game's interact button"
            : $"press {Plugin.InteractKey.Value} or interact";
        Plugin.Log.LogInfo($"Console built at ({p.x:F2}, {p.y:F2}, {p.z:F2}); "
                           + $"aim within {Plugin.ConsoleReach.Value:F0}m and {key}.");
    }

    /// <summary>A skip button: one or two triangles on a square face.</summary>
    private void AddTransport(string name, Vector2 centre, float size, bool pointsRight, bool doubled,
                              Action<BigScreenController> press)
    {
        var button = NewButton(name, centre, size, size, ButtonIdle, ButtonHover, press);

        float tri = size * 0.40f;
        if (doubled)
        {
            AddSymbol(button, Primitives.MakeTriangle(name + "A", tri, tri,
                new Vector3(centre.x - tri * 0.30f, centre.y, SymbolZ), pointsRight));
            AddSymbol(button, Primitives.MakeTriangle(name + "B", tri, tri,
                new Vector3(centre.x + tri * 0.30f, centre.y, SymbolZ), pointsRight));
        }
        else
        {
            AddSymbol(button, Primitives.MakeTriangle(name + "A", tri, tri,
                new Vector3(centre.x, centre.y, SymbolZ), pointsRight));
        }
    }

    /// <summary>
    /// Play/pause carries both symbols and shows one at a time, because rebuilding a mesh
    /// every time playback is toggled would churn allocations for no reason.
    /// </summary>
    private void AddPlayPause(Vector2 centre, float size)
    {
        var button = NewButton("PlayPause", centre, size, size, ButtonIdle, ButtonHover,
                               c => c.UserTogglePlay());

        float tri = size * 0.45f;
        _playTriangle = Primitives.MakeTriangle("PlaySymbol", tri, tri,
            new Vector3(centre.x, centre.y, SymbolZ), pointsRight: true);
        AddSymbol(button, _playTriangle);

        _pauseBars = new GameObject("PauseSymbol");
        _pauseBars.transform.SetParent(_face.transform, false);
        _pauseBars.transform.localPosition = Vector3.zero;
        float barW = tri * 0.28f;
        foreach (float dx in new[] { -tri * 0.26f, tri * 0.26f })
        {
            var bar = Primitives.MakeQuad("PauseBar", barW, tri,
                new Vector3(centre.x + dx, centre.y, SymbolZ));
            bar.transform.SetParent(_pauseBars.transform, false);
            var mat = Primitives.MakeMaterial(null, SymbolColor);
            Primitives.SetDrawOrder(mat, LayerSymbol);
            bar.GetComponent<MeshRenderer>().material = mat;
            button.Materials.Add(mat);
        }
        _pauseBars.SetActive(false);
    }

    /// <summary>
    /// The paste bar, with a "drop something in" glyph so it is not just a blank strip.
    ///
    /// An arrow pointing down into a tray is about the only paste idea that survives having
    /// no font: a stem, a head and a base line, all meshes.
    /// </summary>
    private void AddPasteBar(Vector2 centre, float width, float height)
    {
        var button = NewButton("Paste", centre, width, height, PasteIdle, PasteHover, PressPaste);

        var stem = Primitives.MakeQuad("PasteStem", 0.030f, 0.070f,
            new Vector3(centre.x, centre.y + 0.040f, SymbolZ));
        AddSymbol(button, stem);

        // MakeTriangle only builds left/right, so rotate a right-pointing one to face down
        // rather than adding another mesh variant.
        var head = Primitives.MakeTriangle("PasteHead", 0.060f, 0.110f,
            new Vector3(centre.x, centre.y - 0.012f, SymbolZ), pointsRight: true);
        AddSymbol(button, head);
        head.transform.localRotation = Quaternion.Euler(0f, 0f, -90f);

        var tray = Primitives.MakeQuad("PasteTray", 0.190f, 0.022f,
            new Vector3(centre.x, centre.y - 0.068f, SymbolZ));
        AddSymbol(button, tray);
    }

    /// <summary>
    /// A strip across the top that reports what the mod is doing: grey idle, amber working,
    /// green ready, red failed. Pressing paste and seeing nothing change was the single most
    /// confusing thing about the first version.
    /// </summary>
    private void AddStatusLamp(Vector2 centre, float width, float height)
    {
        var lamp = Primitives.MakeQuad("StatusLamp", width, height,
            new Vector3(centre.x, centre.y, SymbolZ));
        lamp.transform.SetParent(_face.transform, false);
        _lamp = Primitives.MakeMaterial(null, LampIdle);
        Primitives.SetDrawOrder(_lamp, LayerSymbol);
        lamp.GetComponent<MeshRenderer>().material = _lamp;
    }

    private Button NewButton(string name, Vector2 centre, float width, float height,
                             Color idle, Color hover, Action<BigScreenController> press)
    {
        var face = Primitives.MakeQuad(name, width, height, new Vector3(centre.x, centre.y, FaceZ));
        face.transform.SetParent(_face.transform, false);
        var mat = Primitives.MakeMaterial(null, idle);
        Primitives.SetDrawOrder(mat, LayerFace);
        face.GetComponent<MeshRenderer>().material = mat;

        var button = new Button
        {
            Name = name,
            Area = new Rect(centre.x - width * 0.5f, centre.y - height * 0.5f, width, height),
            Press = press,
            Idle = idle,
            Hover = hover,
        };
        button.Materials.Add(mat);
        _buttons.Add(button);
        return button;
    }

    private void AddSymbol(Button button, GameObject symbol)
    {
        symbol.transform.SetParent(_face.transform, false);
        var mat = Primitives.MakeMaterial(null, SymbolColor);
        Primitives.SetDrawOrder(mat, LayerSymbol);
        symbol.GetComponent<MeshRenderer>().material = mat;
        // Symbols are not retinted on hover; only the face is. Kept on the button so the
        // press flash can light the whole thing up.
        button.Materials.Add(mat);
    }

    /// <summary>
    /// Loads whatever is on the clipboard. Unity's systemCopyBuffer is the only clipboard
    /// access available without extra assemblies.
    /// </summary>
    private void PressPaste(BigScreenController controller)
    {
        string clip;
        try { clip = GUIUtility.systemCopyBuffer; }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"Could not read the clipboard: {e.Message}");
            Flash("Paste", BadFlash);
            return;
        }

        clip = (clip ?? "").Trim();
        if (!Resolve.YtDlp.LooksLikeUrl(clip))
        {
            // No font, so the console itself can only answer in colour; the F8 panel and the
            // log carry the actual reason.
            controller.ReportConsoleError(string.IsNullOrEmpty(clip)
                ? "Clipboard is empty. Copy a YouTube link first, then press paste."
                : "Clipboard does not hold a link (it must start with http:// or https://).");
            Flash("Paste", BadFlash);
            return;
        }

        Plugin.Log.LogInfo($"Console: pasting {clip}");
        Flash("Paste", OkFlash);
        controller.UserLoad(clip);
    }

    private void Flash(string buttonName, Color color)
    {
        foreach (var b in _buttons)
        {
            if (b.Name != buttonName) continue;
            b.FlashUntil = Time.unscaledTime + FlashSeconds;
            b.FlashColor = color;
        }
    }

    /// <summary>
    /// Per-frame aiming and input. <paramref name="aimValid"/> is false when the player's
    /// camera could not be found, in which case the console simply shows no hover.
    /// </summary>
    public void Tick(BigScreenController controller, bool aimValid, Vector3 aimOrigin, Vector3 aimDirection,
                     bool playing, Status status, float reachMeters, bool pressed)
    {
        if (Root == null) return;

        _status = status;
        ShowPauseSymbol(playing);
        UpdateLamp();

        var hit = aimValid ? ButtonUnderAim(aimOrigin, aimDirection, reachMeters) : null;
        if (!ReferenceEquals(hit, _hovered))
        {
            _hovered = hit;
            if (hit != null) Plugin.Log.LogInfo($"Console: aiming at {hit.Name}.");
        }

        foreach (var b in _buttons) Repaint(b);

        if (!pressed) return;

        // Logged even with nothing aimed at, because "the key does nothing" has three very
        // different causes and only this tells them apart: the key never arriving, the ray
        // missing the panel, or the ray landing on the panel but between buttons.
        if (_hovered == null)
        {
            Plugin.Log.LogInfo($"Console: interact pressed, nothing aimed at. {DescribeAim(aimOrigin)}");
            return;
        }

        var target = _hovered;
        Plugin.Log.LogInfo($"Console: {target.Name} pressed.");
        if (target.Name != "Paste") Flash(target.Name, OkFlash);
        try { target.Press(controller); }
        catch (Exception e) { Plugin.Log.LogError($"Console button {target.Name} failed: {e}"); }
    }

    /// <summary>
    /// Moves the panel up or down. The screen owns this: the console is pinned relative to
    /// the bottom edge of the picture, so raising or lowering the screen carries the controls
    /// with it instead of leaving them stranded at a height nobody can reach.
    /// </summary>
    public void SetHeight(float localY)
    {
        if (Root == null) return;
        var p = Root.transform.localPosition;
        if (Mathf.Approximately(p.y, localY)) return;
        Root.transform.localPosition = new Vector3(_offsetMeters, localY, p.z);
    }

    /// <summary>True when the player is aiming at any button, so the caller can show a prompt.</summary>
    public bool IsAimed => _hovered != null;

    /// <summary>
    /// Distance and hover state for the diagnostics line. Without this there is no way to
    /// tell "too far away" apart from "aim is not landing on it" from a log alone.
    /// </summary>
    public string DescribeAim(Vector3 aimOrigin)
    {
        if (Root == null) return "none";
        float d = Vector3.Distance(aimOrigin, Root.transform.position);
        string where = _planeHit ? $"panel({_planeLocal.x:F2},{_planeLocal.y:F2})" : "off-panel";
        return $"{d:F1}m {where} {(_hovered != null ? _hovered.Name : "-")}";
    }

    private void UpdateLamp()
    {
        if (_lamp == null) return;

        Color c = _status switch
        {
            Status.Working => LampWorking,
            Status.Ready => LampReady,
            Status.Error => LampError,
            _ => LampIdle,
        };
        // Breathing while working says "still going" rather than "stuck".
        if (_status == Status.Working)
            c = Color.Lerp(c * 0.45f, c, Mathf.PingPong(Time.unscaledTime * 1.6f, 1f));

        Primitives.SetTint(_lamp, c);
    }

    private void Repaint(Button b)
    {
        Color target = b.Idle;
        if (Time.unscaledTime < b.FlashUntil) target = b.FlashColor;
        else if (ReferenceEquals(b, _hovered)) target = b.Hover;
        else if (b.Name == "Paste" && _status == Status.Idle)
            target = Color.Lerp(PasteIdle, PasteInvite, Mathf.PingPong(Time.unscaledTime * 0.8f, 1f));

        // Symbol materials are shared into the button so a flash lights everything, but at
        // rest they must go back to white rather than the face colour.
        for (int i = 0; i < b.Materials.Count; i++)
        {
            bool isFace = i == 0;
            Color c = Time.unscaledTime < b.FlashUntil ? target : (isFace ? target : SymbolColor);
            Primitives.SetTint(b.Materials[i], c);
        }
    }

    private void ShowPauseSymbol(bool playing)
    {
        if (playing == _showingPause) return;
        _showingPause = playing;
        // Showing the pause bars while playing is the usual convention: the button says what
        // pressing it will do.
        if (_playTriangle != null) _playTriangle.SetActive(!playing);
        if (_pauseBars != null) _pauseBars.SetActive(playing);
    }

    /// <summary>
    /// Intersects the aim ray with the console's face and returns the button it lands on.
    ///
    /// The face lies in the console's local XY plane and looks along local -Z, so the plane
    /// normal in world space is -Root.forward.
    /// </summary>
    private Button ButtonUnderAim(Vector3 origin, Vector3 direction, float reachMeters)
    {
        _planeHit = false;

        var plane = _face.transform;
        Vector3 normal = -plane.forward;

        float denom = Vector3.Dot(direction, normal);
        // Facing the back of the panel, or exactly edge-on: nothing to aim at.
        if (denom >= -0.0001f) return null;

        float t = Vector3.Dot(plane.position - origin, normal) / denom;
        if (t < 0f || t > reachMeters) return null;

        Vector3 local = plane.InverseTransformPoint(origin + direction * t);
        _planeLocal = new Vector2(local.x, local.y);
        // Within the panel's own extents, even if between buttons - that distinction is what
        // makes a miss diagnosable.
        _planeHit = Mathf.Abs(local.x) <= PanelWidth * 0.5f && Mathf.Abs(local.y) <= PanelHeight * 0.5f;

        foreach (var b in _buttons)
        {
            if (b.Area.Contains(_planeLocal)) return b;
        }
        return null;
    }

    public void Dispose()
    {
        try { if (Root != null) UnityEngine.Object.Destroy(Root); }
        catch (Exception e) { Plugin.Log.LogDebug($"Console dispose: {e.Message}"); }
        Root = null;
        _face = null;
        _buttons.Clear();
        _hovered = null;
        _playTriangle = null;
        _pauseBars = null;
    }
}
