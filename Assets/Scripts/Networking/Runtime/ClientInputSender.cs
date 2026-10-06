using UnityEngine;
using JumpNowBro.Gameplay;
using JumpNowBro.Util;

namespace JumpNowBro.Networking
{
    /// Client-side INPUT pump. Samples the local IInputSource each FixedUpdate at the TickClock's tick,
    /// stores it in a K=6 redundancy ring, and emits an INPUT body to the host on the unreliable channel.
    /// The redundancy is what protects an edge press against ~K-1 consecutive packet losses — no retransmit
    /// on the unreliable channel (DESIGN §8: INPUT/STATE are never sent reliably).
    ///
    /// Execution order −50 matches NetworkRemoteInputSource so any future client-side pre-tick consumers
    /// see the sampled frame before they run; PlayerController is destroyed on the client per #78, so the
    /// ordering chiefly matters relative to KeyboardInputSource's Tick() at end-of-frame.
    ///
    /// Wiring to a live transport + IInputSource is the #78 spawner's job; Bind() is the seam.
    [DefaultExecutionOrder(-50)]
    public sealed class ClientInputSender : GatedSimulationBehaviour
    {
        readonly InputSendRing ring = new InputSendRing();
        readonly byte[] sendBuffer = new byte[InputBody.HeaderSize + InputSendRing.K];

        IInputSource source;
        IReliableTransport transport;
        TickClock tickClock;

        /// The frame sampled this FixedUpdate, exposed so the v1.5 client predictor consumes the SAME frame the
        /// host will simulate — one sampler, no double-sample divergence. Valid only when
        /// LastSampledTick == TickClock.Current; a transport gap (early-return below) leaves these un-advanced.
        public PlayerInputFrame LastSampledFrame { get; private set; }
        public uint LastSampledTick { get; private set; }

        public void Bind(IInputSource source, IReliableTransport transport, TickClock tickClock)
        {
            this.source = source;
            this.transport = transport;
            this.tickClock = tickClock;
        }

        bool transportAlive = true;

        protected override SimulationGate Gate => SimulationGate.Gameplay;

        protected override void SimulationTick()
        {
            if (!transportAlive || source == null || transport == null || tickClock == null) return;

            uint tick = tickClock.Current;
            var f = new PlayerInputFrame
            {
                moveLeft    = source.MoveLeft,
                moveRight   = source.MoveRight,
                jumpPressed = source.JumpPressed,
                jumpHeld    = source.JumpHeld,
                dashPressed = source.DashPressed,
            };
            LastSampledFrame = f;                        // single-sampler seam: the predictor reads this, not a re-sample
            LastSampledTick  = tick;

            int n = ring.Sample(tick, f, sendBuffer);
            try
            {
                transport.Send(Channel.Unreliable, MessageType.Input, new System.ReadOnlySpan<byte>(sendBuffer, 0, n));
            }
            catch (System.ObjectDisposedException)
            {
                // Socket went away between EndSessionFromUi marking this GameObject for destroy and Unity
                // actually destroying it. Latch off so subsequent FixedUpdates in the same end-of-frame
                // window also short-circuit.
                transportAlive = false;
            }

            source.Tick();                              // clears local edge bits — mirrors KeyboardInputSource's pattern
        }
    }
}
