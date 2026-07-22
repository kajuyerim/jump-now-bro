using UnityEngine;
using UnityEngine.Audio;

namespace JumpNowBro.Gameplay
{
    /// The single audio routing point, on Bootstrap's persistent Manager (#42). Side-effect components call
    /// PlayJump / PlayLand / PlayDash / PlaySwap; nothing inside the simulation touches audio. Clips are assigned
    /// in the Inspector so they stay trivially swappable.
    ///
    /// Volume is bus-controlled through an AudioMixer (#133): the music source feeds the Music group, the SFX
    /// source feeds the SFX group, and Master/Music/SFX exposed params carry the levels the settings panel (#128)
    /// drives. Sliders are 0..1 and mapped to dB logarithmically (perceived loudness is logarithmic, so a linear
    /// slider would feel dead until the bottom of its travel). When no mixer is assigned (CI, or before the asset
    /// is wired in the Inspector) it falls back to per-source AudioSource.volume so the game still plays.
    public sealed class AudioManager : MonoBehaviour
    {
        public static AudioManager Instance { get; private set; }

        // Exposed-parameter names on the AudioMixer asset. These MUST match the names you expose in the mixer
        // (right-click the group's Volume field, Expose, then rename here in the Audio Mixer's Exposed Parameters list).
        const string MasterParam = "MasterVolume";
        const string MusicParam  = "MusicVolume";
        const string SFXParam    = "SFXVolume";

        [Header("Mixer routing (assign the GameMixer asset + its groups)")]
        [SerializeField] AudioMixer mixer;
        [SerializeField] AudioMixerGroup musicGroup;
        [SerializeField] AudioMixerGroup sfxGroup;

        [Header("SFX clips (assign in the Inspector)")]
        [SerializeField] AudioClip jumpClip;
        [SerializeField] AudioClip landClip;
        [SerializeField] AudioClip dashClip;
        [SerializeField] AudioClip swapClip;
        [SerializeField] AudioClip calloutClip;      // v2.5 comms (#151): WAIT/SORRY/NICE pop
        [SerializeField] AudioClip pingClip;         // v2.5 comms: location ping
        [SerializeField] AudioClip countBeatClip;    // v2.5 comms: 3/2/1 beat
        [SerializeField] AudioClip countGoClip;      // v2.5 comms: the GO beat

        [Header("Music (assign in the Inspector)")]
        [SerializeField] AudioClip musicClip;

        [Header("Default levels (0..1; seed the mixer at startup, overridden by #128's saved settings)")]
        [SerializeField, Range(0f, 1f)] float masterVolume = 1f;
        [SerializeField, Range(0f, 1f)] float musicVolume = 0.5f;
        [SerializeField, Range(0f, 1f)] float sfxVolume = 0.8f;

        AudioSource sfx;
        AudioSource music;
        bool muted;
        bool HasMixer => mixer != null;

        public float MasterVolume => masterVolume;
        public float MusicVolume => musicVolume;
        public float SFXVolume => sfxVolume;
        public bool Muted => muted;

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            sfx = gameObject.AddComponent<AudioSource>();
            sfx.playOnAwake = false;
            sfx.spatialBlend = 0f;   // 2D: SFX are feedback, not positional
            sfx.outputAudioMixerGroup = sfxGroup;

            // Looping background music (#116). Persists across level loads via DontDestroyOnLoad, so the loop
            // is continuous through transitions; AudioSource.loop avoids a re-trigger gap (the only seam risk
            // is the clip's own encoder padding, provide a loop-trimmed clip if a gap shows).
            music = gameObject.AddComponent<AudioSource>();
            music.playOnAwake = false;
            music.loop = true;
            music.spatialBlend = 0f;
            music.outputAudioMixerGroup = musicGroup;

            // Mixer-routed: sources run at unity and the mixer attenuates. No mixer (CI / unwired): the source
            // volumes carry the level directly, matching the pre-#133 behavior.
            music.volume = HasMixer ? 1f : musicVolume;

            if (musicClip != null) { music.clip = musicClip; music.Play(); }
        }

        // Apply audio levels in Start, not Awake: AudioMixer.SetFloat on exposed params can be silently
        // overwritten by the startup snapshot transition on frame 1 if set in Awake. Saved settings (#128) win;
        // with nothing saved, the serialized defaults reproduce the pre-settings behaviour.
        void Start()
        {
            if (GameSettings.HasAudioPrefs)
            {
                GameSettings.ApplyAudio(this);
            }
            else
            {
                ApplyMasterToMixer();
                ApplyMusicToMixer();
                ApplySFXToMixer();
            }
        }

        // ---- volume API (#128's settings panel drives these; 0..1) ----

        public void SetMasterVolume(float v01) { masterVolume = Mathf.Clamp01(v01); ApplyMasterToMixer(); }
        public void SetMusicVolume(float v01)  { musicVolume  = Mathf.Clamp01(v01); ApplyMusicToMixer(); }
        public void SetSFXVolume(float v01)     { sfxVolume    = Mathf.Clamp01(v01); ApplySFXToMixer(); }

        public void SetMuted(bool value) { muted = value; ApplyMasterToMixer(); if (!HasMixer) ApplyMusicToMixer(); }
        public void ToggleMuted() => SetMuted(!muted);

        void ApplyMasterToMixer()
        {
            if (HasMixer) mixer.SetFloat(MasterParam, LinearToDb(muted ? 0f : masterVolume));
            // No mixer: master mute folds into the per-source paths (music below, SFX at Play()).
        }

        void ApplyMusicToMixer()
        {
            if (HasMixer) mixer.SetFloat(MusicParam, LinearToDb(musicVolume));
            else if (music != null) music.volume = muted ? 0f : musicVolume;
        }

        void ApplySFXToMixer()
        {
            if (HasMixer) mixer.SetFloat(SFXParam, LinearToDb(sfxVolume));
            // No mixer: SFX level is applied per-shot in Play().
        }

        // 0..1 -> dB. 1 = 0 dB (unity gain), 0 = -80 dB (the mixer's silence floor). Clamped so log10(0) never -inf's.
        static float LinearToDb(float v) => v <= 0.0001f ? -80f : Mathf.Log10(v) * 20f;

        public void PlayJump() => Play(jumpClip);
        public void PlayLand() => Play(landClip);
        public void PlayDash() => Play(dashClip);
        public void PlaySwap() => Play(swapClip);
        public void PlayCallout()   => Play(calloutClip);     // v2.5 comms (#151); Play() no-ops until assigned
        public void PlayPing()      => Play(pingClip);
        public void PlayCountBeat() => Play(countBeatClip);
        public void PlayCountGo()   => Play(countGoClip);

        // PlayOneShot so overlapping triggers (e.g. a dash landing into a swap) layer instead of cutting each other.
        // Mixer-routed: play at unity and let the SFX bus attenuate. No mixer: scale by sfxVolume (and master mute) here.
        void Play(AudioClip clip)
        {
            if (clip == null || sfx == null) return;
            float vol = HasMixer ? 1f : (muted ? 0f : sfxVolume);
            sfx.PlayOneShot(clip, vol);
        }
    }
}
