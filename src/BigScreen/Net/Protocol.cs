using Mirror;
using UnityEngine;

namespace BigScreen.Net;

/// <summary>
/// Wire format for BigScreen messages. Every message starts with our Mirror message id
/// (written by <see cref="MirrorChannel"/>), then a protocol version byte, then a kind.
///
/// Serialization uses Mirror's own NetworkWriter/NetworkReader primitives so we never
/// hand-roll byte packing and stay compatible with Mirror's batching and MTU handling.
/// The extension classes are called statically because the IL2CPP interop proxies do not
/// always expose them as C# extension methods.
/// </summary>
internal static class Protocol
{
    // 2: added ScreenClearance / ScreenWidth. A v1 peer is rejected with a clear
    // warning rather than misreading the trailing bytes.
    public const byte Version = 2;

    public enum Kind : byte
    {
        Hello = 1,     // client -> host: "I have the mod (version X)"
        State = 2,     // host -> clients: full authoritative state
        Request = 3,   // client -> host: please do X (only honoured if GuestsCanControl)
    }

    public enum Action : byte
    {
        Load = 1,
        Play = 2,
        Pause = 3,
        SeekTo = 4,
        Stop = 5,
        Place = 6,
        Remove = 7,
    }

    public static void WriteHeader(NetworkWriter w, Kind kind)
    {
        NetworkWriterExtensions.WriteByte(w, Version);
        NetworkWriterExtensions.WriteByte(w, (byte)kind);
    }

    /// <summary>Returns false if the message is from an incompatible protocol version.</summary>
    public static bool ReadHeader(NetworkReader r, out Kind kind)
    {
        kind = 0;
        byte version = NetworkReaderExtensions.ReadByte(r);
        if (version != Version) return false;
        kind = (Kind)NetworkReaderExtensions.ReadByte(r);
        return true;
    }

    public static void WriteHello(NetworkWriter w, string modVersion)
    {
        WriteHeader(w, Kind.Hello);
        NetworkWriterExtensions.WriteString(w, modVersion ?? "");
    }

    public static string ReadHello(NetworkReader r) => NetworkReaderExtensions.ReadString(r);

    public static void WriteState(NetworkWriter w, SyncState s)
    {
        WriteHeader(w, Kind.State);
        NetworkWriterExtensions.WriteInt(w, s.Revision);
        NetworkWriterExtensions.WriteBool(w, s.HasScreen);
        NetworkWriterExtensions.WriteVector3(w, s.ScreenPosition);
        NetworkWriterExtensions.WriteFloat(w, s.ScreenYaw);
        NetworkWriterExtensions.WriteString(w, s.VideoUrl ?? "");
        NetworkWriterExtensions.WriteString(w, s.Title ?? "");
        NetworkWriterExtensions.WriteBool(w, s.Playing);
        NetworkWriterExtensions.WriteDouble(w, s.AnchorNetTime);
        NetworkWriterExtensions.WriteDouble(w, s.AnchorVideoTime);
        NetworkWriterExtensions.WriteBool(w, s.GuestsCanControl);
        NetworkWriterExtensions.WriteFloat(w, s.ScreenClearance);
        NetworkWriterExtensions.WriteFloat(w, s.ScreenWidth);
    }

    public static SyncState ReadState(NetworkReader r)
    {
        var s = new SyncState();
        s.Revision = NetworkReaderExtensions.ReadInt(r);
        s.HasScreen = NetworkReaderExtensions.ReadBool(r);
        s.ScreenPosition = NetworkReaderExtensions.ReadVector3(r);
        s.ScreenYaw = NetworkReaderExtensions.ReadFloat(r);
        s.VideoUrl = NetworkReaderExtensions.ReadString(r);
        s.Title = NetworkReaderExtensions.ReadString(r);
        s.Playing = NetworkReaderExtensions.ReadBool(r);
        s.AnchorNetTime = NetworkReaderExtensions.ReadDouble(r);
        s.AnchorVideoTime = NetworkReaderExtensions.ReadDouble(r);
        s.GuestsCanControl = NetworkReaderExtensions.ReadBool(r);
        s.ScreenClearance = NetworkReaderExtensions.ReadFloat(r);
        s.ScreenWidth = NetworkReaderExtensions.ReadFloat(r);
        return s;
    }

    public static void WriteRequest(NetworkWriter w, Request req)
    {
        WriteHeader(w, Kind.Request);
        NetworkWriterExtensions.WriteByte(w, (byte)req.Action);
        NetworkWriterExtensions.WriteString(w, req.Text ?? "");
        NetworkWriterExtensions.WriteDouble(w, req.Value);
        NetworkWriterExtensions.WriteVector3(w, req.Position);
        NetworkWriterExtensions.WriteFloat(w, req.Yaw);
    }

    public static Request ReadRequest(NetworkReader r)
    {
        var req = new Request();
        req.Action = (Action)NetworkReaderExtensions.ReadByte(r);
        req.Text = NetworkReaderExtensions.ReadString(r);
        req.Value = NetworkReaderExtensions.ReadDouble(r);
        req.Position = NetworkReaderExtensions.ReadVector3(r);
        req.Yaw = NetworkReaderExtensions.ReadFloat(r);
        return req;
    }
}

/// <summary>
/// The whole shared state of the screen. The host owns it; clients only ever receive it.
///
/// Playback position is expressed as an anchor: "at network time <see cref="AnchorNetTime"/>
/// the video was at <see cref="AnchorVideoTime"/> and (if <see cref="Playing"/>) advancing at
/// 1x". Everyone can then compute the expected position at any moment from Mirror's shared
/// clock (NetworkTime.time) without the host streaming positions every frame.
/// </summary>
internal sealed class SyncState
{
    public int Revision;
    public bool HasScreen;
    public Vector3 ScreenPosition;
    public float ScreenYaw;
    public string VideoUrl = "";
    public string Title = "";
    public bool Playing;
    public double AnchorNetTime;
    public double AnchorVideoTime;
    public bool GuestsCanControl;

    /// <summary>
    /// Screen geometry, owned by the host. Each player keeps their own values in config for
    /// when THEY host; in someone else's game the host's numbers win, so everyone is looking
    /// at the same screen in the same place. Defaults match the config defaults, for the
    /// window before the first state arrives.
    /// </summary>
    public float ScreenClearance = 0.6f;
    public float ScreenWidth = 4f;

    public double ExpectedVideoTime(double netTimeNow)
    {
        if (!Playing) return AnchorVideoTime;
        return AnchorVideoTime + (netTimeNow - AnchorNetTime);
    }

    public SyncState Clone() => (SyncState)MemberwiseClone();
}

internal struct Request
{
    public Protocol.Action Action;
    public string Text;
    public double Value;
    public Vector3 Position;
    public float Yaw;
}
