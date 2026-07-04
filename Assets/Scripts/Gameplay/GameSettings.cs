using UnityEngine;

namespace JumpNowBro.Gameplay
{
    /// PlayerPrefs-backed user preferences (#128): audio levels + display options, restored on launch.
    /// This stores *preferences only*, it deliberately does not persist level progress (DESIGN keeps levels
    /// fresh each session, no save files). One key per value with sane defaults that match AudioManager's
    /// serialized seeds, so a fresh install behaves identically to before any setting was touched.
    ///
    /// The Set* methods are the single funnel: each writes its pref AND applies the change live (audio through
    /// AudioManager's #133 mixer API, display straight to Screen/QualitySettings). Disk flush is deferred to
    /// Flush() (called when the settings panel closes) so dragging a slider doesn't hammer the disk every frame;
    /// PlayerPrefs also auto-flushes on quit.
    public static class GameSettings
    {
        const string KMaster     = "audio.master";
        const string KMusic      = "audio.music";
        const string KSFX        = "audio.sfx";
        const string KMuted      = "audio.muted";
        const string KResW       = "display.resWidth";
        const string KResH       = "display.resHeight";
        const string KFullscreen = "display.fullscreen";
        const string KVSync      = "display.vsync";
        const string KQuality    = "display.quality";
        const string KPalette    = "access.palette";

        // ---- getters (defaults match AudioManager's serialized seeds) ----
        public static float MasterVolume => PlayerPrefs.GetFloat(KMaster, 1f);
        public static float MusicVolume  => PlayerPrefs.GetFloat(KMusic, 0.5f);
        public static float SFXVolume    => PlayerPrefs.GetFloat(KSFX, 0.8f);
        public static bool  Muted        => PlayerPrefs.GetInt(KMuted, 0) != 0;
        public static bool  Fullscreen   => PlayerPrefs.GetInt(KFullscreen, 1) != 0;
        public static bool  VSync        => PlayerPrefs.GetInt(KVSync, 1) != 0;
        public static bool  HasAudioPrefs => PlayerPrefs.HasKey(KMaster);

        // ---- audio (live-applied through AudioManager's mixer API) ----
        public static void SetMasterVolume(float v) { v = Mathf.Clamp01(v); PlayerPrefs.SetFloat(KMaster, v); AudioManager.Instance?.SetMasterVolume(v); }
        public static void SetMusicVolume(float v)  { v = Mathf.Clamp01(v); PlayerPrefs.SetFloat(KMusic, v);  AudioManager.Instance?.SetMusicVolume(v); }
        public static void SetSFXVolume(float v)    { v = Mathf.Clamp01(v); PlayerPrefs.SetFloat(KSFX, v);    AudioManager.Instance?.SetSFXVolume(v); }
        public static void SetMuted(bool m)         { PlayerPrefs.SetInt(KMuted, m ? 1 : 0); AudioManager.Instance?.SetMuted(m); }

        /// Push the stored (or default) audio levels into the AudioManager. Called from AudioManager.Start so a
        /// saved profile is restored on launch; if nothing's saved the defaults above reproduce the old behaviour.
        public static void ApplyAudio(AudioManager am)
        {
            if (am == null) return;
            am.SetMasterVolume(MasterVolume);
            am.SetMusicVolume(MusicVolume);
            am.SetSFXVolume(SFXVolume);
            am.SetMuted(Muted);
        }

        // ---- display (live-applied to Screen / QualitySettings) ----
        public static void SetVSync(bool on)      { PlayerPrefs.SetInt(KVSync, on ? 1 : 0); QualitySettings.vSyncCount = on ? 1 : 0; }
        public static void SetQuality(int level)  { level = Mathf.Clamp(level, 0, Mathf.Max(0, QualitySettings.names.Length - 1)); PlayerPrefs.SetInt(KQuality, level); QualitySettings.SetQualityLevel(level, true); }

        public static void SetFullscreen(bool on)
        {
            PlayerPrefs.SetInt(KFullscreen, on ? 1 : 0);
            Screen.fullScreenMode = on ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed;
        }

        public static void SetResolution(int w, int h)
        {
            PlayerPrefs.SetInt(KResW, w);
            PlayerPrefs.SetInt(KResH, h);
            Screen.SetResolution(w, h, Screen.fullScreenMode);
        }

        public static int QualityLevel => PlayerPrefs.GetInt(KQuality, QualitySettings.GetQualityLevel());
        public static int ResWidth     => PlayerPrefs.GetInt(KResW, Screen.width);
        public static int ResHeight    => PlayerPrefs.GetInt(KResH, Screen.height);

        // ---- accessibility (#136) ----

        public static int PaletteMode => PlayerPrefs.GetInt(KPalette, 0);   // 0 default, 1 colourblind

        /// Persist + live-apply the palette through the two single colour sources. PlayerIdentity.SetPalette
        /// fires OnChanged (the HUD strip re-renders, which also re-reads ActionStyle); armed trigger banners
        /// are the one persistent surface outside that event, hence RetintAll. Transient cues (announcement,
        /// flash, vignette, ghosts) sample colour at fire time and need no refresh.
        public static void SetPaletteMode(int mode)
        {
            mode = mode != 0 ? 1 : 0;
            PlayerPrefs.SetInt(KPalette, mode);
            ActionStyle.SetPalette(mode == 1 ? ActionStyle.PaletteKind.Colourblind : ActionStyle.PaletteKind.Default);
            PlayerIdentity.SetPalette(mode == 1);
            SwapTrigger.RetintAll();
        }

        public static void Flush() => PlayerPrefs.Save();

        /// Restore display prefs before the first scene loads (audio is restored later by AudioManager.Start,
        /// once the manager exists). Resolution/fullscreen are no-ops in the Editor, they take effect in a build.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void ApplyDisplayOnLaunch()
        {
            // #136: restore the palette first — pure statics, safe pre-scene; no triggers exist yet to retint.
            if (PaletteMode != 0)
            {
                ActionStyle.SetPalette(ActionStyle.PaletteKind.Colourblind);
                PlayerIdentity.SetPalette(true);
            }

            QualitySettings.vSyncCount = VSync ? 1 : 0;
            if (PlayerPrefs.HasKey(KQuality))
                QualitySettings.SetQualityLevel(Mathf.Clamp(QualityLevel, 0, Mathf.Max(0, QualitySettings.names.Length - 1)), true);
            var mode = Fullscreen ? FullScreenMode.FullScreenWindow : FullScreenMode.Windowed;
            if (PlayerPrefs.HasKey(KResW))
                Screen.SetResolution(ResWidth, ResHeight, mode);
            else
                Screen.fullScreenMode = mode;
        }
    }
}
