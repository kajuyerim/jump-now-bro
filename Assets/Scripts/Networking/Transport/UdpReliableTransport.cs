using System;
using System.Collections.Generic;

namespace JumpNowBro.Networking
{
    /// Concrete IReliableTransport. Frames messages over an IDatagramChannel, piggybacks message-level
    /// acks on every datagram, retransmits reliable messages on an RTT timer, delivers the reliable
    /// channel in order + de-duplicated, and gates the unreliable channel to latest-wins. Engine-free so
    /// it runs under the no-Unity CI. The handshake, real-socket binding, and discovery belong to later
    /// milestones; the peer-silence timeout that fires OnDisconnected lives here (armed on first inbound).
    public sealed class UdpReliableTransport : IReliableTransport
    {
        const int MaxDatagram = 1200;                 // MTU-safe ceiling; oversized sends are dropped

        readonly IDatagramChannel channel;
        readonly AckSystem ackTracker = new AckSystem();              // over received reliable message-seqs
        readonly ReliableSendQueue sendQueue = new ReliableSendQueue();
        readonly ReliableReceiveBuffer recvBuffer = new ReliableReceiveBuffer();
        readonly RttEstimator rtt = new RttEstimator();
        readonly Queue<(MessageType type, byte[] payload)> inbox = new Queue<(MessageType, byte[])>();
        readonly byte[] scratch = new byte[MaxDatagram];
        double pingInterval;                            // keepalive cadence — v1.2 runs fast (PING only traffic); v1.4 restores 1 Hz once INPUT/STATE flow
        readonly double silenceTimeout;                 // peer-silence → OnDisconnected

        ushort nextPacketSeq = 1;                      // 0 reserved; stamps every datagram, drives unreliable latest-wins
        ushort highestPacketSeq;                       // 0 = none seen yet
        uint seenPacketBits;                           // bit n = received (highestPacketSeq - 1 - n); loss-vs-reorder discriminator (#132)
        int packetsAccepted;                           // inbound datagrams counted once each (newest or late-but-new)
        int packetsMissed;                             // cumulative seq gaps, repaired when a late arrival proves reorder not loss
        double clock;                                   // seconds since construction; advanced by Tick
        double lastPingAt = double.NegativeInfinity;
        double lastReceivedAt;                          // clock of the last inbound datagram (liveness baseline)
        bool connected;
        bool livenessArmed;                             // the silence timeout only runs after the first inbound
        int droppedDatagrams;                           // malformed / unknown-type inbound, dropped after ack-harvest (diagnostic)
        int oversizedSends;                             // outbound bodies over the MTU ceiling, dropped (diagnostic)

        public UdpReliableTransport(IDatagramChannel channel, double pingIntervalSeconds = 1.0, double silenceTimeoutSeconds = 5.0)
        {
            this.channel = channel;
            pingInterval = pingIntervalSeconds;
            silenceTimeout = silenceTimeoutSeconds;
            sendQueue.OnDeliveryFailed += Disconnect;   // a reliable message giving up means the peer is gone
        }

        public float RttSeconds => rtt.RttSeconds;
        public float LastRttSampleSeconds => rtt.LastSampleSeconds;  // raw, for the quality monitor (#132)
        public int PacketsAccepted => packetsAccepted;               // quality-monitor loss inputs (#132)
        public int PacketsMissed => packetsMissed;
        public bool Connected => connected;
        public int PendingReliableCount => sendQueue.PendingCount;   // for tests/diagnostics; not on the interface
        public int DroppedDatagrams => droppedDatagrams;             // malformed/unknown inbound dropped (diagnostic)
        public int OversizedSends => oversizedSends;                 // outbound over-MTU drops (diagnostic)
        /// Loud-log sink for should-never-happen drops (oversized send). Engine-free: the Runtime layer wires
        /// this to Debug.LogWarning; stays null in CI. Not on the interface.
        public Action<string> Logger;
        public event Action OnConnected;
        public event Action OnDisconnected;

        /// Update PING cadence at runtime. v1.2 ran at 0.2 s because PING was the only traffic; v1.4
        /// flips this back to 1.0 s on Session.Established (#76) — INPUT/STATE now keep liveness warm.
        public void SetPingInterval(double seconds) => pingInterval = seconds > 0 ? seconds : pingInterval;

        public void Send(Channel ch, MessageType type, ReadOnlySpan<byte> payload)
        {
            // Reliable rides the send queue (assigned a stable message-seq, flushed next Tick); unreliable
            // goes out immediately. Type and channel must agree per the DESIGN §8 discipline.
            if (ch == Channel.Reliable) sendQueue.Queue(type, payload);
            else SendFramed(type, 0, payload, NowMs());
        }

        // Handshake re-probe primitive: frame and send a reliable-typed message immediately under a
        // caller-fixed message-seq, without enqueuing it for retransmit. See IReliableTransport for why
        // the seq must stay constant across probes (the peer's in-order receive buffer dedupes them).
        public void SendReliableFixedSeq(MessageType type, ushort messageSeq, ReadOnlySpan<byte> payload)
            => SendFramed(type, messageSeq, payload, NowMs());

        public bool TryReceive(out MessageType type, out byte[] payload)
        {
            if (inbox.Count > 0) { (type, payload) = inbox.Dequeue(); return true; }
            type = default; payload = null; return false;
        }

        public void Tick(float dt)
        {
            clock += dt;
            while (channel.TryReceive(out var datagram)) Process(datagram);
            sendQueue.Tick(clock, rtt.RttSeconds, ReliableSend);
            if (clock - lastPingAt >= pingInterval)
            {
                SendFramed(MessageType.Ping, 0, ReadOnlySpan<byte>.Empty, NowMs());
                lastPingAt = clock;
            }
            if (livenessArmed && clock - lastReceivedAt > silenceTimeout) Disconnect();
        }

