using System;
using NUnit.Framework;
using JumpNowBro.Networking;

namespace JumpNowBro.Tests
{
    public class PacketHeaderTests
    {
        static PacketHeader Sample() => new PacketHeader
        {
            type = MessageType.State,
            seq = 0x1234,
            ack = 0xABCD,
            ackBits = 0x00FF,
            timestamp = 0xDEADBEEF
        };

        [Test]
        public void Size_Is11()
        {
            Assert.AreEqual(11, PacketHeader.Size);
        }

        [TestCase(MessageType.Hello, 0)]
        [TestCase(MessageType.Welcome, 1)]
        [TestCase(MessageType.Goodbye, 2)]
        [TestCase(MessageType.Input, 3)]
        [TestCase(MessageType.State, 4)]
        [TestCase(MessageType.Event, 5)]
        [TestCase(MessageType.Ping, 6)]
        [TestCase(MessageType.Pong, 7)]
        public void MessageType_KeepsExistingWireValue(MessageType type, int wireValue)
        {
            var buf = new byte[PacketHeader.Size];
            new PacketHeader { type = type }.Write(buf);
            Assert.AreEqual(wireValue, buf[0]);
        }

        [Test]
        public void Write_Then_TryRead_RoundTripsAllFields()
        {
            var src = Sample();
            var buf = new byte[PacketHeader.Size];
            src.Write(buf);

            Assert.IsTrue(PacketHeader.TryRead(buf, out var dst));
            Assert.AreEqual(src.type, dst.type);
            Assert.AreEqual(src.seq, dst.seq);
            Assert.AreEqual(src.ack, dst.ack);
            Assert.AreEqual(src.ackBits, dst.ackBits);
            Assert.AreEqual(src.timestamp, dst.timestamp);
        }

        [TestCase((ushort)0)]
        [TestCase((ushort)1)]
        [TestCase((ushort)0x7FFF)]
        [TestCase((ushort)0x8000)]
        [TestCase((ushort)0xFFFF)]
        public void Seq_RoundTrips_AtBoundaryValues(ushort seq)
        {
            var buf = new byte[PacketHeader.Size];
            new PacketHeader { seq = seq }.Write(buf);

            Assert.IsTrue(PacketHeader.TryRead(buf, out var dst));
            Assert.AreEqual(seq, dst.seq);
        }

        [Test]
        public void Write_IsBigEndian()
        {
            var buf = new byte[PacketHeader.Size];
            new PacketHeader { seq = 0x0102, timestamp = 0x01020304 }.Write(buf);

            Assert.AreEqual(0x01, buf[1]);   // seq high byte first
            Assert.AreEqual(0x02, buf[2]);
            Assert.AreEqual(0x01, buf[7]);   // timestamp most-significant byte first
            Assert.AreEqual(0x04, buf[10]);
        }

        [Test]
        public void TryRead_ShortBuffer_ReturnsFalse()
        {
            Assert.IsFalse(PacketHeader.TryRead(new byte[10], out _));
            Assert.IsFalse(PacketHeader.TryRead(ReadOnlySpan<byte>.Empty, out _));
        }

        [Test]
        public void Write_BufferTooSmall_Throws()
        {
            Assert.Throws<ArgumentException>(() => new PacketHeader().Write(new byte[10]));
        }

        [Test]
        public void Write_TouchesExactly11Bytes_LeavesPayloadIntact()
        {
            const byte sentinel = 0xAA;
            var buf = new byte[PacketHeader.Size + 4];
            for (int i = 0; i < buf.Length; i++) buf[i] = sentinel;   // "payload" past the header

            Sample().Write(buf);

            for (int i = PacketHeader.Size; i < buf.Length; i++)
                Assert.AreEqual(sentinel, buf[i], $"byte {i} past the header was clobbered");
        }

        [Test]
        public void TryRead_LargerBuffer_ReadsOnlyFirst11()
        {
            var src = Sample();
            var buf = new byte[PacketHeader.Size + 4];
            src.Write(buf);
            buf[PacketHeader.Size] = 0x99;       // junk "payload" after the header must be ignored
            buf[PacketHeader.Size + 3] = 0x77;

            Assert.IsTrue(PacketHeader.TryRead(buf, out var dst));
            Assert.AreEqual(src.type, dst.type);
            Assert.AreEqual(src.seq, dst.seq);
            Assert.AreEqual(src.ack, dst.ack);
            Assert.AreEqual(src.ackBits, dst.ackBits);
            Assert.AreEqual(src.timestamp, dst.timestamp);
        }
    }
}
