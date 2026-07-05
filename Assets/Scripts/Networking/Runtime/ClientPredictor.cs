using UnityEngine;
using JumpNowBro.Gameplay;
using JumpNowBro.Util;

namespace JumpNowBro.Networking
{
    /// Client-side predictor (#80). Each FixedUpdate it runs Movement.Step locally so the client's OWNED inputs
    /// feel instant instead of waiting a round-trip, and drives the (kinematic) Rigidbody2D from the predicted
    /// state. The pure step lives in Util/ClientPrediction; this is the Unity glue: frame timing, the shared
    /// collision world, and the rb-position cast-origin invariant.
    ///
    /// On a new STATE it reconciles: reseed to the authoritative snapshot and replay the buffered local inputs
    /// from lastConsumedClientTick+1..now (ClientPrediction.Reconcile), so owned-input prediction survives the
    /// correction; a hole or an over-cap window hard-snaps instead. Otherwise it steps one tick forward. #82 will
    /// smooth the post-reconcile residual onto a render child; predicted EdgeFlags are produced but juice is
    /// deferred to v1.6 #94.
    ///
    /// Execution order −40: after ClientInputSender (−50) so LastSampledFrame is fresh, after TickClock (−100).
    [DefaultExecutionOrder(-40)]
    public sealed class ClientPredictor : MonoBehaviour
    {
        ClientInputSender sender;
        ClientStateRenderer stateRenderer;
        TickClock tickClock;
        ControlMapStore mapStore;
        Rigidbody2D rb;
        ICollisionWorld world;
        PlayerTuning tuning;
        float fallLimitY;
        Transform visualChild;            // render-only child (#107); collider/rb stay at true sim pos
        Vector3 visualBaseLocal;          // the child's authored local position; offset is added on top

        readonly ClientHistory history = new ClientHistory();
        MovementState predicted;
        VisualSmoothing smoothing;
        bool seeded;
        bool wasDead;
        uint lastReseedSnapshot;
        int ticksSinceSnapshot;                     // #89: forward-prediction ticks since the last fresh STATE; clamps the run-ahead under stall
        System.Func<uint, ControlMap> mapAt;        // cached once in Bind so Reconcile's per-tick lookup never allocates

        public void Bind(ClientInputSender sender, ClientStateRenderer stateRenderer, TickClock tickClock,
                         ControlMapStore mapStore, Rigidbody2D rb, ICollisionWorld world,
                         PlayerTuning tuning, float fallLimitY, Transform visualChild)
        {
            this.sender = sender;
            this.stateRenderer = stateRenderer;
            this.tickClock = tickClock;
            this.mapStore = mapStore;
            this.rb = rb;
            this.world = world;
            this.tuning = tuning;
            this.fallLimitY = fallLimitY;
            this.visualChild = visualChild;
            visualBaseLocal = visualChild != null ? visualChild.localPosition : Vector3.zero;
            // Reconcile resolves the map per replayed tick from the swap scheduler, so a swap whose apply-tick
            // falls inside the replay window routes pre-boundary ticks on the old map. Cached once here — a method
            // group binds one delegate to the (stable) scheduler instance, so the replay loop never allocates.
            var driver = SwapScheduleDriver.Instance;
            mapAt = driver != null
                ? driver.Scheduler.MapAtTick
                : _ => this.mapStore != null ? this.mapStore.Current : ControlMap.Default;
        }

