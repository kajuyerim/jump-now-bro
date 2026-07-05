namespace JumpNowBro.Util
{
    public static class TimeFormat
    {
        /// m:ss.cc with TRUNCATED centiseconds (timer convention: never display a run faster than it was).
        public static string MinutesSecondsCentis(int ms)
        {
            if (ms < 0) ms = 0;
            int totalCentis = ms / 10;
            int centis = totalCentis % 100;
            int totalSeconds = totalCentis / 100;
            int seconds = totalSeconds % 60;
            int minutes = totalSeconds / 60;
            return $"{minutes}:{seconds:D2}.{centis:D2}";
        }

        /// Signed PB delta, "-0:02.31" / "+0:04.10" (zero reads "+0:00.00" — an equal time is not a
        /// record, so it sits on the slower side). Magnitude on long: Math.Abs(int.MinValue) throws.
        public static string SignedDeltaCentis(int deltaMs)
        {
            long magnitude = deltaMs;
            if (magnitude < 0) magnitude = -magnitude;
            return (deltaMs < 0 ? "-" : "+") + MinutesSecondsCentis((int)System.Math.Min(magnitude, int.MaxValue));
        }
    }
}
