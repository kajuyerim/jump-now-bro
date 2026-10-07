using System.Collections.Generic;
using JumpNowBro.Gameplay;
using JumpNowBro.Util;
using NUnit.Framework;
using UnityEngine;

namespace JumpNowBro.Tests.PlayMode
{
    public class PreferencesTests
    {
        readonly Dictionary<string, (bool exists, int value)> saved = new Dictionary<string, (bool, int)>();
        static readonly string[] Keys = {
            "records.solo.level0.bestTimeMs", "records.solo.level0.fewestDeaths",
            "records.lan.level0.bestTimeMs", "records.lan.level0.fewestDeaths",
            "lifetime.playtimeSec", "lifetime.deaths", "lifetime.swaps", "display.resWidth", "display.resHeight"
        };

        [SetUp]
        public void SetUp()
        {
            saved.Clear();
            foreach (string key in Keys)
            {
                saved[key] = (PlayerPrefs.HasKey(key), PlayerPrefs.GetInt(key));
                PlayerPrefs.DeleteKey(key);
            }
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var entry in saved)
                if (entry.Value.exists) PlayerPrefs.SetInt(entry.Key, entry.Value.value);
                else PlayerPrefs.DeleteKey(entry.Key);
            PlayerPrefs.Save();
        }

        [Test]
        public void Records_UpdateBestsIndependently_AndKeepModesSeparate()
        {
            var first = GameRecords.ReportRun(GameRecords.Mode.Solo, 0, new LevelRunStats { timeMs = 1000, deaths = 5 });
            Assert.IsTrue(first.firstCompletion);
            Assert.AreEqual(-1, first.prevBestTimeMs);
            var fewer = GameRecords.ReportRun(GameRecords.Mode.Solo, 0, new LevelRunStats { timeMs = 1200, deaths = 3 });
            Assert.IsFalse(fewer.firstCompletion);
            Assert.IsFalse(fewer.newBestTime);
            Assert.IsTrue(fewer.newFewestDeaths);
            var faster = GameRecords.ReportRun(GameRecords.Mode.Solo, 0, new LevelRunStats { timeMs = 800, deaths = 7 });
            Assert.IsTrue(faster.newBestTime);
            Assert.IsFalse(faster.newFewestDeaths);
            Assert.AreEqual(1000, faster.prevBestTimeMs);
            Assert.IsTrue(GameRecords.TryGetBest(GameRecords.Mode.Solo, 0, out int time, out int deaths));
            Assert.AreEqual(800, time);
            Assert.AreEqual(3, deaths);
            Assert.IsFalse(GameRecords.IsCompleted(GameRecords.Mode.Lan, 0));
            GameRecords.ResetAll(1);
            Assert.IsFalse(GameRecords.IsCompleted(GameRecords.Mode.Solo, 0));
        }

        [Test]
        public void Records_RepairNegativeValues_AndAcceptMaximumTime()
        {
            PlayerPrefs.SetInt(Keys[0], -42);
            PlayerPrefs.SetInt(Keys[1], -7);
            Assert.IsFalse(GameRecords.IsCompleted(GameRecords.Mode.Solo, 0));
            var repaired = GameRecords.ReportRun(GameRecords.Mode.Solo, 0,
                new LevelRunStats { timeMs = int.MaxValue, deaths = 4 });
            Assert.IsTrue(repaired.firstCompletion);
            Assert.AreEqual(-1, repaired.prevBestTimeMs);
            Assert.IsTrue(GameRecords.IsCompleted(GameRecords.Mode.Solo, 0));
            var equal = GameRecords.ReportRun(GameRecords.Mode.Solo, 0,
                new LevelRunStats { timeMs = int.MaxValue, deaths = 4 });
            Assert.IsFalse(equal.firstCompletion);
            Assert.IsFalse(equal.newBestTime);
            Assert.AreEqual(int.MaxValue, equal.prevBestTimeMs);
        }

        [Test]
        public void Lifetime_RoundsMilliseconds_AndSaturatesWithoutWrapping()
        {
            GameRecords.AddLifetime(new LevelRunStats { timeMs = 499, deaths = 2, swaps = 3 });
            Assert.AreEqual(0, GameRecords.LifetimePlaytimeSec);
            GameRecords.AddLifetime(new LevelRunStats { timeMs = 500 });
            Assert.AreEqual(1, GameRecords.LifetimePlaytimeSec);
            GameRecords.AddLifetime(new LevelRunStats { timeMs = int.MaxValue });
            Assert.AreEqual(2147485, GameRecords.LifetimePlaytimeSec);
            PlayerPrefs.SetInt("lifetime.deaths", int.MaxValue - 1);
            GameRecords.AddLifetime(new LevelRunStats { deaths = 2 });
            Assert.AreEqual(int.MaxValue, GameRecords.LifetimeDeaths);
            Assert.AreEqual(3, GameRecords.LifetimeSwaps);
            GameRecords.ResetAll(1);
            Assert.AreEqual(0, GameRecords.LifetimePlaytimeSec);
            Assert.AreEqual(0, GameRecords.LifetimeDeaths);
            Assert.AreEqual(0, GameRecords.LifetimeSwaps);
        }

        [Test]
        public void SavedResolution_UsesOnlyModesSupportedByThisDevice()
        {
            PlayerPrefs.SetInt("display.resWidth", int.MaxValue);
            PlayerPrefs.SetInt("display.resHeight", -1);
            Assert.AreEqual(Screen.width, GameSettings.ResWidth);
            Assert.AreEqual(Screen.height, GameSettings.ResHeight);
            foreach (var resolution in Screen.resolutions)
            {
                PlayerPrefs.SetInt("display.resWidth", resolution.width);
                PlayerPrefs.SetInt("display.resHeight", resolution.height);
                Assert.AreEqual(resolution.width, GameSettings.ResWidth);
                Assert.AreEqual(resolution.height, GameSettings.ResHeight);
            }
        }
    }
}
