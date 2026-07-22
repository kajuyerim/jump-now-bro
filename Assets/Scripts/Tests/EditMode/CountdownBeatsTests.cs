using NUnit.Framework;
using JumpNowBro.Networking;

namespace JumpNowBro.Tests
{
    public class CountdownBeatsTests
    {
        [Test]
        public void GoTick_ComposesClockLeadAndLeadIn()
        {
            Assert.AreEqual(1000u + 12u + CountdownBeats.LeadInTicks, CountdownBeats.GoTick(1000u, 12u));
            Assert.AreEqual(90u, CountdownBeats.LeadInTicks);   // 3 beats x 30 ticks — the wire contract behind the cadence
        }

        [Test]
        public void CurrentBeat_ProgressesThroughExactBoundaries()
        {
            const uint go = 1000u;
            Assert.AreEqual(4, CountdownBeats.CurrentBeat(go, 909u));   // before the first beat
            Assert.AreEqual(3, CountdownBeats.CurrentBeat(go, 910u));   // 90 ticks out: "3" starts
            Assert.AreEqual(3, CountdownBeats.CurrentBeat(go, 939u));
            Assert.AreEqual(2, CountdownBeats.CurrentBeat(go, 940u));
            Assert.AreEqual(1, CountdownBeats.CurrentBeat(go, 970u));
            Assert.AreEqual(1, CountdownBeats.CurrentBeat(go, 999u));
            Assert.AreEqual(0, CountdownBeats.CurrentBeat(go, 1000u));  // GO lands ON goTick
            Assert.AreEqual(0, CountdownBeats.CurrentBeat(go, 1050u));  // past but fresh: still GO
        }

        [Test]
        public void CurrentBeat_LateArrival_SkipsMissedBeats()
        {
            // A driver that last saw clock 905 (beat 4) and next sees 975 must render "1" directly.
            Assert.AreEqual(4, CountdownBeats.CurrentBeat(1000u, 905u));
            Assert.AreEqual(1, CountdownBeats.CurrentBeat(1000u, 975u));
        }

        [Test]
        public void IsStale_ExactBoundary()
        {
            Assert.IsFalse(CountdownBeats.IsStale(1000u, 1000u + CountdownBeats.StaleTicks));       // exactly 2 s: still shows GO
            Assert.IsTrue(CountdownBeats.IsStale(1000u, 1000u + CountdownBeats.StaleTicks + 1u));   // one past: suppressed
            Assert.IsFalse(CountdownBeats.IsStale(1000u, 999u));                                    // future GO is never stale
        }

        [Test]
        public void WrapAround_BeatAndStalenessSurviveU32Wrap()
        {
            // GO just past the wrap, clock just before it: the unsigned distance is small and correct.
            uint go = 10u;
            uint clock = uint.MaxValue - 15u;                            // 26 ticks before goTick
            Assert.AreEqual(1, CountdownBeats.CurrentBeat(go, clock));
            Assert.IsFalse(CountdownBeats.IsStale(go, clock));
            Assert.IsTrue(CountdownBeats.IsStale(uint.MaxValue - 200u, 50u));   // GO 251 ticks behind the wrapped clock
            Assert.IsTrue(CountdownBeats.ShouldReplace(uint.MaxValue - 5u, 20u, incomingIsFromP1: false));   // wrapped = newer
        }

        [Test]
        public void ShouldReplace_NewerWinsRegardlessOfSender()
        {
            Assert.IsTrue(CountdownBeats.ShouldReplace(1000u, 1001u, incomingIsFromP1: false));
            Assert.IsFalse(CountdownBeats.ShouldReplace(1001u, 1000u, incomingIsFromP1: true));    // older never replaces
        }

        [Test]
        public void ShouldReplace_ExactTie_ResolvesToP1OnBothEnds()
        {
            // Host holds its own (P1) countdown; the client's equal-tick arrival (from P2) must NOT replace.
            Assert.IsFalse(CountdownBeats.ShouldReplace(1000u, 1000u, incomingIsFromP1: false));
            // Client holds its own (P2) countdown; the host's equal-tick arrival (from P1) MUST replace.
            Assert.IsTrue(CountdownBeats.ShouldReplace(1000u, 1000u, incomingIsFromP1: true));
        }
    }
}
