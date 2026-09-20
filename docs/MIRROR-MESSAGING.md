# How BigScreen talks over the network

For someone comfortable with C# who has never used Mirror. Explains why the networking code
looks the way it does, and what was confirmed when it first ran against the game
(2026-09-19).

## The situation

Big Walk is networked with [Mirror](https://mirror-networking.gitbook.io/), a library where
one player is the **server** (the host) and everyone else is a **client**. The host is also a
client of itself, which Mirror calls *host mode*. Mirror moves data as **messages**: small
structs with a 16-bit id, serialised into a byte buffer and pushed down a connection.

We need to send our own data — where the screen is, which video, playing or paused — to the
other players who have the mod. We must do that **without touching the game's own traffic**
and **without a vanilla player ever receiving anything they cannot understand**.

## Why the obvious approach is unavailable

Mirror's public API is:

```csharp
NetworkClient.RegisterHandler<MyMessage>(OnMyMessage);
conn.Send(new MyMessage { ... });
```

This does not work from a mod, and the reason is worth understanding because it rules out a
whole family of designs.

Mirror does not serialise messages with reflection. At build time it runs a post-processing
step called the **weaver**, which scans the compiled assemblies, finds every type used as a
message, and *generates* a reader and a writer for each one. Those generated functions are
baked into the shipped game. `Send<T>` and `RegisterHandler<T>` are thin generic wrappers that
look up the pair belonging to `T`.

A mod loads long after the game was built. Our struct was never seen by the weaver, so no
reader or writer exists for it, and nothing at runtime can create one. `Send<MySyncMessage>`
fails not because we lack permission but because the machinery it depends on was generated
before our code existed.

Note this is a limit on *new message types*, not on networking as such.

## What those generics actually do

Strip the generic away and `Send<T>`/`RegisterHandler<T>` do only two interesting things:

1. Write a **16-bit id** at the front of the buffer, then the payload.
2. On arrival, look that id up in a `Dictionary<ushort, NetworkMessageDelegate>` and call the
   delegate it finds.

Neither step needs weaver output. The weaver is only involved in turning a struct into bytes —
and we can do that ourselves, because Mirror's primitive writers (`WriteInt`, `WriteString`,
`WriteVector3`, …) are ordinary methods that work on any value.

The dictionary and the raw `Send(ArraySegment<byte>, int)` are internal to Mirror, but
Il2CppInterop exposes the game's internals as public, so both are reachable. So BigScreen does
by hand exactly what the generics would have done:

```csharp
// registration, in MirrorChannel.EnsureRegistered
NetworkServer.handlers[MessageId] = ourDelegate;

// sending, in MirrorChannel.Send
var writer = NetworkWriterPool.Get();
NetworkWriterExtensions.WriteUShort(writer, MessageId);   // the id Mirror would have written
write(writer);                                            // our payload
conn.Send(writer.ToArraySegment(), ReliableChannel);
```

The id is **2356**, derived the same way Mirror derives its own —
`fold16(fnv1a32("BigScreen.SyncMessage"))` — so it is stable across runs and unlikely to
collide with anything the game already uses. If it ever did collide, registration logs a
warning rather than silently stealing the game's handler.

## What we send

Every message: `[u16 id = 2356][u8 protocolVersion][u8 kind][payload]`, on Mirror's reliable
channel.

| Kind | Direction | Meaning |
| --- | --- | --- |
| Hello | guest → host | "I have the mod, version X" |
| State | host → guests | the full authoritative state |
| Request | guest → host | "please do X" (honoured only if the host allows guest control) |

A guest says Hello once Mirror marks it ready. The host records that connection as modded and
replies with the current state. From then on the host pushes state on every change and every
5 seconds as a heartbeat.

**Vanilla players never receive any of this.** The host only sends to connections that said
Hello, and a vanilla client cannot say Hello because it has no mod. This is the single most
important property of the design: a player without BigScreen sees and receives literally
nothing.

## Why the host owns the state

The host is already Mirror's server, so making it the single authority avoids two players
issuing conflicting play/pause/seek commands. Guests send *requests*; the host decides and
broadcasts the result. Everyone, host included, then runs the same "make the world match the
state" loop — the host is just the only one allowed to change it.

## Why playback position is not streamed

Sending "we are at 14.3 seconds" every frame would be wasteful and would still be wrong by the
time it arrived. Instead the state carries an **anchor**:

```
AnchorNetTime   - a moment on the shared clock
AnchorVideoTime - where the video was at that moment
Playing         - whether it is advancing
```

Anyone can then compute the expected position at any time:

```csharp
expected = AnchorVideoTime + (now - AnchorNetTime)   // while playing
```

`now` is `NetworkTime.time`, a clock Mirror already synchronises from server to client with
round-trip compensation. So the anchor is sent once per change, and every machine works out
its own position continuously from it. Clients only correct themselves when they drift beyond
a tolerance, rate-limited to one seek every 2.5 s so a slow stream cannot cause thrashing.

The nice property: a guest who joins late, or who stalls for four seconds while buffering,
recovers to the right position without the host doing anything.

## What the first real run confirmed

**The handler hook works** (2026-09-19). Both server and client handlers registered against
Big Walk's Mirror.

**But only via the legacy delegate signature.** Mirror changed `NetworkMessageDelegate`'s first
parameter from `NetworkConnection` to `NetworkConnectionToClient` in newer versions.
`MirrorChannel.Convert` tries the modern signature first and falls back. Big Walk needs the
fallback:

```
Modern delegate signature rejected (Parameter type at 0 has mismatched native type pointers;
types: Mirror.NetworkConnection != Mirror.NetworkConnectionToClient); trying legacy signature.
Server message handler registered.
```

This is worth keeping in mind: without that fallback path, sync would not work at all. If a
future Big Walk patch updates Mirror, the modern path may start succeeding instead — both are
still tried, in order, so no change should be needed.

**The Dissonance fallback was not needed.** `ARCHITECTURE.md` "Risk 2" proposed exchanging
payloads as text-chat strings if the handler table proved unreachable. It is reachable, so that
plan stays on the shelf.

## Where the code lives

| File | Responsibility |
| --- | --- |
| `Net/MirrorChannel.cs` | the raw channel: register handlers, send bytes, convert delegates |
| `Net/Protocol.cs` | the wire format, and the `SyncState` type itself |
| `Net/SyncSession.cs` | session logic: who is modded, heartbeats, host vs guest |
| `BigScreenController.cs` | making the local world match whatever the state says |
