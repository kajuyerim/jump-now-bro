using UnityEngine;
using UnityEngine.EventSystems;
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

        // SwapScheduleDriver's lead constants, duplicated: comms may not couple to the swap driver's privates.
        const int BaseLeadTicks = 9;        // ~0.15 s at 60 Hz
        const int LeadFloor     = 6;
        const int LeadCap       = 20;

        float nextKeySendTime;              // keys 1-4 share one bucket (a re-press inside it is spam)
        float nextPingSendTime;             // pings get their own bucket — a callout must not lock out an immediate "here"
        bool victoryLatch;                  // comms dead on the victory screen — see HandleAllLevelsComplete

        // One live marker per sender (P1 = slot 0), newest replaces. Held here because markers are
        // scene-less and must die on level load/teardown, not with any scene.
        readonly PingMarker[] livePings = new PingMarker[2];

        // Synced GO countdown (#152): a single pending goTick, newest-GO-wins (see CountdownBeats).
        bool countdownActive;
        uint countdownGoTick;
        InputOwner countdownSender;
        int lastShownBeat;

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
            // Fires on both roles (host via PlayerController, client via the STATE death-count delta) — a
            // GO scheduled before a death must never fire after the respawn.
            if (DeathNotifier.Instance != null) DeathNotifier.Instance.OnDeath += HandleDeath;
        }

        void OnDestroy()
        {
            if (LevelManager.Instance != null)
            {
                LevelManager.Instance.OnBeforeLevelLoad -= HandleBeforeLevelLoad;
                LevelManager.Instance.OnAllLevelsComplete -= HandleAllLevelsComplete;
            }
            if (DeathNotifier.Instance != null) DeathNotifier.Instance.OnDeath -= HandleDeath;
            if (Instance == this) Instance = null;
        }

        void HandleDeath(int _) => CancelCountdown();

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
            countdownActive = false;
            CalloutBubble.HideImmediate();
            for (int i = 0; i < livePings.Length; i++)
            {
                if (livePings[i] != null) Destroy(livePings[i].gameObject);
                livePings[i] = null;
            }
            victoryLatch = false;
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.digit1Key.wasPressedThisFrame) TrySendCountdown();
                if (kb.digit2Key.wasPressedThisFrame) TrySendCallout(CalloutId.Wait);
                if (kb.digit3Key.wasPressedThisFrame) TrySendCallout(CalloutId.Sorry);
                if (kb.digit4Key.wasPressedThisFrame) TrySendCallout(CalloutId.Nice);
            }
            var mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame) TrySendPing(mouse.position.ReadValue());
        }

        // The beat driver. Gated like SwapScheduleDriver's due-loop (null-tolerant, deliberately NOT
        // SimPaused — the loss surface clears comms via ResetAll instead), so a countdown never beats
        // through a level load or behind the summary card; IsStale mops up one that sat under a hold.
        void FixedUpdate()
        {
            if (!countdownActive) return;
            var lm = LevelManager.Instance;
            if (lm != null && (lm.IsLoading || lm.SummaryHold)) return;
            uint clock = Clock;
            if (CountdownBeats.IsStale(countdownGoTick, clock)) { countdownActive = false; return; }
            int b = CountdownBeats.CurrentBeat(countdownGoTick, clock);
            if (b >= lastShownBeat) return;
            lastShownBeat = b;
            var tint = PlayerIdentity.ColorOf(countdownSender);
            if (b > 0)
            {
                CalloutBubble.Show(b == 3 ? "3" : b == 2 ? "2" : "1", tint, 0.45f);
                AudioManager.Instance?.PlayCountBeat();
            }
            else
            {
                CalloutBubble.Show("GO!", tint, 0.8f, popScale: 1.25f);
                AudioManager.Instance?.PlayCountGo();
                countdownActive = false;
            }
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

        void TrySendCountdown()
        {
            if (!CanSendComms || Time.unscaledTime < nextKeySendTime) return;
            nextKeySendTime = Time.unscaledTime + SendCooldown;
            uint goTick = CountdownBeats.GoTick(Clock, (uint)Lead());
            AdoptCountdown(goTick, LocalOwner);
            // Always send, adopted locally or not: the receiver applies the same predicate, so both ends
            // converge either way, and the unconditional send is the deterministic choice.
            NetworkManager.Instance?.SendCountdownEvent(goTick);
        }

        public void ReceiveCountdown(uint goTick)
        {
            if (!CanReceiveComms) return;
            AdoptCountdown(goTick, RemoteOwner);
        }

        // Newest-GO-wins with the P1 tie-break. The incomingIsFromP1 flag is about the ORIGINATING sender,
        // so a local press on the host passes true and one on the client passes false — hardcoding the
        // receive-direction constants here would let an equal-tick race diverge the two screens' tints.
        void AdoptCountdown(uint goTick, InputOwner sender)
        {
            if (countdownActive && !CountdownBeats.ShouldReplace(countdownGoTick, goTick, sender == InputOwner.P1))
                return;
            countdownActive = true;
            countdownGoTick = goTick;
            countdownSender = sender;
            lastShownBeat = CountdownBeats.BeatCount + 1;   // the driver shows whatever beat is current next tick
        }

        void CancelCountdown()
        {
            if (!countdownActive) return;
            countdownActive = false;
            if (lastShownBeat <= CountdownBeats.BeatCount) CalloutBubble.HideImmediate();   // a beat is on screen
        }

        void TrySendPing(Vector2 screenPos)
        {
            if (!CanSendComms || Time.unscaledTime < nextPingSendTime) return;
            var cam = Camera.main;
            if (cam == null) return;
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;   // Leave bar / card buttons
            var w = cam.ScreenToWorldPoint(screenPos);
            w.z = 0f;   // ScreenToWorldPoint lands on the camera plane (z -10), inside the near clip — the echo would be invisible
            nextPingSendTime = Time.unscaledTime + SendCooldown;
            ShowPing(LocalOwner, new Vector2(w.x, w.y));
            NetworkManager.Instance?.SendWorldPingEvent(w.x, w.y);              // no-op solo (null transport)
        }

        public void ReceivePing(Vector2 pos)
        {
            if (!CanReceiveComms) return;
            ShowPing(RemoteOwner, pos);
        }

        void ShowPing(InputOwner sender, Vector2 pos)
        {
            int slot = sender == InputOwner.P1 ? 0 : 1;
            if (livePings[slot] != null) Destroy(livePings[slot].gameObject);   // one live ping per sender, newest replaces
            livePings[slot] = PingMarker.Spawn(pos, PlayerIdentity.ColorOf(sender));
            AudioManager.Instance?.PlayPing();
        }

        // The shared clock (SwapScheduleDriver.CurrentApplyClock, copied): the host measures goTick against
        // its last-consumed client tick, the client and solo against the local TickClock.
        uint Clock
        {
            get
            {
                var nm = NetworkManager.Instance;
                if (nm != null && nm.Role == GameRole.Hosting) return nm.HostConsumedClientTick;
                return TickClock.Instance != null ? TickClock.Instance.Current : 0u;
            }
        }

        // SwapScheduleDriver's lead formula applied on BOTH roles — either end originates a countdown, and
        // CurrentRtt is fed by PING/PONG on the client too. Solo reads 0 RTT and takes the base lead.
        int Lead()
        {
            int lead = BaseLeadTicks;
            var nm = NetworkManager.Instance;
            if (nm != null && nm.Role != GameRole.SinglePlayer)
                lead = Mathf.Max(lead, Mathf.CeilToInt(nm.CurrentRtt / Time.fixedDeltaTime) + 2);
            return Mathf.Clamp(lead, LeadFloor, LeadCap);
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
