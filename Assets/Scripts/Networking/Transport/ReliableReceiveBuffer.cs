using System;
using System.Collections.Generic;

namespace JumpNowBro.Networking
{
    /// Receive side of the reliable channel. Each reliable message carries a stable message-seq;
    /// this releases them to the application in contiguous seq order, exactly once. Arrivals ahead
    /// of a gap wait until the missing seq shows up (original or retransmit); duplicates and
    /// already-delivered seqs are dropped. seq 0 is the reserved "none" sentinel.
    public sealed class ReliableReceiveBuffer
    {
        struct Msg { public MessageType Type; public byte[] Payload; }

        const int MaxBuffered = 256;   // reorder-window guard; unreachable while reliable in-flight is capped far below
        public const int MaxPayloadSize = 512;   // reliable bodies are <= ~24 B; reject a hostile oversized payload outright

        readonly Dictionary<ushort, Msg> buffer = new Dictionary<ushort, Msg>();

        public ushort NextExpected { get; private set; } = 1;   // next seq to deliver; message seqs start at 1
        public int BufferedCount => buffer.Count;

        /// Record a received reliable message. No-op for the sentinel, already-delivered seqs, and dups.
        public void Accept(ushort seq, MessageType type, ReadOnlySpan<byte> payload)
        {
            if (seq == 0) return;                                       // reserved sentinel
            if (payload.Length > MaxPayloadSize) return;                // hostile oversized reliable body: drop
            if (SeqMath.IsNewer(NextExpected, seq)) return;             // older than expected: already delivered
            if (buffer.ContainsKey(seq)) return;                        // duplicate still queued ahead of the gap
            if (buffer.Count >= MaxBuffered && seq != NextExpected) return;  // bound the window, but never block the unsticking seq
            buffer[seq] = new Msg { Type = type, Payload = payload.ToArray() };
        }

        /// Pull the next in-order message if present. Loop until false to drain a burst.
        public bool TryNext(out MessageType type, out byte[] payload)
        {
            if (buffer.TryGetValue(NextExpected, out var msg))
            {
                buffer.Remove(NextExpected);
                NextExpected = NextSeq(NextExpected);
                type = msg.Type;
                payload = msg.Payload;
                return true;
            }
            type = default;
            payload = null;
            return false;
        }

        static ushort NextSeq(ushort s) { s++; return s == 0 ? (ushort)1 : s; }   // skip reserved 0 on wrap
    }
}
