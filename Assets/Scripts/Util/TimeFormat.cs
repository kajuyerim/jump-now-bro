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
    }
}
