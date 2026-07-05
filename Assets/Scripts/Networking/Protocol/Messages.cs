using System;
using JumpNowBro.Util;

namespace JumpNowBro.Networking
{
    /// v1.4 gameplay message bodies. Each rides on top of the 11-byte PacketHeader (reliable EVENT also
    /// adds the 2-byte reliable message-seq the transport injects). All multi-byte fields big-endian.
    /// Every reader is bounds-checked end-to-end and never throws on a short/malformed buffer — a
    /// truncated datagram returns false and the receive path drops it without crashing.

    /// INPUT (client → host, unreliable, ~60 Hz). Variable-length redundancy window so an edge press
    /// survives ~4 consecutive packet losses without retransmit on the unreliable channel.
    public static class InputBody
    {
        public const byte MaxFrameCount = 16;        // ample headroom over v1.4's K=6
        public const int HeaderSize    = 4 + 1;      // baseTick(u32) + count(u8)

        public static int Write(Span<byte> dst, uint baseTick, ReadOnlySpan<byte> packedFrames)
        {
            if (packedFrames.Length < 1 || packedFrames.Length > MaxFrameCount)
                throw new ArgumentException($"InputBody.Write: count must be 1..{MaxFrameCount}");
            var w = new ByteWriter(dst);
            w.WriteUInt(baseTick);
            w.WriteByte((byte)packedFrames.Length);
            w.WriteBytes(packedFrames);
            return w.Position;
        }

        public static bool TryRead(ReadOnlySpan<byte> src,
                                   out uint baseTick, out byte count, out ReadOnlySpan<byte> packedFrames)
        {
            baseTick = 0; count = 0; packedFrames = default;
            var r = new ByteReader(src);
            if (!r.TryReadUInt(out baseTick)) return false;
            if (!r.TryReadByte(out count)) return false;
            if (count < 1 || count > MaxFrameCount) return false;
            if (!r.TryReadBytes(count, out packedFrames)) return false;
            return true;
        }
    }

    /// STATE (host → client, unreliable, ~30 Hz, latest-wins). Carries everything the client needs to
    /// render the shared character plus the seeds v1.5's predictor will consume (remoteInputFrame for
    /// dead-reckoning host-owned actions; lastConsumedClientTick for the reconciliation anchor).
    public struct StateBody
    {
        public uint snapshotTick;
        public uint lastConsumedClientTick;
        public ushort deathCount;
        public byte sceneIndex;                       // 0xFF = pre-load sentinel
        public ControlMap controlMap;
        public PlayerInputFrame remoteInputFrame;     // the host's local input this tick (for v1.5 dead-reckon)
        public MovementState movementState;

        public const int Size =
            4 + 4 + 2 + 1 + ControlMap.PackedSize + 1 + MovementState.PackedSize;   // = 61

        public int Write(Span<byte> dst)
        {
            var w = new ByteWriter(dst);
            w.WriteUInt(snapshotTick);
            w.WriteUInt(lastConsumedClientTick);
            w.WriteUShort(deathCount);
            w.WriteByte(sceneIndex);
            ControlMap.Pack(controlMap, w.Reserve(ControlMap.PackedSize));
            w.WriteByte(PlayerInputFrame.Pack(remoteInputFrame));
            MovementState.Pack(movementState, w.Reserve(MovementState.PackedSize));
            return w.Position;
        }

        public static bool TryRead(ReadOnlySpan<byte> src, out StateBody body)
        {
            body = default;
            var r = new ByteReader(src);
            if (!r.TryReadUInt(out body.snapshotTick))           return false;
            if (!r.TryReadUInt(out body.lastConsumedClientTick)) return false;
            if (!r.TryReadUShort(out body.deathCount))           return false;
            if (!r.TryReadByte(out body.sceneIndex))             return false;
            if (!r.TryReadBytes(ControlMap.PackedSize, out var mapBytes))    return false;
            if (!ControlMap.TryUnpack(mapBytes, out body.controlMap))        return false;
            if (!r.TryReadByte(out var frameByte))                           return false;
            body.remoteInputFrame = PlayerInputFrame.Unpack(frameByte);
            // MovementState reader tolerates fewer trailing bytes (v1.6/v1.7 padding-reserve story).
            if (!r.TryReadBytes(r.Remaining, out var stateBytes))            return false;
            if (!MovementState.TryUnpack(stateBytes, out body.movementState)) return false;
            return true;
        }
    }

    /// EVENT.kind discriminator. LevelLoad/Swap/Death/LobbyState/RunSummary flow host → client; LevelReady
    /// (the load-barrier ack) and LobbyReady (the pre-game ready toggle) flow client → host. The body shape
    /// differs per kind — see EventBody.
    public enum EventKind : byte
    {
        LevelLoad  = 0,
        Swap       = 1,
        Death      = 2,
        LevelReady = 3,
        LobbyReady = 4,    // client → host: 1-byte ready flag (v2.3 lobby)
        LobbyState = 5,    // host → client: 1-byte selected level, so the client's lobby shows the pick
        RunSummary = 6,    // host → client: the completed level's run stats, sent at goal touch (#130)
    }

