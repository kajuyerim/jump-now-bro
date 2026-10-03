using NUnit.Framework;
using JumpNowBro.Networking;

namespace JumpNowBro.Tests
{
    public class ReliableReceiveBufferTests
    {
        [Test]
        public void InOrder_DeliversImmediately()
        {
            var rb = new ReliableReceiveBuffer();
            rb.Accept(1, MessageType.Event, new byte[] { 1 });
            rb.Accept(2, MessageType.Event, new byte[] { 2 });
            Assert.IsTrue(rb.TryNext(out _, out var p1));
            Assert.AreEqual(new byte[] { 1 }, p1);
            Assert.IsTrue(rb.TryNext(out _, out var p2));
            Assert.AreEqual(new byte[] { 2 }, p2);
            Assert.IsFalse(rb.TryNext(out _, out _));
        }

        [Test]
        public void OutOfOrder_HeldUntilGapFills_ThenDeliveredInOrder()
        {
            var rb = new ReliableReceiveBuffer();
            rb.Accept(2, MessageType.Event, new byte[] { 2 });   // arrives before 1
            Assert.IsFalse(rb.TryNext(out _, out _));            // gap at 1 holds 2
            Assert.AreEqual(1, rb.BufferedCount);
            rb.Accept(1, MessageType.Event, new byte[] { 1 });
            Assert.IsTrue(rb.TryNext(out _, out var p1));
            Assert.AreEqual(new byte[] { 1 }, p1);
            Assert.IsTrue(rb.TryNext(out _, out var p2));
            Assert.AreEqual(new byte[] { 2 }, p2);
        }

        [Test]
        public void Duplicate_BeforeDelivery_DeliveredExactlyOnce()
        {
            var rb = new ReliableReceiveBuffer();
            rb.Accept(1, MessageType.Event, new byte[] { 1 });
            rb.Accept(1, MessageType.Event, new byte[] { 1 });   // retransmit of a not-yet-drained message
            Assert.AreEqual(1, rb.BufferedCount);
            Assert.IsTrue(rb.TryNext(out _, out _));
            Assert.IsFalse(rb.TryNext(out _, out _));            // only once
        }

        [Test]
        public void Duplicate_AfterDelivery_Ignored()
        {
            var rb = new ReliableReceiveBuffer();
            rb.Accept(1, MessageType.Event, new byte[] { 1 });
            Assert.IsTrue(rb.TryNext(out _, out _));             // delivered, NextExpected -> 2
            rb.Accept(1, MessageType.Event, new byte[] { 1 });   // late retransmit of an already-delivered seq
            Assert.AreEqual(0, rb.BufferedCount);
            Assert.IsFalse(rb.TryNext(out _, out _));
        }

        [Test]
        public void Gap_HoldsLaterMessages()
        {
            var rb = new ReliableReceiveBuffer();
            rb.Accept(1, MessageType.Event, new byte[] { 1 });
            Assert.IsTrue(rb.TryNext(out _, out _));             // 1 delivered
            rb.Accept(3, MessageType.Event, new byte[] { 3 });   // 2 missing
            Assert.IsFalse(rb.TryNext(out _, out _));            // 3 waits behind the gap
            Assert.AreEqual(1, rb.BufferedCount);
            rb.Accept(2, MessageType.Event, new byte[] { 2 });
            Assert.IsTrue(rb.TryNext(out _, out var p2));
            Assert.AreEqual(new byte[] { 2 }, p2);
            Assert.IsTrue(rb.TryNext(out _, out var p3));
            Assert.AreEqual(new byte[] { 3 }, p3);
        }

        [Test]
        public void TypeAndPayload_Preserved()
        {
            var rb = new ReliableReceiveBuffer();
            rb.Accept(1, MessageType.State, new byte[] { 7, 8, 9 });
            Assert.IsTrue(rb.TryNext(out var type, out var payload));
            Assert.AreEqual(MessageType.State, type);
            Assert.AreEqual(new byte[] { 7, 8, 9 }, payload);
        }

        [Test]
        public void SeqZero_Ignored()
        {
            var rb = new ReliableReceiveBuffer();
            rb.Accept(0, MessageType.Event, new byte[] { 1 });
            Assert.AreEqual(0, rb.BufferedCount);
            Assert.IsFalse(rb.TryNext(out _, out _));
        }

        [Test]
        public void OversizedPayload_Dropped()
        {
            var rb = new ReliableReceiveBuffer();
            rb.Accept(1, MessageType.Event, new byte[4096]);     // way over the per-payload cap
            Assert.AreEqual(0, rb.BufferedCount);
            Assert.IsFalse(rb.TryNext(out _, out _));
        }

        [Test]
        public void PayloadAtMaximumAccepted_OneOverMaximumDropped()
        {
            var rb = new ReliableReceiveBuffer();
            rb.Accept(1, MessageType.Event, new byte[512]);
            Assert.AreEqual(1, rb.BufferedCount);
            Assert.IsTrue(rb.TryNext(out _, out var payload));
            Assert.AreEqual(512, payload.Length);

            rb.Accept(2, MessageType.Event, new byte[513]);
            Assert.AreEqual(0, rb.BufferedCount);
            Assert.IsFalse(rb.TryNext(out _, out _));
        }

        [Test]
        public void ReorderWindow_CapsAtTwoHundredFiftySix_ButAcceptsGapFiller()
        {
            var rb = new ReliableReceiveBuffer();
            for (ushort seq = 2; seq <= 257; seq++)
                rb.Accept(seq, MessageType.Event, new byte[] { (byte)seq });

            Assert.AreEqual(256, rb.BufferedCount);
            rb.Accept(258, MessageType.Event, new byte[] { 2 });
            Assert.AreEqual(256, rb.BufferedCount);

            rb.Accept(1, MessageType.Event, new byte[] { 1 });
            Assert.IsTrue(rb.TryNext(out _, out var first));
            Assert.AreEqual(new byte[] { 1 }, first);
            Assert.AreEqual(256, rb.BufferedCount);
        }

        [Test]
        public void Wraparound_DeliversAcrossMax_AndDropsPreWrapDup()
        {
            var rb = new ReliableReceiveBuffer();
            for (int s = 1; s <= 65535; s++)
            {
                rb.Accept((ushort)s, MessageType.Event, default);
                Assert.IsTrue(rb.TryNext(out _, out _));
            }
            Assert.AreEqual(1, rb.NextExpected);                 // wrapped past reserved 0 back to 1
            rb.Accept(65535, MessageType.Event, default);        // late dup from before the wrap
            Assert.AreEqual(0, rb.BufferedCount);                // recognized as already delivered
            Assert.IsFalse(rb.TryNext(out _, out _));
            rb.Accept(1, MessageType.State, new byte[] { 42 });  // first message after the wrap
            Assert.IsTrue(rb.TryNext(out var type, out var payload));
            Assert.AreEqual(MessageType.State, type);
            Assert.AreEqual(new byte[] { 42 }, payload);
        }
    }
}
