using UnityEngine;
using JumpNowBro.Gameplay;
using JumpNowBro.Util;

namespace JumpNowBro.Networking
{
    /// Host-side IInputSource fed by decoded client INPUT. The newest-unconsumed ring semantics live
    /// in `NetworkInputRing` (Util, engine-free, CI-tested); this class is the Unity glue: edge-bit
    /// clear on Tick() (mirrors KeyboardInputSource_*), and execution order −50 so FixedUpdate's
    /// frame pick lands BEFORE PlayerController (default order 0) reads the IInputSource.
    ///
    /// The execution-order choice replaces the plan's `(p2 as NetworkRemoteInputSource)?.Refresh(...)`
    /// cast pattern — that would require Gameplay (PlayerController) to reference Networking, breaking
    /// the asmdef direction. Ordering does the same work without the upstream coupling.
    ///
    /// Wiring to a transport / INPUT packet decode lands in the role-aware spawner (#78); v1.4 #74
    /// exposes `EnqueueFromInputBody` as the integration seam so tests and #78 can both feed frames.
    [DefaultExecutionOrder(-50)]
    public sealed class NetworkRemoteInputSource : GatedSimulationBehaviour, IInputSource
    {
        readonly NetworkInputRing ring = new NetworkInputRing();
        PlayerInputFrame current;
        PlayerController player;

        void Awake() => player = GetComponent<PlayerController>();

        public uint LastConsumedClientTick => ring.LastConsumedClientTick;

        public bool MoveLeft    => current.moveLeft;
        public bool MoveRight   => current.moveRight;
        public bool JumpPressed => current.jumpPressed;
        public bool JumpHeld    => current.jumpHeld;
        public bool DashPressed => current.dashPressed;

        /// Decode a v1.4 INPUT packet's redundancy window into the ring. Caller (#78 dispatcher) parses
        /// the InputBody header and hands the packed-frame slice in tick order.
        public void EnqueueFromInputBody(uint baseTick, System.ReadOnlySpan<byte> packedFrames)
        {
            for (int i = 0; i < packedFrames.Length; i++)
                EnqueueFrame(baseTick + (uint)i, PlayerInputFrame.Unpack(packedFrames[i]));
        }

        /// Direct enqueue for tests and (when needed) fine-grained replay paths.
        public void EnqueueFrame(uint clientTick, in PlayerInputFrame frame)
        {
            var accepted = frame;
            // Respawn can run after packet delivery but before the next FixedUpdate; filter on arrival too.
            if (player != null && player.IsDead) accepted.jumpPressed = accepted.dashPressed = false;
            ring.Enqueue(clientTick, accepted);
        }

        protected override SimulationGate Gate => SimulationGate.Gameplay;

        protected override void SimulationTick()
        {
            if (ring.TryConsumeNewest(out var picked, out _))
            {
                current = picked;                       // newest unconsumed: edges live, held bits live
            }
            else
            {
                // Starvation: held bits cache (moveLeft/moveRight/jumpHeld preserved on `current`);
                // edges never repeat — clear them so a stale `jumpPressed` doesn't refire.
                current.jumpPressed = false;
                current.dashPressed = false;
            }
            if (player != null && player.IsDead) Tick();
        }

        public void Tick()
        {
            // PlayerController calls this after reading the frame; edge bits clear so the same press
            // doesn't fire twice on the next FixedUpdate's starvation branch.
            current.jumpPressed = false;
            current.dashPressed = false;
        }
    }
}
