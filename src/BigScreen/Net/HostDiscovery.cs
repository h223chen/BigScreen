using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Dissonance;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace BigScreen.Net;

/// <summary>
/// Answers one question for a guest before it says anything over Mirror: does the host have
/// BigScreen?
///
/// Why this exists: Mirror disconnects any peer that sends it a message id it has no handler
/// for. A vanilla host has no handler for ours, so a modded guest that greets a vanilla host
/// over Mirror is kicked mid-join and left on a black screen. The same applies in reverse, so
/// the host cannot simply greet everyone over Mirror either.
///
/// So the host announces itself through Dissonance, the game's voice library, instead. Dissonance
/// has text chat addressed to named rooms, and it only delivers room text to players who have
/// joined that room. The host posts a short line to a private room every couple of seconds;
/// modded guests join the room and listen; vanilla players never join it, so they never receive
/// anything. A vanilla host's Dissonance server relays nothing, because a vanilla host never
/// posts. A guest that joins a room on a vanilla host is ordinary Dissonance traffic.
///
/// Failure is safe by construction: if the announcement never arrives, for any reason, the guest
/// stays silent on Mirror. The screen does not sync, but nobody is kicked.
/// </summary>
internal sealed class HostDiscovery
{
    /// <summary>
    /// The private room. Dissonance turns room names into 16-bit ids, so a collision with one
    /// of the game's own voice rooms is possible in principle, but unlikely.
    /// </summary>
    public const string RoomName = "BigScreen.Discovery";

    private const string Prefix = "BIGSCREEN";
    private const float AnnounceSeconds = 2f;
    private const float RetrySeconds = 2f;

    /// <summary>Guest: seconds without hearing the host before we report "host has no mod".</summary>
    public const float GiveUpSeconds = 15f;

    public bool HostHasMod { get; private set; }
    public string HostVersion { get; private set; } = "";
    public byte HostProtocol { get; private set; }

    /// <summary>True once we know we cannot listen at all (API mismatch), so the UI can say so.</summary>
    public bool ListenFailed { get; private set; }

    /// <summary>Unscaled time the guest started waiting; 0 when not waiting.</summary>
    public float WaitingSince { get; private set; }

    private DissonanceComms _comms;
    private IntPtr _commsPtr = IntPtr.Zero;
    private RoomMembership _membership;
    private bool _joined;
    private bool _subscribed;
    private float _nextAttempt;
    private float _nextAnnounce;
    private bool _loggedGiveUp;
    private bool _loggedAnnounceError;

    // Strong references: the IL2CPP side holds a pointer to a trampoline into these.
    private Delegate _managedHandler;
    private object _il2cppHandler;

    /// <summary>Call every frame while in a session.</summary>
    public void Tick(bool isHost)
    {
        float now = Time.unscaledTime;
        if (!isHost && WaitingSince <= 0f) WaitingSince = now;

        // Finding Dissonance, subscribing and joining are retried on a slow cadence; they
        // fail harmlessly for the first moments of a session while Dissonance starts up.
        if (now >= _nextAttempt)
        {
            _nextAttempt = now + RetrySeconds;
            if (EnsureComms() && !isHost)
            {
                if (!_subscribed && !ListenFailed) Subscribe();
                if (!_joined) Join();
            }
        }

        if (isHost)
        {
            Announce(now);
            return;
        }

        if (!HostHasMod && !_loggedGiveUp && now - WaitingSince > GiveUpSeconds)
        {
            _loggedGiveUp = true;
            Plugin.Log.LogInfo(ListenFailed
                ? "Could not listen for the host's BigScreen announcement (see the error above). Staying silent on Mirror so the host cannot kick us."
                : $"No BigScreen announcement from the host after {GiveUpSeconds:F0} s. The host probably does not have the mod; staying silent so it cannot kick us.");
        }
    }

    /// <summary>Guest: we have waited long enough to say the host most likely has no mod.</summary>
    public bool GaveUp => !HostHasMod && WaitingSince > 0f && Time.unscaledTime - WaitingSince > GiveUpSeconds;

    public void Reset()
    {
        try
        {
            if (_joined && _comms != null && _comms.Rooms != null)
                _comms.Rooms.Leave(_membership);
        }
        catch (Exception e) { Plugin.Log.LogDebug($"Leaving discovery room: {e.Message}"); }

        Unsubscribe();
        _comms = null;
        _commsPtr = IntPtr.Zero;
        _joined = false;
        HostHasMod = false;
        HostVersion = "";
        HostProtocol = 0;
        WaitingSince = 0f;
        _nextAttempt = 0f;
        _nextAnnounce = 0f;
        _loggedGiveUp = false;
    }

    // --- Host ----------------------------------------------------------------------------

    private void Announce(float now)
    {
        if (now < _nextAnnounce || _comms == null) return;
        _nextAnnounce = now + AnnounceSeconds;
        try
        {
            _comms.Text.Send(RoomName, string.Format(CultureInfo.InvariantCulture,
                "{0}|{1}|host|{2}", Prefix, Protocol.Version, Plugin.Version));
            _loggedAnnounceError = false;
        }
        catch (Exception e)
        {
            // Normal for the first second or two, before Dissonance has finished its handshake.
            if (!_loggedAnnounceError)
            {
                _loggedAnnounceError = true;
                Plugin.Log.LogDebug($"Discovery announce not sent yet: {e.Message}");
            }
        }
    }