    /// EVENT body (reliable channel). A discriminated union keyed on `kind`; each variant validates its own
    /// length on read. apply_at_tick is a CLIENT input-tick (the one coordinate both ends agree on — see
    /// DESIGN §8 and the v1.6 plan): the host flips its map when LastConsumedClientTick reaches it, the
    /// client when its TickClock does.
    public struct EventBody
    {
        public EventKind kind;
        public byte sceneIndex;     // LevelLoad / LevelReady / LobbyState (selected level) / RunSummary (completed level)
        public uint tick;           // Swap: apply_at_tick · Death: deathTick (both client input-ticks)
        public ControlMap map;      // Swap: new absolute map · Death: checkpoint map to restore
        public byte triggerId;      // Swap: which physical SwapTrigger fired (client banner targeting)
        public byte ready;          // LobbyReady: 0|1 (strict on read, like ControlMap owners)
        public uint timeMs;         // RunSummary: level play time
        public ushort deaths;       // RunSummary
        public ushort swaps;        // RunSummary
        public uint streakMs;       // RunSummary: longest deathless span

        // Largest variant (RunSummary) bounds the send scratch buffer; variants write fewer bytes.
        public const int MaxSize = 1 + 1 + 4 + 2 + 2 + 4;   // = 14

        public static EventBody LevelLoad(byte sceneIndex)  => new EventBody { kind = EventKind.LevelLoad,  sceneIndex = sceneIndex };
        public static EventBody LevelReady(byte sceneIndex) => new EventBody { kind = EventKind.LevelReady, sceneIndex = sceneIndex };
        public static EventBody Swap(uint applyTick, ControlMap map, byte triggerId) =>
            new EventBody { kind = EventKind.Swap, tick = applyTick, map = map, triggerId = triggerId };
        public static EventBody Death(uint deathTick, ControlMap checkpointMap) =>
            new EventBody { kind = EventKind.Death, tick = deathTick, map = checkpointMap };
        public static EventBody LobbyReady(bool isReady) =>
            new EventBody { kind = EventKind.LobbyReady, ready = isReady ? (byte)1 : (byte)0 };
        public static EventBody LobbyState(byte selectedLevel) =>
            new EventBody { kind = EventKind.LobbyState, sceneIndex = selectedLevel };
        /// Clamps at the wire boundary: tracker stats are ints, the body is u16/u32 (negatives -> 0).
        public static EventBody RunSummary(byte levelIndex, LevelRunStats stats) =>
            new EventBody
            {
                kind = EventKind.RunSummary,
                sceneIndex = levelIndex,
                timeMs = ClampMs(stats.timeMs),
                deaths = ClampU16(stats.deaths),
                swaps = ClampU16(stats.swaps),
                streakMs = ClampMs(stats.streakMs),
            };

        static ushort ClampU16(int v) => v < 0 ? (ushort)0 : v > ushort.MaxValue ? ushort.MaxValue : (ushort)v;
        static uint ClampMs(int v) => v < 0 ? 0u : (uint)v;

        public int Write(Span<byte> dst)
        {
            var w = new ByteWriter(dst);
            w.WriteByte((byte)kind);
            switch (kind)
            {
                case EventKind.LevelLoad:
                case EventKind.LevelReady:
                    w.WriteByte(sceneIndex);
                    break;
                case EventKind.Swap:
                    w.WriteUInt(tick);
                    ControlMap.Pack(map, w.Reserve(ControlMap.PackedSize));
                    w.WriteByte(triggerId);
                    break;
                case EventKind.Death:
                    w.WriteUInt(tick);
                    ControlMap.Pack(map, w.Reserve(ControlMap.PackedSize));
                    break;
                case EventKind.LobbyReady:
                    w.WriteByte(ready);
                    break;
                case EventKind.LobbyState:
                    w.WriteByte(sceneIndex);
                    break;
                case EventKind.RunSummary:
                    w.WriteByte(sceneIndex);
                    w.WriteUInt(timeMs);
                    w.WriteUShort(deaths);
                    w.WriteUShort(swaps);
                    w.WriteUInt(streakMs);
                    break;
            }
            return w.Position;
        }

        public static bool TryRead(ReadOnlySpan<byte> src, out EventBody body)
        {
            body = default;
            var r = new ByteReader(src);
            if (!r.TryReadByte(out var k)) return false;
            if (k > (byte)EventKind.RunSummary) return false;          // reject kinds we don't define
            body.kind = (EventKind)k;
            switch (body.kind)
            {
                case EventKind.LevelLoad:
                case EventKind.LevelReady:
                case EventKind.LobbyState:
                    return r.TryReadByte(out body.sceneIndex);
                case EventKind.LobbyReady:
                    if (!r.TryReadByte(out body.ready)) return false;
                    return body.ready <= 1;                            // strict: a flag byte is 0 or 1
                case EventKind.Swap:
                    if (!r.TryReadUInt(out body.tick)) return false;
                    if (!r.TryReadBytes(ControlMap.PackedSize, out var sm)) return false;
                    if (!ControlMap.TryUnpack(sm, out body.map)) return false;
                    return r.TryReadByte(out body.triggerId);
                case EventKind.Death:
                    if (!r.TryReadUInt(out body.tick)) return false;
                    if (!r.TryReadBytes(ControlMap.PackedSize, out var dm)) return false;
                    return ControlMap.TryUnpack(dm, out body.map);
                case EventKind.RunSummary:
                    if (!r.TryReadByte(out body.sceneIndex)) return false;
                    if (!r.TryReadUInt(out body.timeMs)) return false;
                    if (!r.TryReadUShort(out body.deaths)) return false;
                    if (!r.TryReadUShort(out body.swaps)) return false;
                    return r.TryReadUInt(out body.streakMs);
            }
            return false;
        }
    }
}
