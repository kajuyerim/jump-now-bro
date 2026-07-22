namespace JumpNowBro.Networking
{
    /// v2.5 synced GO countdown (#152), pure tick math. goTick is the GO beat on the shared client
    /// input-tick timeline (the swap apply_at_tick coordinate); beats 3/2/1 precede it by whole BeatTicks.
    /// Engine-free and CI-tested; every comparison is wrap-safe via SeqMath so a countdown survives a
    /// u32 tick wrap.
    public static class CountdownBeats
    {
        public const int BeatTicks = 30;                                 // 0.5 s at the 60 Hz fixed step
        public const int BeatCount = 3;                                  // "3", "2", "1" before GO
        public const uint LeadInTicks = (uint)(BeatCount * BeatTicks);   // the sender's network lead rides on top
        public const uint StaleTicks = 120;                              // 2 s past GO, a late arrival is noise, not news

        /// GO-beat tick for a press at `clockAtSend` with the sender's network lead.
        public static uint GoTick(uint clockAtSend, uint networkLeadTicks) =>
            clockAtSend + networkLeadTicks + LeadInTicks;

        /// 4 = before the first beat; 3/2/1 = that numeral is current; 0 = GO reached (or passed).
        public static int CurrentBeat(uint goTick, uint clock)
        {
            if (!SeqMath.IsNewer32(goTick, clock)) return 0;             // clock reached (or passed) GO
            uint remaining = goTick - clock;                             // wrap-correct unsigned distance
            uint beat = (remaining + (uint)BeatTicks - 1) / (uint)BeatTicks;   // ceil
            return beat > BeatCount ? BeatCount + 1 : (int)beat;
        }

        /// GO passed more than StaleTicks ago — suppress entirely rather than blurt a stale GO.
        public static bool IsStale(uint goTick, uint clock) =>
            SeqMath.IsNewer32(clock, goTick) && clock - goTick > StaleTicks;

        /// Newest-GO-wins convergence for racing countdowns. incomingIsFromP1 = the ORIGINATING sender is
        /// P1, for local presses and receives alike (a host local press passes true, a client local press
        /// false; receives are the flip) — an exact tie then resolves to P1 on BOTH screens, so tint and
        /// timing converge regardless of arrival order. Strict-newer alone cannot converge a tie: each end
        /// would keep its own.
        public static bool ShouldReplace(uint currentGoTick, uint incomingGoTick, bool incomingIsFromP1) =>
            SeqMath.IsNewer32(incomingGoTick, currentGoTick)
            || (incomingGoTick == currentGoTick && incomingIsFromP1);
    }
}