    // --- Guest ---------------------------------------------------------------------------

    private void Join()
    {
        try
        {
            _membership = _comms.Rooms.Join(RoomName);
            _joined = true;
            Plugin.Log.LogInfo("Listening for the host's BigScreen announcement.");
        }
        catch (Exception e)
        {
            Plugin.Log.LogDebug($"Joining discovery room failed, will retry: {e.Message}");
        }
    }

    /// <summary>
    /// Subscribes to Dissonance's text event. Done by reflection for two reasons: Dissonance's
    /// own docs spell the event both MessageReceived and MessageRecieved, and IL2CPP interop
    /// exposes events as add_/remove_ methods that take an IL2CPP delegate, which we have to
    /// build from a managed one.
    /// </summary>
    private void Subscribe()
    {
        try
        {
            var text = _comms.Text;
            var add = FindAccessor(text, "add_");
            if (add == null)
                throw new MissingMethodException("TextChat has no add_MessageReceived / add_MessageRecieved.");

            var delegateType = add.GetParameters()[0].ParameterType;
            Action<TextMessage> managed = OnText;

            var convert = typeof(DelegateSupport).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.Name == nameof(DelegateSupport.ConvertDelegate) && m.IsGenericMethodDefinition
                            && m.GetParameters().Length == 1);
            var il2cpp = convert.MakeGenericMethod(delegateType).Invoke(null, new object[] { managed });
            if (il2cpp == null) throw new InvalidOperationException("Delegate conversion returned null.");

            add.Invoke(text, new[] { il2cpp });
            _managedHandler = managed;
            _il2cppHandler = il2cpp;
            _subscribed = true;
        }
        catch (Exception e)
        {
            ListenFailed = true;
            Plugin.Log.LogError("Cannot listen to Dissonance text chat, so BigScreen cannot tell whether the host " +
                                "has the mod. Sync with the host is disabled to keep you from being kicked. " +
                                "If you know the host has BigScreen, set Sync.AssumeHostHasMod = true. Details: " + e);
        }
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        try
        {
            var text = _comms?.Text;
            var remove = text != null ? FindAccessor(text, "remove_") : null;
            remove?.Invoke(text, new[] { _il2cppHandler });
        }
        catch (Exception e) { Plugin.Log.LogDebug($"Unsubscribing from text chat: {e.Message}"); }
        _subscribed = false;
        _managedHandler = null;
        _il2cppHandler = null;
    }

    private static MethodInfo FindAccessor(object target, string prefix)
    {
        var type = target.GetType();
        return type.GetMethod(prefix + "MessageReceived") ?? type.GetMethod(prefix + "MessageRecieved");
    }

    private void OnText(TextMessage message)
    {
        try
        {
            // Only the prefix is checked. It is distinctive, and it avoids depending on how
            // Dissonance reports the recipient room on this build.
            var body = message.Message ?? "";
            if (!body.StartsWith(Prefix + "|", StringComparison.Ordinal)) return;

            // BIGSCREEN|<protocol>|host|<mod version>
            var parts = body.Split('|');
            if (parts.Length < 4 || parts[2] != "host") return;
            byte.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var protocol);

            if (!HostHasMod)
            {
                HostHasMod = true;
                HostProtocol = protocol;
                HostVersion = parts[3];
                Plugin.Log.LogInfo($"Host has BigScreen v{HostVersion} (protocol {protocol}); announced by '{message.Sender}'.");
                if (protocol != Protocol.Version)
                    Plugin.Log.LogWarning($"Host uses BigScreen protocol {protocol}, we use {Protocol.Version}. " +
                                          "The screen will not sync until you both run the same BigScreen version.");
            }
        }
        catch (Exception e) { Plugin.Log.LogDebug($"Discovery text handler: {e.Message}"); }
    }

    // --- Plumbing --------------------------------------------------------------------------

    /// <summary>
    /// Finds the game's DissonanceComms, the same way BigDJ does. Re-resolves when the object
    /// changes (scene reload), which also means re-subscribing and re-joining.
    /// </summary>
    private bool EnsureComms()
    {
        DissonanceComms found = null;
        try
        {
            var wm = WorldManager.instance;
            if (wm != null) found = wm.dissonanceComms;
        }
        catch { }
        if (found == null)
        {
            try { found = UnityEngine.Object.FindObjectOfType<DissonanceComms>(); }
            catch { }
        }
        if (found == null) return false;

        if (found.Pointer != _commsPtr)
        {
            if (_comms != null) Plugin.Log.LogInfo("Dissonance comms changed; re-attaching discovery.");
            Unsubscribe();
            _comms = found;
            _commsPtr = found.Pointer;
            _joined = false;
            ListenFailed = false;
        }
        return true;
    }
}
