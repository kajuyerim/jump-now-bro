using System;
using UnityEngine;
using JumpNowBro.Util;

namespace JumpNowBro.Gameplay
{
    /// PlayerPrefs-backed per-level records (#129): best time + fewest deaths, in SEPARATE solo and LAN
    /// tables (a one-brain solo run and a coordinated pair run are different skills). Persisting records
    /// supersedes the old no-save-files scope the same way #128's preferences did; #139 (Steam Cloud)
    /// already expects records on disk. Bests update independently: the best time may come from one run,
    /// the fewest deaths from another. "Completed" == a bestTimeMs key exists (written only at completion).
    public static class GameRecords
    {
        public enum Mode : byte { Solo, Lan }

        public struct RunReport
        {
            public bool newBestTime, newFewestDeaths, firstCompletion;
            public int bestTimeMs, fewestDeaths;      // post-update table values, so the card can print "Best: x"
        }

        /// Fired after any table change (ReportRun improvement / ResetAll) so pickers refresh their sublabels.
        public static event Action OnChanged;

        static string Key(Mode m, int level, string field) =>
            $"records.{(m == Mode.Solo ? "solo" : "lan")}.level{level}.{field}";

        public static bool TryGetBest(Mode m, int level, out int bestTimeMs, out int fewestDeaths)
        {
            bestTimeMs = PlayerPrefs.GetInt(Key(m, level, "bestTimeMs"), -1);
            fewestDeaths = PlayerPrefs.GetInt(Key(m, level, "fewestDeaths"), -1);
            return bestTimeMs >= 0;
        }

        public static bool IsCompleted(Mode m, int level) => PlayerPrefs.HasKey(Key(m, level, "bestTimeMs"));

        public static RunReport ReportRun(Mode m, int level, in LevelRunStats stats)
        {
            if (level < 0) return default;

            var r = new RunReport { firstCompletion = !IsCompleted(m, level) };
            int bestT = PlayerPrefs.GetInt(Key(m, level, "bestTimeMs"), int.MaxValue);
            int bestD = PlayerPrefs.GetInt(Key(m, level, "fewestDeaths"), int.MaxValue);
            r.newBestTime = stats.timeMs < bestT;
            r.newFewestDeaths = stats.deaths < bestD;
            if (r.newBestTime) PlayerPrefs.SetInt(Key(m, level, "bestTimeMs"), stats.timeMs);
            if (r.newFewestDeaths) PlayerPrefs.SetInt(Key(m, level, "fewestDeaths"), stats.deaths);
            r.bestTimeMs = r.newBestTime ? stats.timeMs : bestT;
            r.fewestDeaths = r.newFewestDeaths ? stats.deaths : bestD;
            if (r.newBestTime || r.newFewestDeaths)
            {
                PlayerPrefs.Save();                        // records are precious: one write per level completion
                OnChanged?.Invoke();
            }
            return r;
        }

        public static void ResetAll(int levelCount)
        {
            for (int m = 0; m < 2; m++)
                for (int i = 0; i < levelCount; i++)
                {
                    PlayerPrefs.DeleteKey(Key((Mode)m, i, "bestTimeMs"));
                    PlayerPrefs.DeleteKey(Key((Mode)m, i, "fewestDeaths"));
                }
            PlayerPrefs.Save();
            OnChanged?.Invoke();
        }
    }
}
