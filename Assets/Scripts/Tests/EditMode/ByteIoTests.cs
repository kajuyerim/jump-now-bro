using System;
using NUnit.Framework;
using JumpNowBro.Networking;

namespace JumpNowBro.Tests
{
    public class ByteIoTests
    {
        [Test]
        public void RoundTrips_AllPrimitives()
        {
            var buf = new byte[64];
            var w = new ByteWriter(buf);
            w.WriteByte(0xAB);
            w.WriteUShort(0x1234);
            w.WriteUInt(0x89ABCDEFu);
            w.WriteInt(-42);
            w.WriteString("hi");
            w.WriteBytes(new byte[] { 1, 2, 3 });

            var r = new ByteReader(buf.AsSpan(0, w.Position));
            Assert.IsTrue(r.TryReadByte(out var b)); Assert.AreEqual(0xAB, b);
            Assert.IsTrue(r.TryReadUShort(out var us)); Assert.AreEqual(0x1234, us);
            Assert.IsTrue(r.TryReadUInt(out var ui)); Assert.AreEqual(0x89ABCDEFu, ui);
            Assert.IsTrue(r.TryReadInt(out var i)); Assert.AreEqual(-42, i);
            Assert.IsTrue(r.TryReadString(out var s)); Assert.AreEqual("hi", s);
            Assert.IsTrue(r.TryReadBytes(3, out var raw)); Assert.AreEqual(new byte[] { 1, 2, 3 }, raw.ToArray());
            Assert.AreEqual(0, r.Remaining);
        }

        [Test]
        public void BigEndian_MatchesHeaderConvention()
        {
            var u16 = new byte[2];
            new ByteWriter(u16).WriteUShort(0x0102);
            Assert.AreEqual(new byte[] { 0x01, 0x02 }, u16);          // high byte first

            var u32 = new byte[4];
            new ByteWriter(u32).WriteUInt(0x01020304u);
            Assert.AreEqual(new byte[] { 0x01, 0x02, 0x03, 0x04 }, u32);
        }

        [Test]
        public void Reader_Truncated_FailsGracefully_WithoutConsuming()
        {
            var r = new ByteReader(new byte[] { 0xFF });             // one byte, ask for two
            Assert.IsFalse(r.TryReadUShort(out _));
            Assert.AreEqual(1, r.Remaining);                          // not consumed on failure
            Assert.IsTrue(r.TryReadByte(out _));
            Assert.IsFalse(r.TryReadByte(out _));                     // now empty
        }

        [Test]
        public void Reader_TruncatedString_IsAllOrNothing()
        {
            // length prefix says 5, but only 2 body bytes follow
            var r = new ByteReader(new byte[] { 0x00, 0x05, 0x41, 0x42 });
            Assert.IsFalse(r.TryReadString(out var s));
            Assert.IsNull(s);
            Assert.AreEqual(4, r.Remaining);                          // prefix not consumed
        }

        [Test]
        public void Reader_OversizedStringLengthPrefix_Rejected()
        {
            // Length prefix claims 5000 bytes — over MaxStringLength. Reject without allocating; prefix not consumed.
            var r = new ByteReader(new byte[] { (byte)(5000 >> 8), (byte)(5000 & 0xFF), 0x41, 0x42 });
            Assert.IsFalse(r.TryReadString(out var s));
            Assert.IsNull(s);
            Assert.AreEqual(4, r.Remaining);
        }

        [Test]
        public void Writer_Overflow_Throws()
        {
            Assert.Throws<System.ArgumentException>(() =>
            {
                var w = new ByteWriter(new byte[1]);
                w.WriteUShort(0x1234);
            });
        }

        [Test]
        public void EmptyString_RoundTrips()
        {
            var buf = new byte[8];
            var w = new ByteWriter(buf);
            w.WriteString("");
            var r = new ByteReader(buf.AsSpan(0, w.Position));
            Assert.IsTrue(r.TryReadString(out var s));
            Assert.AreEqual("", s);
        }

