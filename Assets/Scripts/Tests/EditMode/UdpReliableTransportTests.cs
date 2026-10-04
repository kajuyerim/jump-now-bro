using System;
using NUnit.Framework;
using JumpNowBro.Networking;

namespace JumpNowBro.Tests
{
    public class UdpReliableTransportTests
    {
        // Step both transports n times at a fixed dt, carrying datagrams across the paired channels.
        static void Pump(UdpReliableTransport a, UdpReliableTransport b, int steps, float dt = 0.016f)
        {
            for (int i = 0; i < steps; i++) { a.Tick(dt); b.Tick(dt); }
        }

        static void InjectStatePacket(InMemoryDatagramChannel sender, ushort seq)
        {
            var datagram = new byte[PacketHeader.Size];
            new PacketHeader { type = MessageType.State, seq = seq }.Write(datagram);
            sender.Send(datagram);
        }

        [Test]
        public void Reliable_RoundTrips()
        {
            var (ca, cb) = InMemoryDatagramChannel.Pair();
            var a = new UdpReliableTransport(ca);
            var b = new UdpReliableTransport(cb);

            a.Send(Channel.Reliable, MessageType.Event, new byte[] { 1, 2, 3 });
            Pump(a, b, 3);

            Assert.IsTrue(b.TryReceive(out var type, out var payload));
            Assert.AreEqual(MessageType.Event, type);
            Assert.AreEqual(new byte[] { 1, 2, 3 }, payload);
            Assert.IsFalse(b.TryReceive(out _, out _));
        }

        [Test]
        public void Reliable_SurvivesADroppedDatagram_DeliveredExactlyOnce()
        {
            var (ca, cb) = InMemoryDatagramChannel.Pair();
            var a = new UdpReliableTransport(ca);
            var b = new UdpReliableTransport(cb);

            ca.DropNextSends = 1;                       // lose the EVENT's first transmit
            a.Send(Channel.Reliable, MessageType.Event, new byte[] { 9 });
            Pump(a, b, 90);                             // well past the retransmit timeout

            Assert.IsTrue(b.TryReceive(out _, out var payload));
            Assert.AreEqual(new byte[] { 9 }, payload);
            Assert.IsFalse(b.TryReceive(out _, out _));  // retransmits are de-duplicated
        }

        [Test]
        public void Reliable_AcksDrainTheSendQueue()
        {
            var (ca, cb) = InMemoryDatagramChannel.Pair();
            var a = new UdpReliableTransport(ca);
            var b = new UdpReliableTransport(cb);

            a.Send(Channel.Reliable, MessageType.Event, new byte[] { 5 });
            Pump(a, b, 10);                             // round trip + ack comes back

            Assert.AreEqual(0, a.PendingReliableCount); // b's ack removed it from a's queue
        }

        [Test]
        public void Reliable_ExactReceiverPayloadLimit_RoundTrips()
        {
            var (ca, cb) = InMemoryDatagramChannel.Pair();
            var a = new UdpReliableTransport(ca);
            var b = new UdpReliableTransport(cb);
            var payload = new byte[ReliableReceiveBuffer.MaxPayloadSize];

            a.Send(Channel.Reliable, MessageType.Event, payload);
            Pump(a, b, 3);

            Assert.IsTrue(b.TryReceive(out _, out var received));
            Assert.AreEqual(payload, received);
        }

        [Test]
        public void Reliable_AboveReceiverPayloadLimit_RejectedImmediately()
        {
            var (ca, _) = InMemoryDatagramChannel.Pair();
            var a = new UdpReliableTransport(ca);

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                a.Send(Channel.Reliable, MessageType.Event, new byte[ReliableReceiveBuffer.MaxPayloadSize + 1]));
            Assert.AreEqual(0, a.PendingReliableCount);
        }

        [Test]
        public void Send_RejectsMismatchedChannelAndMessageType()
        {
            var (ca, _) = InMemoryDatagramChannel.Pair();
            var a = new UdpReliableTransport(ca);

            Assert.Throws<ArgumentException>(() =>
                a.Send(Channel.Reliable, MessageType.State, new byte[] { 1 }));
            Assert.Throws<ArgumentException>(() =>
                a.Send(Channel.Unreliable, MessageType.Event, new byte[] { 1 }));
            Assert.AreEqual(0, a.PendingReliableCount);
        }

