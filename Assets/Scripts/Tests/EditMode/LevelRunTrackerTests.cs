using NUnit.Framework;
using JumpNowBro.Util;

namespace JumpNowBro.Tests
{
    /// #130 run tracker: accumulation, streak segmentation, and the Begin..Complete lifecycle guards
    /// (every feed outside a live run must be a no-op, so stray events cannot corrupt a latched result).
    public class LevelRunTrackerTests
    {
        const double Dt = 1.0 / 60.0;

        static void Run(LevelRunTracker t, double seconds)
        {
            int steps = (int)System.Math.Round(seconds / Dt);
            for (int i = 0; i < steps; i++) t.Tick(Dt);
        }

        [Test]
        public void Begin_TickAccumulates_Time()
        {
            var t = new LevelRunTracker();
            t.Begin();
            Run(t, 10.0);
            var s = t.Complete();
            Assert.AreEqual(10_000, s.timeMs);
        }

        [Test]
        public void ZeroDeaths_StreakEqualsFullTime()
        {
            var t = new LevelRunTracker();
            t.Begin();
            Run(t, 7.5);
            var s = t.Complete();
            Assert.AreEqual(0, s.deaths);
            Assert.AreEqual(s.timeMs, s.streakMs);
        }

        [Test]
        public void DeathsSplitStreak_BestSegmentWins()
        {
            var t = new LevelRunTracker();
            t.Begin();
            Run(t, 3.0);
            t.NotifyDeath();
            Run(t, 5.0);
            t.NotifyDeath();
            Run(t, 2.0);
            var s = t.Complete();
            Assert.AreEqual(2, s.deaths);
            Assert.AreEqual(10_000, s.timeMs);
            Assert.AreEqual(5_000, s.streakMs);
        }

        [Test]
        public void SwapApplied_Counted()
        {
            var t = new LevelRunTracker();
            t.Begin();
            t.NotifySwapApplied();
            t.NotifySwapApplied();
            t.NotifySwapApplied();
            Assert.AreEqual(3, t.Complete().swaps);
        }

        [Test]
        public void Complete_IsIdempotent_ReturnsLatchedStats()
        {
            var t = new LevelRunTracker();
            t.Begin();
            Run(t, 2.0);
            t.NotifyDeath();
            var first = t.Complete();
            var second = t.Complete();
            Assert.AreEqual(first.timeMs, second.timeMs);
            Assert.AreEqual(first.deaths, second.deaths);
            Assert.AreEqual(first.swaps, second.swaps);
            Assert.AreEqual(first.streakMs, second.streakMs);
        }

        [Test]
        public void AfterComplete_TickDeathSwap_Ignored()
        {
            var t = new LevelRunTracker();
            t.Begin();
            Run(t, 2.0);
            var latched = t.Complete();

            // A pending swap applying during the summary hold, or a stray death, must not mutate the latch.
            Run(t, 5.0);
            t.NotifyDeath();
            t.NotifySwapApplied();
            var again = t.Complete();
            Assert.AreEqual(latched.timeMs, again.timeMs);
            Assert.AreEqual(0, again.deaths);
            Assert.AreEqual(0, again.swaps);
        }

        [Test]
        public void CompleteWithoutBegin_ReturnsZeroStats()
        {
            var t = new LevelRunTracker();
            var s = t.Complete();
            Assert.AreEqual(0, s.timeMs);
            Assert.AreEqual(0, s.deaths);
            Assert.AreEqual(0, s.swaps);
            Assert.AreEqual(0, s.streakMs);
            Assert.IsFalse(t.Running);
        }

        [Test]
        public void Reset_ThenBegin_StartsFresh()
        {
            var t = new LevelRunTracker();
            t.Begin();
            Run(t, 4.0);
            t.NotifyDeath();
            t.Complete();

            t.Reset();
            Assert.IsFalse(t.Running);
            t.Begin();
            Run(t, 1.0);
            var s = t.Complete();
            Assert.AreEqual(1_000, s.timeMs);
            Assert.AreEqual(0, s.deaths);
        }

        [Test]
        public void TickWhileNotRunning_Ignored()
        {
            var t = new LevelRunTracker();
            t.Tick(5.0);                          // before Begin: dropped
            t.Begin();
            var s = t.Complete();
            Assert.AreEqual(0, s.timeMs);
        }

        [Test]
        public void NonPositiveDt_Ignored()
        {
            var t = new LevelRunTracker();
            t.Begin();
            t.Tick(0.0);
            t.Tick(-1.0);
            Assert.AreEqual(0, t.Complete().timeMs);
        }
    }
}
