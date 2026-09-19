using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using Mirror;

namespace BigScreen.Net;

/// <summary>
/// A private message channel on top of the game's Mirror connection.
///
/// Why not Mirror's RegisterHandler&lt;T&gt;/Send&lt;T&gt;? Those are generic over a message
/// struct that must be known to Mirror's weaver-generated reader/writer tables inside
/// the IL2CPP binary. A struct defined in a mod cannot be added to those tables. What we
/// CAN do is exactly what those generics expand to: put a delegate into Mirror's
/// <c>handlers</c> dictionary keyed by a 16-bit message id, and send a NetworkWriter
/// payload that starts with that id. The interop assemblies expose the internal
/// dictionary and the internal Send(ArraySegment) for us.
///
/// Vanilla players that receive our message log "Unknown message id" and carry on, but
/// we avoid even that by only sending to peers that said Hello.
/// </summary>
internal static class MirrorChannel
{
    /// <summary>
    /// Mirror derives message ids as fold16(fnv1a32(type.FullName)). We use the same
    /// derivation on the name "BigScreen.SyncMessage" so the id is stable and unlikely
    /// to collide with the game's own message types.
    /// </summary>
    public const ushort MessageId = 2356;

    public static event Action<NetworkConnectionToClient, NetworkReader> ServerMessage;
    public static event Action<NetworkReader> ClientMessage;

    // The converted IL2CPP delegates reference managed trampolines; keep strong refs so
    // the GC never collects them while Mirror still holds the native side.
    private static NetworkMessageDelegate _serverDelegate;
    private static NetworkMessageDelegate _clientDelegate;
    private static Delegate _serverManaged;
    private static Delegate _clientManaged;

    private static bool _serverRegistered;
    private static bool _clientRegistered;
    private static bool _conversionFailed;

    public static bool ConversionFailed => _conversionFailed;

    /// <summary>
    /// Idempotent. Mirror clears its handler tables on Shutdown, so this is called every
    /// second while a session is active and re-adds our handler when it went missing.
    /// </summary>
    public static void EnsureRegistered()
    {
        if (_conversionFailed) return;

        try
        {
            if (NetworkServer.active)
            {
                var handlers = NetworkServer.handlers;
                if (handlers != null && (!_serverRegistered || !handlers.ContainsKey(MessageId)))
                {
                    if (handlers.ContainsKey(MessageId) && !_serverRegistered)
                        Plugin.Log.LogWarning($"Mirror server already has a handler for id {MessageId}; replacing it. If the game breaks, change MessageId.");
                    _serverDelegate ??= Convert(OnServerMessage, out _serverManaged);
                    handlers[MessageId] = _serverDelegate;
                    _serverRegistered = true;
                    Plugin.Log.LogInfo("Server message handler registered.");
                }
            }
            else _serverRegistered = false;

            if (NetworkClient.active)
            {
                var handlers = NetworkClient.handlers;
                if (handlers != null && (!_clientRegistered || !handlers.ContainsKey(MessageId)))
                {
                    _clientDelegate ??= Convert(OnClientMessage, out _clientManaged);
                    handlers[MessageId] = _clientDelegate;
                    _clientRegistered = true;
                    Plugin.Log.LogInfo("Client message handler registered.");
                }
            }
            else _clientRegistered = false;
        }
        catch (Exception e)
        {
            _conversionFailed = true;
            Plugin.Log.LogError("Could not hook into Mirror's message handlers. Sync is disabled. " +
                                "This usually means the game's Mirror version changed its NetworkMessageDelegate " +
                                "signature; see docs/ARCHITECTURE.md 'Risk 2'. Details: " + e);
        }
    }

    public static void Reset()
    {
        _serverRegistered = false;
        _clientRegistered = false;
    }

    private static NetworkMessageDelegate Convert(Action<NetworkConnectionToClient, NetworkReader, int> handler, out Delegate keepAlive)
    {
        // Mirror (2022+) declares: delegate void NetworkMessageDelegate(NetworkConnectionToClient conn, NetworkReader reader, int channelId)
        // Older versions took NetworkConnection. Try the current one first, then the old.
        try
        {
            keepAlive = handler;
            return DelegateSupport.ConvertDelegate<NetworkMessageDelegate>(handler);
        }
        catch (Exception first)
        {
            Plugin.Log.LogDebug($"Modern delegate signature rejected ({first.Message}); trying legacy signature.");
            Action<NetworkConnection, NetworkReader, int> legacy = (conn, reader, ch) => handler(conn as NetworkConnectionToClient, reader, ch);
            keepAlive = legacy;
            return DelegateSupport.ConvertDelegate<NetworkMessageDelegate>(legacy);
        }
    }

    private static void OnServerMessage(NetworkConnectionToClient conn, NetworkReader reader, int channelId)
    {
        try { ServerMessage?.Invoke(conn, reader); }
        catch (Exception e) { Plugin.Log.LogError($"Server message handler threw: {e}"); }
    }

    private static void OnClientMessage(NetworkConnectionToClient _, NetworkReader reader, int channelId)
    {
        try { ClientMessage?.Invoke(reader); }
        catch (Exception e) { Plugin.Log.LogError($"Client message handler threw: {e}"); }
    }

    // --- Sending --------------------------------------------------------------------

    private const int ReliableChannel = 0; // Mirror.Channels.Reliable

    /// <summary>Client -> host.</summary>
    public static bool SendToServer(Action<NetworkWriter> write)
    {
        var conn = NetworkClient.connection;
        if (conn == null || !NetworkClient.isConnected) return false;
        return Send(conn, write);
    }

    /// <summary>Host -> one client.</summary>
    public static bool SendTo(NetworkConnectionToClient conn, Action<NetworkWriter> write)
    {
        if (conn == null) return false;
        return Send(conn, write);
    }

    private static bool Send(NetworkConnection conn, Action<NetworkWriter> write)
    {
        NetworkWriterPooled writer = null;
        try
        {
            writer = NetworkWriterPool.Get();
            NetworkWriterExtensions.WriteUShort(writer, MessageId);
            write(writer);
            conn.Send(writer.ToArraySegment(), ReliableChannel);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"Send failed: {e.Message}");
            return false;
        }
        finally
        {
            if (writer != null) NetworkWriterPool.Return(writer);
        }
    }
}
