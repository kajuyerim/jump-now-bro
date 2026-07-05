using System;
using UnityEngine;
using JumpNowBro.Gameplay;
using JumpNowBro.Util;

namespace JumpNowBro.Networking
{
    /// Host-side STATE pump. Self-subscribes (in Awake) to PlayerController.OnSimStepCompleted so the
    /// broadcaster captures the authoritative state AT the moment of FixedUpdate completion — no race
    /// against a "Bind() landed after the first sim step" hazard (#76 Finding #9). On every other host
    /// tick (~30 Hz at 60 Hz sim, per DESIGN §8) it builds a StateBody and sends it on the unreliable
    /// channel, suppressed during async LevelManager.LoadNext to avoid LEVEL_LOAD/STATE wire races.
    ///
    /// Runs only when the host is actually hosting (defense in depth — #78 spawner only attaches this
    /// component on Hosting role, but the role check stays for robustness).
    [DefaultExecutionOrder(50)]
    public sealed class NetworkStateBroadcaster : MonoBehaviour
    {
        PlayerController controller;
        ControlMapStore store;
        LevelManager levelManager;
        Func<uint> lastConsumedClientTickGetter;
        byte[] sendBuffer;
        bool transportAlive = true;

        void Awake()
        {
            controller = GetComponent<PlayerController>();
            if (controller != null) controller.OnSimStepCompleted += Handle;
        }

        void OnDestroy()
        {
            if (controller != null) controller.OnSimStepCompleted -= Handle;
        }

        /// Called by the role-aware spawner (#78). Note: the transport is NOT cached — it's read dynamically
        /// from NetworkManager.Instance.CurrentTransport on every Handle, so a client-rejoin (which creates
        /// a new transport via LatchPeer without re-spawning the host's Player) automatically picks up the
        /// new endpoint. A cached reference would keep sending to the disconnected previous client.
        public void Bind(ControlMapStore store, LevelManager levelManager,
                         Func<uint> lastConsumedClientTickGetter)
        {
            this.store = store;
            this.levelManager = levelManager;
            this.lastConsumedClientTickGetter = lastConsumedClientTickGetter;
            sendBuffer = new byte[StateBody.Size];
        }

        void Handle(uint hostTick, MovementState state)
        {
            if (!transportAlive) return;
            var nm = NetworkManager.Instance;
            if (nm == null) return;
            if (nm.Role != GameRole.Hosting) return;                                 // defense in depth: only host broadcasts
            var transport = nm.CurrentTransport;                                     // read fresh: client rejoin swaps transport without re-Binding the broadcaster
            if (transport == null) return;
            // Withhold STATE during the async load, the armed load-barrier (waiting for the client's
            // LEVEL_READY), and the summary hold — the host must not broadcast a pose the client can't (or
            // shouldn't) render. Defense in depth: the PlayerController gate already silences
            // OnSimStepCompleted for all three, so this rarely even runs while gated.
            bool gated = levelManager != null && levelManager.SimGated;
            if (!StateBroadcastTiming.ShouldBroadcast(hostTick, gated)) return;

            var body = new StateBody
            {
                snapshotTick           = hostTick,
                lastConsumedClientTick = lastConsumedClientTickGetter != null ? lastConsumedClientTickGetter() : 0u,
                // Cumulative across level transitions — client mirrors this via DeathNotifier.Raise so its
                // HUD lines up with the host's TotalDeaths instead of a per-level controller.DeathCount.
                deathCount             = (ushort)(PlayerSpawner.Instance != null ? PlayerSpawner.Instance.TotalDeaths : 0),
                // Post-victory the index sits at LevelCount (out of range): map it to the 0xFE sentinel like
                // the WELCOME provider does, not a raw invalid index. No consumer reads STATE.sceneIndex
                // today, kept consistent so a future reader can't inherit the (#143) out-of-range bug.
                sceneIndex             = (byte)(levelManager == null || levelManager.CurrentLevelIndex < 0 ? 0xFF
                                                : levelManager.CurrentLevelIndex >= levelManager.LevelCount ? LevelManager.AllLevelsCompleteSentinel
                                                : levelManager.CurrentLevelIndex),
                controlMap             = store != null ? store.Current : ControlMap.Default,
                remoteInputFrame       = controller != null ? controller.LastHostInputFrame : default,
                movementState          = state,
            };
            int n = body.Write(sendBuffer);
            try
            {
                transport.Send(Channel.Unreliable, MessageType.State, new ReadOnlySpan<byte>(sendBuffer, 0, n));
            }
            catch (System.ObjectDisposedException)
            {
                // End-of-frame teardown race after EndSessionFromUi — see ClientInputSender for context.
                transportAlive = false;
            }
        }
    }
}
