using System;

namespace JumpNowBro.Util
{
    /// One completed level run's accounting (#130). Times in whole milliseconds; streakMs is the longest
    /// deathless span (== timeMs when deaths == 0, by construction).
    public struct LevelRunStats
    {
        public int timeMs;
        public int deaths;
        public int swaps;
        public int streakMs;
    }

    /// Engine-free per-level stats accumulator (#130), CI-tested. The caller owns the gating: feed Tick
    /// with the fixed dt only when the sim actually steps, NotifyDeath/NotifySwapApplied at the observable
    /// events. Every feed is ignored outside Begin..Complete, so stray notifications (a swap applying
    /// during the summary hold, a death event with no run live) cannot corrupt a latched result.
    public sealed class LevelRunTracker
    {
        double elapsedSeconds;
        double segmentStartSeconds;   // start of the current deathless span
        double bestSegmentSeconds;
        int deaths;
        int swaps;
        bool completed;
        LevelRunStats latched;

        public bool Running { get; private set; }

        public void Begin()
        {
            elapsedSeconds = 0.0;
            segmentStartSeconds = 0.0;
            bestSegmentSeconds = 0.0;
            deaths = 0;
            swaps = 0;
            completed = false;
            latched = default;
            Running = true;
        }

        public void Tick(double dtSeconds)
        {
            if (!Running || dtSeconds <= 0.0) return;
            elapsedSeconds += dtSeconds;
        }

        /// Streak boundary sits at the death EVENT, so the respawn freeze accrues to the next segment.
        public void NotifyDeath()
        {
            if (!Running) return;
            deaths++;
            CloseSegment();
            segmentStartSeconds = elapsedSeconds;
        }

        public void NotifySwapApplied()
        {
            if (!Running) return;
            swaps++;
        }

        /// First call closes the run and latches the stats; later calls return the latch unchanged.
        /// Complete without Begin returns zeroed stats.
        public LevelRunStats Complete()
        {
            if (completed) return latched;
            if (!Running) return default;
            CloseSegment();
            latched = new LevelRunStats
            {
                timeMs = ToMs(elapsedSeconds),
                deaths = deaths,
                swaps = swaps,
                streakMs = ToMs(bestSegmentSeconds),
            };
            Running = false;
            completed = true;
            return latched;
        }

        public void Reset()
        {
            Running = false;
            completed = false;
            latched = default;
            elapsedSeconds = segmentStartSeconds = bestSegmentSeconds = 0.0;
            deaths = swaps = 0;
        }

        void CloseSegment()
        {
            double span = elapsedSeconds - segmentStartSeconds;
            if (span > bestSegmentSeconds) bestSegmentSeconds = span;
        }

        static int ToMs(double seconds) => (int)Math.Round(Math.Min(seconds, 2_000_000.0) * 1000.0);
    }
}