        [Test]
        public void Send_RejectsInvalidChannelValue()
        {
            var (ca, _) = InMemoryDatagramChannel.Pair();
            var a = new UdpReliableTransport(ca);

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                a.Send((Channel)99, MessageType.State, new byte[] { 1 }));
        }

        [Test]
        public void SendReliableFixedSeq_RejectsUnreliableMessageType()
        {
            var (ca, _) = InMemoryDatagramChannel.Pair();
            var a = new UdpReliableTransport(ca);

            Assert.Throws<ArgumentException>(() =>
                a.SendReliableFixedSeq(MessageType.State, 1, new byte[] { 1 }));
        }

        [Test]
        public void Unreliable_IsLatestWins_StalePacketDropped()
        {
            var (ca, cb) = InMemoryDatagramChannel.Pair();
            var a = new UdpReliableTransport(ca);
            var b = new UdpReliableTransport(cb);

            cb.Lifo = true;                             // b drains newest-first: the older packet arrives last
            a.Send(Channel.Unreliable, MessageType.State, new byte[] { 1 });   // packet seq 1
            a.Send(Channel.Unreliable, MessageType.State, new byte[] { 2 });   // packet seq 2
            b.Tick(0.016f);                             // processes seq 2 then seq 1

            Assert.IsTrue(b.TryReceive(out _, out var payload));
            Assert.AreEqual(new byte[] { 2 }, payload);  // newest delivered
            Assert.IsFalse(b.TryReceive(out _, out _));  // the stale seq 1 was dropped
        }

        [Test]
        public void Rtt_TracksPingPong()
        {
            var (ca, cb) = InMemoryDatagramChannel.Pair();
            var a = new UdpReliableTransport(ca);
            var b = new UdpReliableTransport(cb);

            Pump(a, b, 10);                             // a PING goes out, a PONG echoes back, RTT is sampled

            Assert.That(a.RttSeconds, Is.GreaterThan(0f).And.LessThan(0.1f)); // moved off the 0.1 s default to the tiny loopback RTT
        }

        [Test]
        public void UnknownType_Dropped_AfterAckHarvest()
        {
            for (int value = 0; value <= byte.MaxValue; value++)
            {
                if (Enum.IsDefined(typeof(MessageType), (byte)value)) continue;
                var (ca, cb) = InMemoryDatagramChannel.Pair();
                var b = new UdpReliableTransport(cb);
                b.Send(Channel.Reliable, MessageType.Event, new byte[] { 1 });
                b.Tick(0);
                Assert.AreEqual(1, b.PendingReliableCount);

                var dg = new byte[PacketHeader.Size];
                new PacketHeader { type = (MessageType)value, seq = 1, ack = 1 }.Write(dg);
                ca.Send(dg);
                b.Tick(0.016f);
                Assert.AreEqual(1, b.DroppedDatagrams, $"Undefined message type {value} was accepted.");
                Assert.IsFalse(b.TryReceive(out _, out _));
                Assert.AreEqual(0, b.PendingReliableCount, "Unknown types must still harvest acknowledgements.");
                Assert.IsTrue(b.Connected);
            }
        }

        static void InjectPong(InMemoryDatagramChannel sender, uint stamp, ushort seq = 1, ushort ack = 0)
        {
            var datagram = new byte[PacketHeader.Size];
            new PacketHeader { type = MessageType.Pong, timestamp = stamp, seq = seq, ack = ack }.Write(datagram);
            sender.Send(datagram);
        }

        [Test]
        public void Pong_WithoutAnySentPing_DoesNotSeedRtt()
        {
            var (ca, cb) = InMemoryDatagramChannel.Pair();
            var a = new UdpReliableTransport(ca);
            InjectPong(cb, 0);
            a.Tick(0.02f);
            Assert.AreEqual(0.1f, a.RttSeconds);
            Assert.AreEqual(0f, a.LastRttSampleSeconds);
            Assert.IsTrue(a.Connected, "RTT filtering must not suppress inbound liveness.");
        }

        [TestCase(1u)]
        [TestCase(1000u)]
        [TestCase(uint.MaxValue)]
        public void Pong_UnmatchedTimestamp_DoesNotConsumeOutstandingPing(uint stamp)
        {
            var (ca, cb) = InMemoryDatagramChannel.Pair();
            var a = new UdpReliableTransport(ca);
            a.Tick(0);
            InjectPong(cb, stamp);
            a.Tick(0.05f);
            Assert.AreEqual(0.1f, a.RttSeconds);
            Assert.AreEqual(0f, a.LastRttSampleSeconds);
            InjectPong(cb, 0, seq: 2);
            a.Tick(0.05f);
            Assert.AreEqual(0.1f, a.LastRttSampleSeconds, 0.001f);
        }

        [Test]
        public void Pong_UnmatchedTimestamp_StillHarvestsAcknowledgements()
        {
            var (ca, cb) = InMemoryDatagramChannel.Pair();
            var a = new UdpReliableTransport(ca);
            a.Send(Channel.Reliable, MessageType.Event, new byte[] { 1 });
            a.Tick(0);
            Assert.AreEqual(1, a.PendingReliableCount);
            InjectPong(cb, 9999, ack: 1);
            a.Tick(0.02f);
            Assert.AreEqual(0, a.PendingReliableCount);
            Assert.AreEqual(0f, a.LastRttSampleSeconds);
        }

        [Test]
        public void Pong_Duplicate_DoesNotSampleTwice()
        {
            var (ca, cb) = InMemoryDatagramChannel.Pair();
            var a = new UdpReliableTransport(ca);
            a.Tick(0);
            InjectPong(cb, 0);
            a.Tick(0.25f);
            InjectPong(cb, 0, seq: 2);
            a.Tick(0.25f);
            Assert.AreEqual(0.25f, a.RttSeconds);
            Assert.AreEqual(0.25f, a.LastRttSampleSeconds);
        }

        [Test]
        public void Pong_EarlierPingStillMatchesAfterAnotherPingWasSent()
        {
            var (ca, cb) = InMemoryDatagramChannel.Pair();
            var a = new UdpReliableTransport(ca);
            a.Tick(0);
            a.Tick(1f);
            InjectPong(cb, 0);
            a.Tick(0.25f);
            Assert.AreEqual(1.25f, a.LastRttSampleSeconds);
            InjectPong(cb, 1000, seq: 2);
            a.Tick(0.25f);
            Assert.AreEqual(0.5f, a.LastRttSampleSeconds);
            Assert.AreEqual(1.15625f, a.RttSeconds);
        }

        [Test]
        public void Pong_MatchingPingWithOverCeilingRtt_DoesNotSeedEstimator()
        {
            var (ca, cb) = InMemoryDatagramChannel.Pair();
            var a = new UdpReliableTransport(ca);
            a.Tick(0);
            a.Tick(RttEstimator.MaxSampleSeconds + 1f);
            InjectPong(cb, 0);
            a.Tick(0);
            Assert.AreEqual(0.1f, a.RttSeconds);
            Assert.AreEqual(0f, a.LastRttSampleSeconds);
        }

        [Test]
        public void Pong_OldestPingIsEvictedWhenOutstandingWindowFills()
        {
            var (ca, cb) = InMemoryDatagramChannel.Pair();
            var a = new UdpReliableTransport(ca, pingIntervalSeconds: 0.125);
            a.Tick(0);
            for (int i = 0; i < 32; i++) a.Tick(0.125f);
            InjectPong(cb, 0);
            a.Tick(0);
            Assert.AreEqual(0f, a.LastRttSampleSeconds, "The oldest of 33 pending timestamps must be evicted.");
            InjectPong(cb, 4000, seq: 2);
            a.Tick(0.125f);
            Assert.AreEqual(0.125f, a.LastRttSampleSeconds);
        }

        [Test]
        public void Pong_MatchesPingSentThroughPublicSend()
        {
            var (ca, cb) = InMemoryDatagramChannel.Pair();
            var a = new UdpReliableTransport(ca);
            a.Send(Channel.Unreliable, MessageType.Ping, ReadOnlySpan<byte>.Empty);
            InjectPong(cb, 0);
            a.Tick(0.125f);
            Assert.AreEqual(0.125f, a.RttSeconds);
        }

        [Test]
        public void OversizedSend_CountsAndLogs_DoesNotThrow()
        {
            var (ca, _) = InMemoryDatagramChannel.Pair();
            var a = new UdpReliableTransport(ca);
            string logged = null;
            a.Logger = m => logged = m;
            Assert.DoesNotThrow(() => a.Send(Channel.Unreliable, MessageType.State, new byte[2000]));  // over the 1200 ceiling
            Assert.GreaterOrEqual(a.OversizedSends, 1);
            Assert.IsNotNull(logged);
        }

        [Test]
        public void OversizedReliableSend_FailsImmediately_AndIsNotQueued()
        {
            var (ca, _) = InMemoryDatagramChannel.Pair();
            var a = new UdpReliableTransport(ca);
            bool disconnected = false;
            a.OnDisconnected += () => disconnected = true;

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                a.Send(Channel.Reliable, MessageType.Event, new byte[2000]));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                a.SendReliableFixedSeq(MessageType.Hello, 1, new byte[2000]));
            Assert.AreEqual(0, a.PendingReliableCount);
            Assert.AreEqual(0, a.OversizedSends);
            a.Tick(0.016f);
            Assert.IsFalse(disconnected);
        }

        // #132 loss counters: a dropped datagram leaves a seq gap that books a miss on the next arrival.
        [Test]
        public void PacketCounters_GapAfterDrop_CountsMissed()
        {
            var (ca, cb) = InMemoryDatagramChannel.Pair();
            var a = new UdpReliableTransport(ca);
            var b = new UdpReliableTransport(cb);
            Pump(a, b, 5);                                   // handshake-era PING/PONG; settle baselines
            int accepted0 = b.PacketsAccepted;
            int missed0 = b.PacketsMissed;

            a.Send(Channel.Unreliable, MessageType.State, new byte[] { 1 });
            ca.DropNextSends = 1;
            a.Send(Channel.Unreliable, MessageType.State, new byte[] { 2 });   // lost on the wire
            a.Send(Channel.Unreliable, MessageType.State, new byte[] { 3 });
            b.Tick(0.016f);                                  // b drains its channel; no new a-side traffic

            Assert.AreEqual(missed0 + 1, b.PacketsMissed, "the dropped datagram's gap books one miss");
            Assert.AreEqual(accepted0 + 2, b.PacketsAccepted, "the two delivered datagrams count");
        }

        // #132: reordering is NOT loss — a late arrival that fills its gap repairs the miss.
        [Test]
        public void PacketCounters_LateArrival_RepairsMissed()
        {
            var (ca, cb) = InMemoryDatagramChannel.Pair();
            var a = new UdpReliableTransport(ca);
            var b = new UdpReliableTransport(cb);
            Pump(a, b, 5);
            int accepted0 = b.PacketsAccepted;
            int missed0 = b.PacketsMissed;

            cb.Lifo = true;                                  // b drains newest-first: 3, 2, 1
            a.Send(Channel.Unreliable, MessageType.State, new byte[] { 1 });
            a.Send(Channel.Unreliable, MessageType.State, new byte[] { 2 });
            a.Send(Channel.Unreliable, MessageType.State, new byte[] { 3 });
            b.Tick(0.016f);                                  // processes all three in one drain

            Assert.AreEqual(missed0, b.PacketsMissed, "both late arrivals repair their booked misses");
            Assert.AreEqual(accepted0 + 3, b.PacketsAccepted, "all three datagrams arrived and count once each");
        }

        [TestCase(32)]
        [TestCase(33)]
        public void PacketCounters_HistoryBoundary_CountsMissesAndIgnoresDuplicate(int gap)
        {
            var (ca, cb) = InMemoryDatagramChannel.Pair();
            var b = new UdpReliableTransport(cb);

            InjectStatePacket(ca, 1);
            b.Tick(0.016f);
            InjectStatePacket(ca, (ushort)(1 + gap));
            b.Tick(0.016f);

            Assert.AreEqual(2, b.PacketsAccepted);
            Assert.AreEqual(gap - 1, b.PacketsMissed);

            InjectStatePacket(ca, 1);                  // the old highest packet was already counted
            b.Tick(0.016f);
            Assert.AreEqual(2, b.PacketsAccepted);
            Assert.AreEqual(gap - 1, b.PacketsMissed);
        }

        [Test]
        public void PacketCounters_StalePacketBeyondHistoryWindow_IsIgnored()
        {
            var (ca, cb) = InMemoryDatagramChannel.Pair();
            var b = new UdpReliableTransport(cb);

            InjectStatePacket(ca, 100);
            b.Tick(0.016f);
            InjectStatePacket(ca, 200);
            b.Tick(0.016f);
            int accepted = b.PacketsAccepted;
            int missed = b.PacketsMissed;

            InjectStatePacket(ca, 100);                 // 100 behind, outside the 32-packet history window
            b.Tick(0.016f);

            Assert.AreEqual(accepted, b.PacketsAccepted);
            Assert.AreEqual(missed, b.PacketsMissed);
        }

        [Test]
        public void PacketCounters_WrappedSequence_TracksCountsAndDuplicate()
        {
            var (ca, cb) = InMemoryDatagramChannel.Pair();
            var b = new UdpReliableTransport(cb);

            InjectStatePacket(ca, ushort.MaxValue);
            b.Tick(0.016f);
            InjectStatePacket(ca, 0);                    // wraps forward by one
            b.Tick(0.016f);

            Assert.AreEqual(2, b.PacketsAccepted);
            Assert.AreEqual(0, b.PacketsMissed);

            InjectStatePacket(ca, ushort.MaxValue);      // already represented in the wrapped history
            b.Tick(0.016f);
            Assert.AreEqual(2, b.PacketsAccepted);
            Assert.AreEqual(0, b.PacketsMissed);
        }

        [Test]
        public void Fuzz_RandomDatagrams_NeverThrow()
        {
            var (ca, cb) = InMemoryDatagramChannel.Pair();
            var b = new UdpReliableTransport(cb);
            var rng = new System.Random(999);
            Assert.DoesNotThrow(() =>
            {
                for (int i = 0; i < 5000; i++)
                {
                    var dg = new byte[rng.Next(0, 64)];
                    rng.NextBytes(dg);
                    ca.Send(dg);                        // inject raw garbage into b's receive path
                    b.Tick(0.016f);
                    while (b.TryReceive(out _, out _)) { }   // drain whatever parsed
                }
            });
        }
    }
}
