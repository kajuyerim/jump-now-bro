using UnityEngine;
using UnityEngine.InputSystem;
using JumpNowBro.Gameplay;
using JumpNowBro.Util;

namespace JumpNowBro.Networking
{
    /// v2.5 non-verbal comms (#131): canned callouts on the top-row digits, later the synced GO countdown
    /// and the location ping. Owns the input polling (read directly off the devices, SettingsPanel-Esc
    /// style — comms never enter the sim input path or the rebind grid), the send cooldown, and the
    /// presentation calls. The wire is symmetric: either peer originates a comms EVENT and the receive
    /// direction identifies the sender, so the local echo renders immediately and no sender byte exists.
    ///
    /// Self-spawns like SwapScheduleDriver; persists across levels; all state is transient (ResetAll).
    public sealed class CommsController : MonoBehaviour
    {
        public static CommsController Instance { get; private set; }

        // Self-throttle: nothing else rate-limits, and the reliable queue silently drops past 64 in flight.
        const float SendCooldown = 0.45f;

        float nextKeySendTime;              // keys 1-4 share one bucket (a re-press inside it is spam)
        bool victoryLatch;                  // comms dead on the victory screen — see HandleAllLevelsComplete

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void AutoSpawn()
        {
            if (Instance == null)
                new GameObject(nameof(CommsController)).AddComponent<CommsController>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        void Start()
        {
            // Subscribe in Start so the gameplay singletons already exist (SwapScheduleDriver precedent).
            if (LevelManager.Instance != null)
            {
                LevelManager.Instance.OnBeforeLevelLoad += HandleBeforeLevelLoad;
                LevelManager.Instance.OnAllLevelsComplete += HandleAllLevelsComplete;
            }
        }

        void OnDestroy()
        {
            if (LevelManager.Instance != null)
            {
                LevelManager.Instance.OnBeforeLevelLoad -= HandleBeforeLevelLoad;
                LevelManager.Instance.OnAllLevelsComplete -= HandleAllLevelsComplete;
            }
            if (Instance == this) Instance = null;
        }

        void HandleBeforeLevelLoad(int _) => ResetAll();

        // The victory screen needs its own latch: the client's 0xFE path never raises OnBeforeLevelLoad and
        // parks CurrentLevelIndex on the last REAL level (the host parks at LevelCount), so the in-level
        // range check alone would only block the host. OnAllLevelsComplete fires on both roles.
        void HandleAllLevelsComplete()
        {
            ResetAll();
            victoryLatch = true;
        }

        /// Drop every piece of transient comms state. Called on level load, session teardown, rejoin and
        /// the connection-loss surface — a comm never outlives the moment it was about.
        public void ResetAll()
        {
            CalloutBubble.HideImmediate();
            victoryLatch = false;
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            if (kb.digit2Key.wasPressedThisFrame) TrySendCallout(CalloutId.Wait);
            if (kb.digit3Key.wasPressedThisFrame) TrySendCallout(CalloutId.Sorry);
            if (kb.digit4Key.wasPressedThisFrame) TrySendCallout(CalloutId.Nice);
        }

        // In play, unobstructed, with a character to anchor the bubble to. SimGated covers loading, the
        // level barrier, the connection-loss pause and the summary hold in one predicate.
        bool CanSendComms
        {
            get
            {
                var lm = LevelManager.Instance;
                if (lm == null || lm.CurrentLevelIndex < 0 || lm.CurrentLevelIndex >= lm.LevelCount) return false;
                if (lm.SimGated || victoryLatch) return false;
                if (SettingsPanel.Instance != null && SettingsPanel.Instance.IsOpen) return false;
                return PlayerSpawner.Instance != null && PlayerSpawner.Instance.CurrentPlayerInstance != null;
            }
        }

        // Receives take a lighter gate — deliberately NOT SummaryHold/SimPaused: a callout delivered during
        // the hold renders under the opaque card, which is fine. In-order delivery + the transport nulling
        // on disconnect already keep comms out of the lobby; this covers the victory and mid-join windows.
        bool CanReceiveComms
        {
            get
            {
                var lm = LevelManager.Instance;
                return lm != null && lm.CurrentLevelIndex >= 0 && lm.CurrentLevelIndex < lm.LevelCount
                       && !lm.IsLoading && !victoryLatch;
            }
        }

        // Host and solo drive P1; the client drives P2. The remote is always the flip — two peers only.
        InputOwner LocalOwner
        {
            get
            {
                var nm = NetworkManager.Instance;
                return nm != null && nm.Role == GameRole.Client ? InputOwner.P2 : InputOwner.P1;
            }
        }
        InputOwner RemoteOwner => LocalOwner == InputOwner.P1 ? InputOwner.P2 : InputOwner.P1;

        void TrySendCallout(CalloutId id)
        {
            if (!CanSendComms || Time.unscaledTime < nextKeySendTime) return;   // suppression is silent
            nextKeySendTime = Time.unscaledTime + SendCooldown;
            ShowCallout(LocalOwner, id);                                        // local echo, no round-trip
            NetworkManager.Instance?.SendCalloutEvent(id);                      // no-op solo (null transport)
        }

        public void ReceiveCallout(CalloutId id)
        {
            if (!CanReceiveComms) return;
            ShowCallout(RemoteOwner, id);
        }

        static void ShowCallout(InputOwner sender, CalloutId id)
        {
            string text = id switch
            {
                CalloutId.Wait  => "WAIT!",
                CalloutId.Sorry => "SORRY!",
                _               => "NICE!",
            };
            CalloutBubble.Show(text, PlayerIdentity.ColorOf(sender), 1.6f);
            AudioManager.Instance?.PlayCallout();
        }
    }
}
