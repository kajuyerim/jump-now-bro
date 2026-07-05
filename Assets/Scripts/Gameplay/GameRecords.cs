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
            public int prevBestTimeMs;                // the best BEFORE this run (-1 when none): the PB delta's baseline
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

        /// Picker sublabel: "0:42.10 | 3 deaths" ("| flawless" when the fewest-deaths record is zero —
        /// that IS the flawless-ever badge, no extra key), or null when never completed in that mode.
        public static string SublabelFor(Mode m, int level) =>
            TryGetBest(m, level, out int t, out int d)
                ? $"{TimeFormat.MinutesSecondsCentis(t)} | {(d == 0 ? "flawless" : $"{d} {(d == 1 ? "death" : "deaths")}")}"
                : null;

        public static RunReport ReportRun(Mode m, int level, in LevelRunStats stats)
        {
            if (level < 0) return new RunReport { prevBestTimeMs = -1 };

            var r = new RunReport { firstCompletion = !IsCompleted(m, level) };
            int bestT = PlayerPrefs.GetInt(Key(m, level, "bestTimeMs"), int.MaxValue);
            int bestD = PlayerPrefs.GetInt(Key(m, level, "fewestDeaths"), int.MaxValue);
            // Sentinel is unambiguous: a stored best can never equal MaxValue (a write needs timeMs < bestT).
            r.prevBestTimeMs = bestT == int.MaxValue ? -1 : bestT;
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
            PlayerPrefs.DeleteKey(KLifePlay);
            PlayerPrefs.DeleteKey(KLifeDeaths);
            PlayerPrefs.DeleteKey(KLifeSwaps);
            PlayerPrefs.Save();
            OnChanged?.Invoke();
        }

        // ---- lifetime totals (#149): per-machine, mode-agnostic, COMPLETED levels only (aborted runs
        // and menu time do not count). Monotonic — no session flow resets these; only the wipe above. ----

        const string KLifePlay   = "lifetime.playtimeSec";
        const string KLifeDeaths = "lifetime.deaths";
        const string KLifeSwaps  = "lifetime.swaps";

        public static int LifetimePlaytimeSec => PlayerPrefs.GetInt(KLifePlay, 0);
        public static int LifetimeDeaths      => PlayerPrefs.GetInt(KLifeDeaths, 0);
        public static int LifetimeSwaps       => PlayerPrefs.GetInt(KLifeSwaps, 0);

        /// One call per completed level; the caller's accounting latch keeps rejoin re-sends out.
        public static void AddLifetime(in LevelRunStats stats)
        {
            PlayerPrefs.SetInt(KLifePlay, LifetimePlaytimeSec + (stats.timeMs + 500) / 1000);
            PlayerPrefs.SetInt(KLifeDeaths, LifetimeDeaths + stats.deaths);
            PlayerPrefs.SetInt(KLifeSwaps, LifetimeSwaps + stats.swaps);
            PlayerPrefs.Save();
        }
    }
}
