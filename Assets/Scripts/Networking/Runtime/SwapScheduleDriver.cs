using UnityEngine;
using JumpNowBro.Gameplay;
using JumpNowBro.Util;

namespace JumpNowBro.Networking
{
    /// Drives the PendingSwapScheduler each FixedUpdate so a control swap flips ControlMapStore on the same
    /// client-input-tick on both ends. apply_at_tick is a client input-tick: the host reaches it via its
    /// LastConsumedClientTick, the client (and solo) via TickClock. See DESIGN §8 + the v1.6 plan.
    ///
    /// Self-spawns before the first scene loads, so no Bootstrap wiring is needed; persists across levels.
    /// Execution order −45: after NetworkRemoteInputSource (−50) so the host's consumed-tick is fresh this
    /// FixedUpdate, before PlayerController (0) and ClientPredictor (−40) so the flipped map is the one they
    /// route input through this tick.
    [DefaultExecutionOrder(-45)]
    public sealed class SwapScheduleDriver : MonoBehaviour
    {
        public static SwapScheduleDriver Instance { get; private set; }

        // Telegraph + network slack before a swap applies, in client ticks. Floored for a readable telegraph,
        // raised toward the RTT when hosting, capped. v1.7 re-tunes under lag-sim.
        const int BaseLeadTicks = 9;     // ~0.15 s at 60 Hz
        const int LeadFloor     = 6;     // ~0.1 s
        const int LeadCap       = 20;

        public PendingSwapScheduler Scheduler { get; } = new PendingSwapScheduler();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void AutoSpawn()
        {
            if (Instance == null)
                new GameObject(nameof(SwapScheduleDriver)).AddComponent<SwapScheduleDriver>();
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        void Start()
        {
            // Subscribe in Start so the gameplay singletons (DeathNotifier, LevelManager) already exist.
            SwapTrigger.OnSwapRequested += HandleSwapRequested;
            if (DeathNotifier.Instance != null) DeathNotifier.Instance.OnDeath += HandleDeath;
            if (LevelManager.Instance != null)  LevelManager.Instance.OnBeforeLevelLoad += HandleBeforeLevelLoad;
        }

        void OnDestroy()
        {
            SwapTrigger.OnSwapRequested -= HandleSwapRequested;   // static event: unsubscribe or it leaks the handler
            if (DeathNotifier.Instance != null) DeathNotifier.Instance.OnDeath -= HandleDeath;
            if (LevelManager.Instance != null)  LevelManager.Instance.OnBeforeLevelLoad -= HandleBeforeLevelLoad;
            if (Instance == this) Instance = null;
        }

        void FixedUpdate()
        {
            // Don't flip into a half-loaded scene (triggers mid-destroy; HandleBeforeLevelLoad already reset
            // us), and don't flip during the end-of-level summary hold (#130) — the clocks keep advancing
            // under the hold, so a swap scheduled just before the goal would otherwise apply and sting behind
            // the card. Deliberately NOT SimPaused: swaps applying during the LEVEL_READY barrier is existing
            // behavior this must not silently change. Held-pending swaps die at the next load's ResetTo.
            if (LevelManager.Instance != null
                && (LevelManager.Instance.IsLoading || LevelManager.Instance.SummaryHold)) return;

            var due = Scheduler.OnTick(CurrentApplyClock);
            for (int i = 0; i < due.Count; i++)
            {
                // Diff old->new for the announcement (#115) + flash (#126); keep grey/sting unconditional.
                var store = ControlMapStore.Instance;
                if (store != null)
                {
                    var old = store.Current;
                    store.Apply(due[i].Map);
                    RunSummaryController.Instance?.NotifySwapApplied();   // #130 stat: the shared due-apply moment, identical on both ends
                    var ch = SwapDiff.FirstChange(old, due[i].Map);
                    if (ch != null) RaiseSwapCues(ch.Value.action, ch.Value.newOwner);
                }
                SwapTrigger.GreyById(due[i].TriggerId);
                AudioManager.Instance?.PlaySwap();           // sting on both ends, at the shared apply tick (#42)
            }
        }

        // Swap cues fired at the shared apply tick on whichever end is running this. Announcement (#115) is the
        // same on both screens; the edge flash (#126) is local and only on a GAIN: the player this screen drives
        // just received the action. Losing it shows nothing (a dim pulse there read as confusing). Solo has no
        // single local player, so every swap is a gain for the new owner.
        void RaiseSwapCues(PlayerAction action, InputOwner newOwner)
        {
            var overlay = GameHudOverlay.Instance;
            if (overlay == null) return;
            overlay.Announce(action, newOwner);

            var role = NetworkManager.Instance != null ? NetworkManager.Instance.Role : GameRole.SinglePlayer;
            if (role == GameRole.SinglePlayer || newOwner == (role == GameRole.Hosting ? InputOwner.P1 : InputOwner.P2))
                overlay.Flash(action);   // solo: always; networked: only when this screen's player gains it
        }

        // Host/solo only (the client gates out in SwapTrigger). Compose onto the pending-final map so two
        // in-flight swaps don't clobber, schedule locally, and (when hosting) send the reliable SWAP EVENT.
        void HandleSwapRequested(SwapRequest req)
        {
            var store = ControlMapStore.Instance;
            if (store == null) return;
            uint applyTick = CurrentApplyClock + (uint)Lead();
            var map = ControlMap.WithSwap(Scheduler.PendingFinalMap(store.Current), req.action);
            Scheduler.Schedule(applyTick, map, req.triggerId);
            NetworkManager.Instance?.SendSwapEvent(applyTick, map, req.triggerId);   // no-op unless hosting
        }

        // Cancel pending swaps on death so one scheduled before the death can't fire after respawn. Host/solo
        // only: the client cancels via the reliable, in-order DEATH EVENT (delivered after the swaps it must
        // cancel), never off the unreliable STATE-delta that drives DeathNotifier — that would race the swaps.
        // The base map is irrelevant here (host/solo don't reconcile via MapAtTick); respawn re-applies it.
        void HandleDeath(int _)
        {
            if (!Authority.IsHost) return;
            Scheduler.ResetTo(ControlMapStore.Instance != null ? ControlMapStore.Instance.Current : ControlMap.Default);
        }

        // A new level starts at Default (matches LevelManager's own map reset); drop any cross-level pending swap.
        void HandleBeforeLevelLoad(int _) => Scheduler.ResetTo(ControlMap.Default);

        // The clock apply_at_tick is measured against: the host's last-consumed client tick, else local TickClock.
        uint CurrentApplyClock
        {
            get
            {
                var nm = NetworkManager.Instance;
                if (nm != null && nm.Role == GameRole.Hosting) return nm.HostConsumedClientTick;
                return TickClock.Instance != null ? TickClock.Instance.Current : 0u;
            }
        }

        int Lead()
        {
            int lead = BaseLeadTicks;
            var nm = NetworkManager.Instance;
            if (nm != null && nm.Role == GameRole.Hosting)
                lead = Mathf.Max(lead, Mathf.CeilToInt(nm.CurrentRtt / Time.fixedDeltaTime) + 2);
            return Mathf.Clamp(lead, LeadFloor, LeadCap);
        }
    }
}
