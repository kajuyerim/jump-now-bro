using System.Collections.Generic;
using NUnit.Framework;
using JumpNowBro.Networking;

namespace JumpNowBro.Tests
{
    public class ReliableSendQueueTests
    {
        sealed class Sent { public ushort Seq; public MessageType Type; public byte[] Payload; }

        static List<Sent> Recorder(out ReliableSendQueue.SendFn fn)
        {
            var log = new List<Sent>();
            fn = (seq, type, payload) => log.Add(new Sent { Seq = seq, Type = type, Payload = payload.ToArray() });
            return log;
        }

        [Test]
        public void Queue_ThenTick_Sends()
        {
            var q = new ReliableSendQueue();
            var log = Recorder(out var send);
            q.Queue(MessageType.Event, new byte[] { 1, 2, 3 });
            q.Tick(0, 0.05f, send);
            Assert.AreEqual(1, log.Count);
            Assert.AreEqual(MessageType.Event, log[0].Type);
            Assert.AreEqual(new byte[] { 1, 2, 3 }, log[0].Payload);
        }

        [Test]
        public void FirstMessageSeq_IsOne()
        {
            var q = new ReliableSendQueue();
            var log = Recorder(out var send);
            q.Queue(MessageType.Event, new byte[] { 0 });
            q.Tick(0, 0.05f, send);
            Assert.AreEqual(1, log[0].Seq);
        }

        [Test]
        public void Unacked_ResendsUnderSameSeq_AfterRto()
        {
            var q = new ReliableSendQueue();
            var log = Recorder(out var send);
            q.Queue(MessageType.Event, new byte[] { 9 });
            q.Tick(0, 0.1f, send);          // first send (baseRto = clamp(0.2) = 0.2)
            q.Tick(0.1, 0.1f, send);        // 0.1 < 0.2 -> no resend
            Assert.AreEqual(1, log.Count);
            q.Tick(0.25, 0.1f, send);       // overdue -> resend, same seq
            Assert.AreEqual(2, log.Count);
            Assert.AreEqual(log[0].Seq, log[1].Seq);
        }

        [Test]
        public void OnAck_StopsResends()
        {
            var q = new ReliableSendQueue();
            var log = Recorder(out var send);
            q.Queue(MessageType.Event, new byte[] { 9 });
            q.Tick(0, 0.1f, send);
            q.OnAck(log[0].Seq);
            q.Tick(10, 0.1f, send);         // far past RTO; must not resend
            Assert.AreEqual(1, log.Count);
            Assert.AreEqual(0, q.PendingCount);
        }

        [Test]
        public void OnAck_Duplicate_IsHarmless()
        {
            var q = new ReliableSendQueue();
            var log = Recorder(out var send);
            q.Queue(MessageType.Event, new byte[] { 9 });
            q.Tick(0, 0.1f, send);
            var seq = log[0].Seq;
            Assert.DoesNotThrow(() => { q.OnAck(seq); q.OnAck(seq); });
            Assert.AreEqual(0, q.PendingCount);
        }

        [Test]
        public void Backoff_LengthensResendInterval()
        {
            var q = new ReliableSendQueue();
            var log = Recorder(out var send);
            q.Queue(MessageType.Event, new byte[] { 9 });   // baseRto = 0.2
            q.Tick(0, 0.1f, send);           // send #1
            q.Tick(0.21, 0.1f, send);        // resend #1 (interval 0.2)
            q.Tick(0.5, 0.1f, send);         // 0.29 < 0.4 (backoff) -> no resend
            Assert.AreEqual(2, log.Count);
            q.Tick(0.62, 0.1f, send);        // 0.41 >= 0.4 -> resend #2
            Assert.AreEqual(3, log.Count);
        }

        [Test]
        public void RetryCap_RaisesDeliveryFailed()
        {
            var q = new ReliableSendQueue();
            var log = Recorder(out var send);
            var failed = false;
            q.OnDeliveryFailed += () => failed = true;
            q.Queue(MessageType.Event, new byte[] { 9 });
            double t = 0;
            for (int i = 0; i < 50 && !failed; i++) { q.Tick(t, 0.1f, send); t += 10; }
            Assert.IsTrue(failed);
            Assert.AreEqual(0, q.PendingCount);
        }

        [Test]
        public void InFlightCap_RaisesDeliveryFailed()
        {
            var q = new ReliableSendQueue();
            var failed = false;
            q.OnDeliveryFailed += () => failed = true;
            for (int i = 0; i < 100 && !failed; i++) q.Queue(MessageType.Event, new byte[] { 1 });
            Assert.IsTrue(failed);
        }

        [Test]
        public void InFlightCap_AllowsExactlySixtyFourMessages()
        {
            var q = new ReliableSendQueue();
            var failed = false;
            q.OnDeliveryFailed += () => failed = true;

            for (int i = 0; i < 64; i++) q.Queue(MessageType.Event, new byte[] { 1 });

            Assert.IsFalse(failed);
            Assert.AreEqual(64, q.PendingCount);
            q.Queue(MessageType.Event, new byte[] { 1 });
            Assert.IsTrue(failed);
            Assert.AreEqual(64, q.PendingCount);
        }

        [Test]
        public void MessageSequence_WrapsFromMaxToOneWithoutUsingZero()
        {
            var q = new ReliableSendQueue();
            var log = Recorder(out var send);

            for (int i = 0; i < 65534; i++)
            {
                q.Queue(MessageType.Event, new byte[] { 1 });
                q.Tick(i, 0.1f, send);
                q.OnAck(log[log.Count - 1].Seq);
            }

            q.Queue(MessageType.Event, new byte[] { 1 });
            q.Tick(65534, 0.1f, send);
            Assert.AreEqual(65535, log[log.Count - 1].Seq);
            q.OnAck(65535);

            q.Queue(MessageType.Event, new byte[] { 1 });
            q.Tick(65535, 0.1f, send);
            Assert.AreEqual(1, log[log.Count - 1].Seq);
        }
    }
}