        // The queue's SendFn: first send and every retransmit reuse the same stable message-seq.
        void ReliableSend(ushort messageSeq, MessageType type, ReadOnlySpan<byte> payload)
            => SendFramed(type, messageSeq, payload, NowMs());

        // One datagram: [header][message-seq if reliable][body]. Current acks ride along on all of them.
        void SendFramed(MessageType type, ushort messageSeq, ReadOnlySpan<byte> body, uint timestamp)
        {
            bool reliable = IsReliable(type);
            int size = PacketHeader.Size + (reliable ? 2 : 0) + body.Length;
            if (size > scratch.Length)                  // oversized: drop loudly (no fragmentation — our messages are tiny)
            {
                oversizedSends++;
                Logger?.Invoke($"send dropped: {size} B exceeds {scratch.Length} B MTU ceiling (type {type})");
                return;
            }

            ackTracker.GenerateAck(out var ack, out var ackBits);
            var header = new PacketHeader { type = type, seq = nextPacketSeq, ack = ack, ackBits = ackBits, timestamp = timestamp };
            nextPacketSeq = NextSeq(nextPacketSeq);

            header.Write(scratch);
            int offset = PacketHeader.Size;
            if (reliable) { scratch[offset++] = (byte)(messageSeq >> 8); scratch[offset++] = (byte)messageSeq; }
            body.CopyTo(scratch.AsSpan(offset));
            channel.Send(scratch.AsSpan(0, offset + body.Length));
        }

        void Process(byte[] datagram)
        {
            if (!PacketHeader.TryRead(datagram, out var h)) { droppedDatagrams++; return; }   // truncated/malformed: drop

            lastReceivedAt = clock;                                   // any inbound keeps the link alive
            livenessArmed = true;
            if (!connected) { connected = true; OnConnected?.Invoke(); }

            AckSystem.ForEachAcked(h.ack, h.ackBits, sendQueue.OnAck);  // acks ride on every inbound datagram

            // Packet-seq latest-wins bookkeeping. (DESIGN §7 gates on the packet seq; the precise per-message
            // tick gate lands with the INPUT/STATE payload formats in v1.4.)
            // Also feeds the quality monitor's loss counters (#132), mirroring AckSystem.OnReceived: a newest
            // packet books its seq gap as missed; a stale packet that flips a previously-unseen history bit
            // repairs one miss (it was reordered, not lost); duplicates change nothing. Loss here is an
            // inbound-only proxy: each end reports what IT failed to receive.
            bool newestPacket = highestPacketSeq == 0 || SeqMath.IsNewer(h.seq, highestPacketSeq);
            if (newestPacket)
            {
                if (highestPacketSeq != 0)                    // first-ever inbound seeds the baseline, no misses booked
                {
                    int gap = SeqMath.Delta(h.seq, highestPacketSeq);
                    packetsMissed += gap - 1;
                    seenPacketBits = gap >= 32 ? 0u : (seenPacketBits << gap) | (1u << (gap - 1));
                }
                packetsAccepted++;
                highestPacketSeq = h.seq;
            }
            else
            {
                int back = SeqMath.Delta(highestPacketSeq, h.seq);
                if (back >= 1 && back <= 32)
                {
                    uint bit = 1u << (back - 1);
                    if ((seenPacketBits & bit) == 0)          // late but new: repair; already-set = duplicate, no-op
                    {
                        seenPacketBits |= bit;
                        packetsAccepted++;
                        if (packetsMissed > 0) packetsMissed--;
                    }
                }
            }

            int offset = PacketHeader.Size;
            bool reliable = IsReliable(h.type);
            ushort messageSeq = 0;
            if (reliable)
            {
                if (datagram.Length < offset + 2) { droppedDatagrams++; return; }   // reliable but no room for the message-seq: drop
                messageSeq = (ushort)((datagram[offset] << 8) | datagram[offset + 1]);
                offset += 2;
            }
            var body = new ReadOnlySpan<byte>(datagram, offset, datagram.Length - offset);

            switch (h.type)
            {
                case MessageType.Ping:
                    SendFramed(MessageType.Pong, 0, ReadOnlySpan<byte>.Empty, h.timestamp);   // echo the sender's stamp
                    break;
                case MessageType.Pong:
                    rtt.AddSample(SecondsSince(h.timestamp));
                    break;
                default:
                    if ((byte)h.type > (byte)MessageType.Pong)        // unknown type: drop AFTER acks + liveness harvested above
                    {
                        droppedDatagrams++;
                        break;
                    }
                    if (reliable)
                    {
                        ackTracker.OnReceived(messageSeq);
                        recvBuffer.Accept(messageSeq, h.type, body);
                        while (recvBuffer.TryNext(out var t, out var p)) inbox.Enqueue((t, p));
                    }
                    else if (newestPacket)            // unreliable: deliver only the newest packet seen, drop the rest
                    {
                        inbox.Enqueue((h.type, body.ToArray()));
                    }
                    break;
            }
        }

        void Disconnect()
        {
            if (!connected) return;
            connected = false;
            OnDisconnected?.Invoke();
        }

        uint NowMs() => (uint)(clock * 1000.0);
        float SecondsSince(uint stampMs) => (uint)(NowMs() - stampMs) / 1000f;   // wrap-safe unsigned subtraction

        // Channel discipline (DESIGN §8): these ride the reliable channel and carry a message-seq prefix.
        static bool IsReliable(MessageType t) =>
            t == MessageType.Event || t == MessageType.Hello || t == MessageType.Welcome || t == MessageType.Goodbye;

        static ushort NextSeq(ushort s) { s++; return s == 0 ? (ushort)1 : s; }
    }
}
