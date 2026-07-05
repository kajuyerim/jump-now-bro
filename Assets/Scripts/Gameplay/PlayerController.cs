using System.Collections;
using UnityEngine;
using JumpNowBro.Util;

namespace JumpNowBro.Gameplay
{
    [RequireComponent(typeof(Rigidbody2D))]
    [RequireComponent(typeof(PlayerCollisionConfig))]
    public class PlayerController : MonoBehaviour
    {
        [SerializeField] PlayerTuning tuning;
        [SerializeField] float fallLimitY = -20f;

        const float RespawnDelay = 0.4f;

        Rigidbody2D rb;
        ICollisionWorld collisionWorld;
        IInputSource p1;
        IInputSource p2;
        MovementState currentState;                                                 // all per-tick state (MoveState, timers, facing, dash charge, wasJumpHeld)
        Vector2 checkpointPosition;
        ControlMap checkpointControlMap = ControlMap.Default;
        bool isDead;

        public bool IsInvulnerable => currentState.invulnTimer > 0f;
        public bool IsDead => isDead;
        public Vector2 CheckpointPosition => checkpointPosition;
        /// The ControlMap restored on respawn — the host sends this in the DEATH EVENT so the client resets
        /// ownership to the same checkpoint map (read at OnDeath, before the respawn delay applies it).
        public ControlMap CheckpointMap => checkpointControlMap;
        public int DeathCount { get; private set; }

        public event System.Action<int> OnDeath;
        public event System.Action OnDash;
        public event System.Action OnJump;   // takeoff edge — drives jump juice (#42 SFX, #118 squash)
        public event System.Action OnLand;   // touchdown edge — drives the land thud (#42)
        /// Fired at end of FixedUpdate (after MovePosition) with (hostTick, post-step state) so the v1.4
        /// NetworkStateBroadcaster can sample at the exact moment authoritative state is settled.
        public event System.Action<uint, MovementState> OnSimStepCompleted;

        /// The host's local-keyboard PlayerInputFrame this tick — carried in STATE so the v1.5 client
        /// predictor can dead-reckon host-owned actions between snapshots.
        public PlayerInputFrame LastHostInputFrame { get; private set; }

        /// Read by NetworkManager.WireClient BEFORE it destroys this controller on the client, so the client
        /// predictor can build the same MovementTuning the host steps with (these serialized fields vanish
        /// with the destroyed component otherwise).
        public PlayerTuning Tuning => tuning;
        public float FallLimitY => fallLimitY;

        public void ResetDeathCount() => DeathCount = 0;

        public void SetCheckpoint(Vector2 pos, ControlMap map)
        {
            checkpointPosition = pos;
            checkpointControlMap = map;
        }

        public void Die()
        {
            if (isDead) return;
            StartCoroutine(DieRoutine());
        }

        IEnumerator DieRoutine()
        {
            isDead = true;
            DeathCount++;
            OnDeath?.Invoke(DeathCount);                                            // fires immediately so juice (camera shake, death-count UI) lands before respawn
            yield return new WaitForSeconds(RespawnDelay);
            transform.position = checkpointPosition;                                // Kinematic teleport: transform.position + explicit SyncTransforms() so the
            Physics2D.SyncTransforms();                                             // next FixedUpdate's cast queries see the new pose (m_AutoSyncTransforms=0).
            currentState = FreshSpawnState();
            if (ControlMapStore.Instance != null)
                ControlMapStore.Instance.Apply(checkpointControlMap);
            SwapTrigger.RestoreCheckpointStates();
            isDead = false;
        }

        public void Inject(IInputSource player1, IInputSource player2)
        {
            p1 = player1;
            p2 = player2;
        }

        void Awake()
        {
            rb = GetComponent<Rigidbody2D>();
            currentState = FreshSpawnState();
            var collisionConfig = GetComponent<PlayerCollisionConfig>();
            if (collisionConfig == null)
            {
                Debug.LogError("PlayerController requires a PlayerCollisionConfig component on the Player prefab.", this);
                enabled = false;
                return;
            }
            collisionWorld = collisionConfig.CreateWorld(rb);
        }

