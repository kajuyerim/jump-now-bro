using System.Collections;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using JumpNowBro.Gameplay;

namespace JumpNowBro.Tests.PlayMode
{
    public class AudioSettingsTests
    {
        const string MasterKey = "audio.master";
        const string MusicKey = "audio.music";
        const string SfxKey = "audio.sfx";
        const string MutedKey = "audio.muted";

        GameObject managerObject;
        AudioManager manager;
        AudioManager originalInstance;
        FloatPrefSnapshot masterSnapshot;
        FloatPrefSnapshot musicSnapshot;
        FloatPrefSnapshot sfxSnapshot;
        IntPrefSnapshot mutedSnapshot;

        [SetUp]
        public void SetUp()
        {
            originalInstance = AudioManager.Instance;
            SetStaticInstance(null);
            masterSnapshot = SnapshotFloat(MasterKey);
            musicSnapshot = SnapshotFloat(MusicKey);
            sfxSnapshot = SnapshotFloat(SfxKey);
            mutedSnapshot = SnapshotInt(MutedKey);
            DeleteAudioPrefs();
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            try
            {
                if (managerObject != null) Object.Destroy(managerObject);
                yield return null;
            }
            finally
            {
                SetStaticInstance(originalInstance);
                RestoreFloat(MasterKey, masterSnapshot);
                RestoreFloat(MusicKey, musicSnapshot);
                RestoreFloat(SfxKey, sfxSnapshot);
                RestoreInt(MutedKey, mutedSnapshot);
                PlayerPrefs.Save();
            }
        }

        [UnityTest]
        public IEnumerator OnlyMusicPreferencePreservesOtherSerializedValues()
        {
            PlayerPrefs.SetFloat(MusicKey, 0.23f);
            manager = CreateManager(0.61f, 0.27f, 0.83f);
            yield return null;
            Assert.AreEqual(0.61f, manager.MasterVolume, 0.0001f);
            Assert.AreEqual(0.23f, manager.MusicVolume, 0.0001f);
            Assert.AreEqual(0.83f, manager.SFXVolume, 0.0001f);
            Assert.IsFalse(manager.Muted);
        }

        [UnityTest]
        public IEnumerator OnlySfxPreferencePreservesOtherSerializedValues()
        {
            PlayerPrefs.SetFloat(SfxKey, 0.37f);
            manager = CreateManager(0.61f, 0.27f, 0.83f);
            yield return null;
            Assert.AreEqual(0.61f, manager.MasterVolume, 0.0001f);
            Assert.AreEqual(0.27f, manager.MusicVolume, 0.0001f);
            Assert.AreEqual(0.37f, manager.SFXVolume, 0.0001f);
            Assert.IsFalse(manager.Muted);
        }

        [UnityTest]
        public IEnumerator OnlyMutePreferencePreservesOtherSerializedValues()
        {
            PlayerPrefs.SetInt(MutedKey, 1);
            manager = CreateManager(0.61f, 0.27f, 0.83f);
            yield return null;
            Assert.AreEqual(0.61f, manager.MasterVolume, 0.0001f);
            Assert.AreEqual(0.27f, manager.MusicVolume, 0.0001f);
            Assert.AreEqual(0.83f, manager.SFXVolume, 0.0001f);
            Assert.IsTrue(manager.Muted);
        }

        [UnityTest]
        public IEnumerator OnlyMasterPreferenceIsRestored()
        {
            PlayerPrefs.SetFloat(MasterKey, 0.42f);
            manager = CreateManager(0.61f, 0.27f, 0.83f);
            yield return null;
            Assert.AreEqual(0.42f, manager.MasterVolume, 0.0001f);
            Assert.AreEqual(0.27f, manager.MusicVolume, 0.0001f);
            Assert.AreEqual(0.83f, manager.SFXVolume, 0.0001f);
            Assert.IsFalse(manager.Muted);
        }

        [UnityTest]
        public IEnumerator AllAudioPreferencesAreRestored()
        {
            PlayerPrefs.SetFloat(MasterKey, 0.42f);
            PlayerPrefs.SetFloat(MusicKey, 0.23f);
            PlayerPrefs.SetFloat(SfxKey, 0.37f);
            PlayerPrefs.SetInt(MutedKey, 1);
            manager = CreateManager(0.61f, 0.27f, 0.83f);
            yield return null;
            Assert.AreEqual(0.42f, manager.MasterVolume, 0.0001f);
            Assert.AreEqual(0.23f, manager.MusicVolume, 0.0001f);
            Assert.AreEqual(0.37f, manager.SFXVolume, 0.0001f);
            Assert.IsTrue(manager.Muted);
        }

        [UnityTest]
        public IEnumerator NoAudioPreferencesKeepSerializedValues()
        {
            manager = CreateManager(0.61f, 0.27f, 0.83f);
            yield return null;
            Assert.AreEqual(0.61f, manager.MasterVolume, 0.0001f);
            Assert.AreEqual(0.27f, manager.MusicVolume, 0.0001f);
            Assert.AreEqual(0.83f, manager.SFXVolume, 0.0001f);
            Assert.IsFalse(manager.Muted);
        }

        AudioManager CreateManager(float master, float music, float sfx)
        {
            managerObject = new GameObject("AudioSettingsTestManager");
            var audioManager = managerObject.AddComponent<AudioManager>();
            Set(audioManager, "masterVolume", master);
            Set(audioManager, "musicVolume", music);
            Set(audioManager, "sfxVolume", sfx);
            return audioManager;
        }

        static void DeleteAudioPrefs()
        {
            PlayerPrefs.DeleteKey(MasterKey);
            PlayerPrefs.DeleteKey(MusicKey);
            PlayerPrefs.DeleteKey(SfxKey);
            PlayerPrefs.DeleteKey(MutedKey);
        }

        static FloatPrefSnapshot SnapshotFloat(string key) => new FloatPrefSnapshot
        {
            Exists = PlayerPrefs.HasKey(key),
            Value = PlayerPrefs.GetFloat(key)
        };

        static IntPrefSnapshot SnapshotInt(string key) => new IntPrefSnapshot
        {
            Exists = PlayerPrefs.HasKey(key),
            Value = PlayerPrefs.GetInt(key)
        };

        static void RestoreFloat(string key, FloatPrefSnapshot snapshot)
        {
            if (snapshot.Exists) PlayerPrefs.SetFloat(key, snapshot.Value);
            else PlayerPrefs.DeleteKey(key);
        }

        static void RestoreInt(string key, IntPrefSnapshot snapshot)
        {
            if (snapshot.Exists) PlayerPrefs.SetInt(key, snapshot.Value);
            else PlayerPrefs.DeleteKey(key);
        }

        static void SetStaticInstance(AudioManager value) =>
            typeof(AudioManager).GetField("<Instance>k__BackingField", BindingFlags.NonPublic | BindingFlags.Static)
                .SetValue(null, value);

        static void Set(object target, string field, object value) =>
            target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value);

        struct FloatPrefSnapshot
        {
            public bool Exists;
            public float Value;
        }

        struct IntPrefSnapshot
        {
            public bool Exists;
            public int Value;
        }
    }
}
