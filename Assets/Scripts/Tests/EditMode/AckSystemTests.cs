using System.Collections.Generic;
using NUnit.Framework;
using JumpNowBro.Networking;

namespace JumpNowBro.Tests
{
    public class AckSystemTests
    {
        [Test]
        public void NothingReceived_GeneratesNoneAck()
        {
            var acks = new AckSystem();
            acks.GenerateAck(out var ack, out var bits);
            Assert.AreEqual(0, ack);
            Assert.AreEqual(0, bits);
            Assert.AreEqual(0, acks.Latest);
        }

        [Test]
        public void FirstReceive_SetsLatest_NoHistoryBits()
        {
            var acks = new AckSystem();
            acks.OnReceived(5);
            acks.GenerateAck(out var ack, out var bits);
            Assert.AreEqual(5, ack);
            Assert.AreEqual(0, bits);
        }

        [Test]
        public void SequentialReceives_FillHistory()
        {
            var acks = new AckSystem();
            for (ushort s = 1; s <= 5; s++) acks.OnReceived(s);
            acks.GenerateAck(out var ack, out var bits);
            Assert.AreEqual(5, ack);
            Assert.AreEqual(0b1111, bits);   // seqs 4,3,2,1 -> bits 0..3
        }

        [Test]
        public void Gap_LeavesMissingSeqUnacked()
        {
            var acks = new AckSystem();
            acks.OnReceived(1);
            acks.OnReceived(2);
            acks.OnReceived(4);              // 3 skipped
            acks.GenerateAck(out var ack, out var bits);
            Assert.AreEqual(4, ack);
            Assert.AreEqual(0b110, bits);    // seq3 missing (bit0=0), seq2 (bit1), seq1 (bit2)
        }

        [Test]
        public void HistoryAtAckBitsBoundary_TracksExactlySixteenPrevious()
        {
            var acks = new AckSystem();
            acks.OnReceived(1);
            acks.OnReceived(17); // seq 1 is exactly 16 positions behind the latest

            acks.GenerateAck(out var ack, out var bits);

            Assert.AreEqual(17, ack);
            Assert.AreEqual(1 << 15, bits);
        }

        [Test]
        public void ForwardGapBeyondInternalHistory_ClearsOldHistory()
        {
            var acks = new AckSystem();
            acks.OnReceived(1);
            acks.OnReceived(2);
            acks.OnReceived(35); // shift is 33, beyond the internal history word

            acks.GenerateAck(out var ack, out var bits);

            Assert.AreEqual(35, ack);
            Assert.AreEqual(0, bits);
        }

        [Test]
        public void StaleSequenceBeyondInternalHistory_DoesNotAlterAck()
        {
            var acks = new AckSystem();
            acks.OnReceived(100);
            acks.OnReceived(67); // 33 behind Latest: outside the uint history word
            acks.GenerateAck(out var ack, out var bits);

            Assert.AreEqual(100, ack);
            Assert.AreEqual(0, bits);
        }

        [Test]
        public void OutOfOrder_FillsBitRetroactively()
        {
            var acks = new AckSystem();
            acks.OnReceived(1);
            acks.OnReceived(2);
            acks.OnReceived(4);              // gap at 3
            acks.OnReceived(3);              // arrives late
            acks.GenerateAck(out var ack, out var bits);
            Assert.AreEqual(4, ack);
            Assert.AreEqual(0b111, bits);    // 3,2,1 now all received
        }

        [Test]
        public void Duplicate_IsIdempotent()
        {
            var acks = new AckSystem();
            acks.OnReceived(1);
            acks.OnReceived(2);
            acks.OnReceived(2);              // dup
            acks.OnReceived(1);              // dup older
            acks.GenerateAck(out var ack, out var bits);
            Assert.AreEqual(2, ack);
            Assert.AreEqual(0b1, bits);
        }

        [Test]
        public void Wraparound_AcrossMax_Works()
        {
            var acks = new AckSystem();
            acks.OnReceived(65535);
            acks.OnReceived(1);              // wraps past 0 (reserved)
            acks.GenerateAck(out var ack, out var bits);
            Assert.AreEqual(1, ack);
            Assert.AreEqual(0b10, bits);     // seq0 reserved (bit0=0), seq65535 (bit1)
        }

        [Test]
        public void ForEachAcked_YieldsAckAndSetBits()
        {
            var got = new List<ushort>();
            AckSystem.ForEachAcked(10, 0b101, s => got.Add(s));   // bits 0 and 2
            CollectionAssert.AreEquivalent(new ushort[] { 10, 9, 7 }, got);
        }

        [Test]
        public void ForEachAcked_AckZero_YieldsNothing()
        {
            var got = new List<ushort>();
            AckSystem.ForEachAcked(0, 0xFFFF, s => got.Add(s));
            Assert.IsEmpty(got);
        }

        [Test]
        public void ForEachAcked_SkipsReservedSeqZero()
        {
            var got = new List<ushort>();
            AckSystem.ForEachAcked(1, 0b1, s => got.Add(s));   // bit0 -> seq (1-1-0)=0, must be skipped
            CollectionAssert.AreEquivalent(new ushort[] { 1 }, got);
        }
    }
}
