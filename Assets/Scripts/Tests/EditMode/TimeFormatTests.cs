using NUnit.Framework;
using JumpNowBro.Util;

namespace JumpNowBro.Tests
{
    public class TimeFormatTests
    {
        [Test]
        public void Zero_Formats_AsZero()
        {
            Assert.AreEqual("0:00.00", TimeFormat.MinutesSecondsCentis(0));
        }

        [Test]
        public void SecondsAndCentis_Truncated()
        {
            // 9.996 s truncates to .99, never rounds up to 10.00 (a run must not display faster than it was).
            Assert.AreEqual("0:09.99", TimeFormat.MinutesSecondsCentis(9_996));
        }

        [Test]
        public void MinutesCarry()
        {
            Assert.AreEqual("1:23.45", TimeFormat.MinutesSecondsCentis(83_450));
        }

        [Test]
        public void LargeMinutes_Unbounded()
        {
            Assert.AreEqual("60:00.00", TimeFormat.MinutesSecondsCentis(3_600_000));
        }

        [Test]
        public void Negative_ClampsToZero()
        {
            Assert.AreEqual("0:00.00", TimeFormat.MinutesSecondsCentis(-42));
        }

        [Test]
        public void SignedDelta_Negative()
        {
            Assert.AreEqual("-0:02.31", TimeFormat.SignedDeltaCentis(-2_310));
        }

        [Test]
        public void SignedDelta_Positive()
        {
            Assert.AreEqual("+0:04.10", TimeFormat.SignedDeltaCentis(4_100));
        }

        [Test]
        public void SignedDelta_Zero_ReadsPositive()
        {
            Assert.AreEqual("+0:00.00", TimeFormat.SignedDeltaCentis(0));
        }

        [Test]
        public void SignedDelta_MinuteCarry()
        {
            Assert.AreEqual("-1:23.45", TimeFormat.SignedDeltaCentis(-83_450));
        }

        [Test]
        public void SignedDelta_SubCentisecond_TruncatesToZeroMagnitude()
        {
            // A 1 ms improvement renders "-0:00.00" — truncation is a decision, pinned here.
            Assert.AreEqual("-0:00.00", TimeFormat.SignedDeltaCentis(-1));
        }

        [Test]
        public void SignedDelta_IntMinValue_DoesNotThrow()
        {
            Assert.DoesNotThrow(() => TimeFormat.SignedDeltaCentis(int.MinValue));
        }
    }
}