        [Test]
        public void Reader_BytesZeroLength_SucceedsWithoutConsuming()
        {
            var r = new ByteReader(new byte[] { 1, 2 });
            Assert.IsTrue(r.TryReadBytes(0, out var bytes));
            Assert.AreEqual(0, bytes.Length);
            Assert.AreEqual(2, r.Remaining);
        }

        [Test]
        public void Reader_NegativeBytesLength_FailsWithoutConsuming()
        {
            var r = new ByteReader(new byte[] { 1, 2 });
            Assert.IsFalse(r.TryReadBytes(-1, out _));
            Assert.AreEqual(2, r.Remaining);
        }

        [Test]
        public void Reader_MaxStringLength_IsAcceptedAtExactBoundary()
        {
            string expected = new string('x', ByteReader.MaxStringLength);
            var buf = new byte[2 + expected.Length];
            new ByteWriter(buf).WriteString(expected);

            var r = new ByteReader(buf);
            Assert.IsTrue(r.TryReadString(out var actual));
            Assert.AreEqual(expected, actual);
            Assert.AreEqual(0, r.Remaining);
        }

        [Test]
        public void Reader_StringOneOverMaximum_IsRejectedWithoutConsuming()
        {
            int length = ByteReader.MaxStringLength + 1;
            var buf = new byte[2 + length];
            new ByteWriter(buf).WriteString(new string('x', length));
            var r = new ByteReader(buf);

            Assert.IsFalse(r.TryReadString(out var actual));
            Assert.IsNull(actual);
            Assert.AreEqual(buf.Length, r.Remaining);
        }

        [Test]
        public void Writer_Reserve_ExactCapacitySucceeds()
        {
            var w = new ByteWriter(new byte[2]);
            var reserved = w.Reserve(2);
            Assert.AreEqual(2, reserved.Length);
            Assert.AreEqual(2, w.Position);
        }

        [Test]
        public void SByte_RoundTrips()
        {
            var buf = new byte[3];
            var w = new ByteWriter(buf);
            w.WriteSByte(sbyte.MinValue);
            w.WriteSByte(0);
            w.WriteSByte(sbyte.MaxValue);
            var r = new ByteReader(buf);
            Assert.IsTrue(r.TryReadSByte(out var a)); Assert.AreEqual(sbyte.MinValue, a);
            Assert.IsTrue(r.TryReadSByte(out var b)); Assert.AreEqual(0, b);
            Assert.IsTrue(r.TryReadSByte(out var c)); Assert.AreEqual(sbyte.MaxValue, c);
            Assert.IsFalse(r.TryReadSByte(out _));
        }

        [TestCase(0f)]
        [TestCase(1f)]
        [TestCase(-1f)]
        [TestCase(3.14159265f)]
        [TestCase(float.MinValue)]
        [TestCase(float.MaxValue)]
        [TestCase(float.Epsilon)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        public void Float_RoundTrips_BitExact(float v)
        {
            var buf = new byte[4];
            new ByteWriter(buf).WriteFloat(v);
            Assert.IsTrue(new ByteReader(buf).TryReadFloat(out var roundtrip));
            Assert.AreEqual(v, roundtrip);
        }

        [Test]
        public void Float_NaN_RoundTrips_BitExact()
        {
            // NaN != NaN, so compare bit pattern explicitly.
            var buf = new byte[4];
            new ByteWriter(buf).WriteFloat(float.NaN);
            Assert.IsTrue(new ByteReader(buf).TryReadFloat(out var v));
            Assert.AreEqual(
                System.BitConverter.SingleToInt32Bits(float.NaN),
                System.BitConverter.SingleToInt32Bits(v));
        }

        [Test]
        public void Float_BigEndianByteOrder()
        {
            // 1.0f is 0x3F800000 — write should emit { 0x3F, 0x80, 0x00, 0x00 }.
            var buf = new byte[4];
            new ByteWriter(buf).WriteFloat(1.0f);
            Assert.AreEqual(new byte[] { 0x3F, 0x80, 0x00, 0x00 }, buf);
        }
    }
}