        void FixedUpdate()
        {
            if (sender == null || stateRenderer == null || tickClock == null || rb == null || world == null || tuning == null) return;
            if (LevelManager.Instance != null && LevelManager.Instance.SimGated) return;
            if (!stateRenderer.HasState) return;                       // nothing authoritative to predict from yet

            var authoritative = stateRenderer.CurrentState;

            // Host-authoritative death: hold at the authoritative pose and do NOT predict, so local input can't
            // walk the body through the hazard during the host's death freeze. Hard-snap (transform +
            // SyncTransforms, matching the host's respawn teleport) so the spike→checkpoint jump cuts cleanly
            // instead of streaking under Rigidbody2D interpolation.
            if (authoritative.isDead)
            {
                predicted = authoritative;
                seeded = true;
                wasDead = true;
                lastReseedSnapshot = stateRenderer.SnapshotTick;
                ticksSinceSnapshot = 0;                               // fresh authority resets the staleness clamp
                HardSnapTo(authoritative);                            // also clears the visual offset (instant cut)
                return;
            }

            // First live tick after a death: snap to the authoritative respawn pose, then resume prediction next tick.
            if (wasDead)
            {
                predicted = authoritative;
                wasDead = false;
                lastReseedSnapshot = stateRenderer.SnapshotTick;
                ticksSinceSnapshot = 0;                               // fresh authority resets the staleness clamp
                HardSnapTo(authoritative);
                return;
            }

            uint tick = tickClock.Current;
            if (sender.LastSampledTick != tick) return;               // R5: only this tick's sample; never record a stale frame
            var local = sender.LastSampledFrame;
            history.RecordInput(tick, local);                         // record BEFORE reconcile so the now-tick is replayable

            var map = mapStore != null ? mapStore.Current : ControlMap.Default;
            float dt = Mathf.Clamp(Time.fixedDeltaTime, 0.001f, 0.05f);   // defensive: never build tuning / step on a degenerate dt
            var t = tuning.AsMovementTuning(dt, fallLimitY);
            var host = stateRenderer.LastRemoteHostFrame;

            bool newSnapshot = !seeded || stateRenderer.SnapshotTick != lastReseedSnapshot;
            if (newSnapshot)
            {
                // Forward continuation from last tick's state WITHOUT the correction — what we'd have shown this
                // frame absent a new STATE. The gap between this and the reconciled result is the discontinuity to
                // smooth (and only that; normal motion produces a zero gap).
                SetBodyOrigin(predicted);
                var (forward, _) = ClientPrediction.PredictStep(predicted, map, local, host, t, dt, world);

                // Reconcile: reseed to authority and replay buffered local inputs up through this tick. SetBodyOrigin
                // re-establishes the cast origin (R2) before each replayed step so replay collision matches the host.
                var r = ClientPrediction.Reconcile(authoritative, stateRenderer.LastConsumedClientTick, tick,
                                                   history, mapAt, host, t, dt, world, SetBodyOrigin);
                predicted = r.State;
                seeded = true;
                lastReseedSnapshot = stateRenderer.SnapshotTick;
                ticksSinceSnapshot = 0;                               // fresh authority resets the staleness clamp

                if (r.HardSnapped) { HardSnapTo(predicted); return; }  // teleport (initial sync / post-stall / post-death hole)

                smoothing.Inject(forward.posX - predicted.posX, forward.posY - predicted.posY);
            }
            else
            {
                ticksSinceSnapshot++;
                if (ClientPrediction.ShouldHoldForStaleness(ticksSinceSnapshot, PredictionTuning.MaxForwardPredictTicks))
                {
                    // STATE has gone stale (heavy loss / stall): stop free-running forward so the body can't sprint
                    // off and hard-snap back. Hold the last predicted pose; reconcile cuts when a fresh STATE returns.
                    rb.MovePosition(new Vector2(predicted.posX, predicted.posY));
                    ApplyVisual();
                    smoothing.Decay();
                    return;
                }
                SetBodyOrigin(predicted);                              // R2 cast origin for the single forward step
                var (next, _) = ClientPrediction.PredictStep(predicted, map, local, host, t, dt, world);
                predicted = next;
                history.RecordPredicted(tick, next);
            }

            rb.MovePosition(new Vector2(predicted.posX, predicted.posY)); // kinematic mover; also fires trigger contacts
            ApplyVisual();                                            // render child at simPos + offset
            smoothing.Decay();                                       // ease the offset toward zero for next tick
        }

        // Render-child position = its authored local + the (decaying) smoothing offset. Collider/rb are untouched
        // — only the sprite eases the correction, so collision + triggers stay on the true sim position.
        void ApplyVisual()
        {
            if (visualChild == null) return;
            visualChild.localPosition = visualBaseLocal + new Vector3(smoothing.offsetX, smoothing.offsetY, 0f);
        }

        // Re-establish the cast origin so Movement.Step's sweep (rb.Cast) originates at `s` — the host's per-tick
        // invariant, reproduced on the client so prediction + replay collision is bit-for-bit comparable (R2).
        void SetBodyOrigin(MovementState s)
        {
            rb.position = new Vector2(s.posX, s.posY);
            Physics2D.SyncTransforms();
        }

        // Hard teleport for host-authoritative jumps (death hold, respawn, over-cap/hole reconcile). transform +
        // SyncTransforms (not MovePosition) so the kinematic body cuts instantly without interpolation streak —
        // the v1.3 respawn-teleport pattern, mirrored on the client.
        void HardSnapTo(in MovementState s)
        {
            transform.position = new Vector3(s.posX, s.posY, transform.position.z);
            Physics2D.SyncTransforms();
            smoothing.Clear();                                        // instant cut: no residual render offset across a teleport
            ApplyVisual();
        }
    }
}