        void FixedUpdate()
        {
            // Freeze the sim across a level transition (IsLoading: the body would free-fall once the old scene's
            // ground unloads and rack up phantom fall deaths), the host's LEVEL_READY barrier (SimPaused), and
            // the end-of-level summary hold (SummaryHold, #130). Mirrors the same SimGated gate in
            // ClientPredictor / NetworkStateBroadcaster.
            if (LevelManager.Instance != null && LevelManager.Instance.SimGated) return;
            if (p1 == null || p2 == null || tuning == null) return;
            if (isDead)
            {
                // Keep the wire warm during the 0.4s death freeze: broadcast the frozen pose with isDead set so
                // the v1.5 client predictor HOLDS at the death position (and snaps cleanly on respawn) instead of
                // free-running its local prediction through the hazard. Without this the host goes silent during
                // death and the client's predictor walks the body through the spike until the respawn STATE lands.
                // v1.6's reliable DEATH EVENT (apply_at_tick) replaces this STATE-bridged signal.
                currentState.posX = rb.position.x;
                currentState.posY = rb.position.y;
                currentState.isDead = true;
                uint deadTick = TickClock.Instance != null ? TickClock.Instance.Current : 0u;
                OnSimStepCompleted?.Invoke(deadTick, currentState);
                return;
            }

            float dt = Time.fixedDeltaTime;

            currentState.posX = rb.position.x;                                       // read live pos (DieRoutine teleport may have moved it); velocity is
            currentState.posY = rb.position.y;                                       // owned by Movement.Step — no rb.linearVelocity read at #70.

            var f1 = ReadFrame(p1);
            var f2 = ReadFrame(p2);
            LastHostInputFrame = f1;                                                 // host=P1 convention; broadcaster reads this for STATE.remoteInputFrame
            var map = ControlMapStore.Instance != null ? ControlMapStore.Instance.Current : ControlMap.Default;
            var input = ControlMap.Route(map, f1, f2);

            var movementTuning = tuning.AsMovementTuning(dt, fallLimitY);            // rebuilt every tick so Inspector live-tune still works

            // sweep:true at #70 — body is Kinematic; Movement.Step's swept position is authoritative.
            var (newState, edges) = Movement.Step(currentState, input, movementTuning, dt, collisionWorld);

            if ((edges & EdgeFlags.DiedThisTick) != 0)                               // fall-limit → Die() routes through the existing 0.4s respawn coroutine
            {
                Die();
                return;
            }
            if ((edges & EdgeFlags.DashedThisTick) != 0) OnDash?.Invoke();
            if ((edges & EdgeFlags.JumpedThisTick) != 0) OnJump?.Invoke();
            if ((edges & EdgeFlags.LandedThisTick) != 0) OnLand?.Invoke();

            currentState = newState;
            rb.MovePosition(new Vector2(newState.posX, newState.posY));              // Kinematic body — MovePosition is the only mover. The earlier "also set
                                                                                     // linearVelocity for trigger detection" pattern conflicts with MovePosition's
                                                                                     // internal velocity computation and stalls the body. UFKC=1 + MovePosition +
                                                                                     // moving Kinematic body is sufficient for OnTriggerEnter2D vs static triggers.
                                                                                     // Hard-snap (DieRoutine, v1.5 CSP correction) uses transform.position + SyncTransforms.

            p1.Tick();
            p2.Tick();

            // Broadcaster subscribes in Awake (Finding #9); event fires AFTER MovePosition so the state
            // sample reflects the authoritative post-step pose.
            uint hostTick = TickClock.Instance != null ? TickClock.Instance.Current : 0u;
            OnSimStepCompleted?.Invoke(hostTick, newState);
        }

        static PlayerInputFrame ReadFrame(IInputSource s) =>
            new PlayerInputFrame
            {
                moveLeft    = s.MoveLeft,
                moveRight   = s.MoveRight,
                jumpPressed = s.JumpPressed,
                jumpHeld    = s.JumpHeld,
                dashPressed = s.DashPressed,
            };

        static MovementState FreshSpawnState() =>
            new MovementState
            {
                state               = MoveState.Falling,
                facing              = 1,
                dashChargeAvailable = true,
            };
    }
}
