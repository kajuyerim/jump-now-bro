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
    }
}
