using System;
using JumpNowBro.Util;

namespace JumpNowBro.Networking
{
    public enum WelcomeReason : byte { Accepted, VersionMismatch, Busy }
    public enum GoodbyeReason : byte { Normal, Busy, VersionMismatch, ProtocolError }

    /// Magic + version are negotiated in the HELLO/WELCOME bodies — the fixed 11-byte header has no room.
    public static class SessionProtocol
    {
        public const uint Magic = 0x4A4E4252;   // 'J' 'N' 'B' 'R'
        public const ushort Version = 5;         // v5: comms EVENT kinds — Callout/WorldPing/Countdown (#131). A v4 peer's
                                                 // read boundary rejects kind > 6, silently dropping every comms EVENT and
                                                 // leaving the signals one-sided, so version skew fails at the handshake
                                                 // instead. (v4: RunSummary, #130. v3: lobby kinds, #142. v2: name+colour,
                                                 // #114/#125.)

        /// Validate a raw inbound datagram as a well-formed, current-version HELLO — used by the host's
        /// listen phase before it commits to a peer. HELLO rides the reliable channel, so its body sits
        /// after the 11-byte header AND the 2-byte reliable message-seq (i.e. at offset 13).
        public static bool IsValidHello(ReadOnlySpan<byte> datagram)
        {
            if (!PacketHeader.TryRead(datagram, out var h) || h.type != MessageType.Hello) return false;
            int bodyOffset = PacketHeader.Size + 2;
            if (datagram.Length < bodyOffset) return false;
            return Hello.TryRead(datagram.Slice(bodyOffset), out var hello)
                   && hello.Magic == Magic && hello.Version == Version;
        }
    }

    public struct Hello
    {
        public uint Magic;
        public ushort Version;
        public byte ColorIndex;                  // v2: client's assigned colour slot (#125)
        public string Name;                      // v2: client's display name (#114)

        public int Write(Span<byte> dst)
        {
            var w = new ByteWriter(dst);
            w.WriteUInt(Magic);
            w.WriteUShort(Version);
            w.WriteByte(ColorIndex);
            w.WriteString(Name);
            return w.Position;
        }

        public static bool TryRead(ReadOnlySpan<byte> src, out Hello h)
        {
            h = default;
            var r = new ByteReader(src);
            return r.TryReadUInt(out h.Magic) && r.TryReadUShort(out h.Version)
                   && r.TryReadByte(out h.ColorIndex) && r.TryReadString(out h.Name);
        }
    }

    /// WELCOME is the host's response to a validated HELLO. v1.4 enriches the body so the client can
    /// (1) confirm its assigned slot (peerOwner=P2 — host is always P1), (2) load whichever level the host
    /// is currently on (mid-game join), and (3) note the host's tick at WELCOME-send for debug telemetry.
    public struct Welcome
    {
        public uint Magic;
        public ushort Version;
        public bool Accepted;
        public WelcomeReason Reason;
        public InputOwner PeerOwner;             // v1.4: always P2 — host owns P1 by convention
        public byte CurrentSceneIndex;           // host's LevelManager.CurrentLevelIndex; 0xFF if pre-load
        public uint HostTickAtWelcome;           // v1.4 logs only; v1.5+ may seed offset estimation here
        public byte ColorIndex;                  // v2: host's assigned colour slot (#125)
        public string Name;                      // v2: host's display name (#114)

        public int Write(Span<byte> dst)
        {
            var w = new ByteWriter(dst);
            w.WriteUInt(Magic);
            w.WriteUShort(Version);
            w.WriteByte((byte)(Accepted ? 1 : 0));
            w.WriteByte((byte)Reason);
            w.WriteByte((byte)PeerOwner);
            w.WriteByte(CurrentSceneIndex);
            w.WriteUInt(HostTickAtWelcome);
            w.WriteByte(ColorIndex);
            w.WriteString(Name);
            return w.Position;
        }

        public static bool TryRead(ReadOnlySpan<byte> src, out Welcome w)
        {
            w = default;
            var r = new ByteReader(src);
            if (!r.TryReadUInt(out w.Magic) || !r.TryReadUShort(out w.Version)) return false;
            if (!r.TryReadByte(out var accepted) || !r.TryReadByte(out var reason)) return false;
            if (!r.TryReadByte(out var peerOwner)) return false;
            if (peerOwner > 1) return false;                          // out-of-range enum byte → malformed
            if (reason > (byte)WelcomeReason.Busy) return false;      // out-of-range enum byte → malformed
            if (!r.TryReadByte(out w.CurrentSceneIndex)) return false;
            if (!r.TryReadUInt(out w.HostTickAtWelcome)) return false;
            if (!r.TryReadByte(out w.ColorIndex)) return false;
            if (!r.TryReadString(out w.Name)) return false;
            w.Accepted = accepted != 0;
            w.Reason = (WelcomeReason)reason;
            w.PeerOwner = (InputOwner)peerOwner;
            return true;
        }
    }

    public struct Goodbye
    {
        public GoodbyeReason Reason;

        public int Write(Span<byte> dst)
        {
            var w = new ByteWriter(dst);
            w.WriteByte((byte)Reason);
            return w.Position;
        }

        public static bool TryRead(ReadOnlySpan<byte> src, out Goodbye g)
        {
            g = default;
            var r = new ByteReader(src);
            if (!r.TryReadByte(out var reason)) return false;
            if (reason > (byte)GoodbyeReason.ProtocolError) return false;   // out-of-range enum byte → malformed
            g.Reason = (GoodbyeReason)reason;
            return true;
        }
    }
}
