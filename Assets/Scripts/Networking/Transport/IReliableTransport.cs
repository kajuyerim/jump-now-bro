using System;

namespace JumpNowBro.Networking
{
    public enum Channel { Unreliable, Reliable }

    /// Wire message types; channel discipline and send rates are in DESIGN §8.
    public enum MessageType : byte
    {
        Hello = 0, Welcome = 1, Goodbye = 2,
        Input = 3, State = 4, Event = 5,
        Ping = 6, Pong = 7
    }

    /// Boundary between the transport (sockets, seq/ack, RTT, retransmit) and the application
    /// protocol, drafted before either side so they can be built independently. Callers honor the
    /// channel discipline in DESIGN §8.
    public interface IReliableTransport
    {
        void Send(Channel channel, MessageType type, ReadOnlySpan<byte> payload);

        // Send a reliable-typed message with a caller-fixed message-seq, bypassing the retransmit queue.
        // The handshake uses this so Session can re-probe HELLO on its own connect cadence while holding the
        // seq constant: the peer's in-order receive buffer dedupes the repeats. A queued (auto-incrementing)
        // seq would instead stall that buffer, which waits for the gaps left by probes the host missed before
        // it was listening, so the host would never deliver the probe it actually caught.
        void SendReliableFixedSeq(MessageType type, ushort messageSeq, ReadOnlySpan<byte> payload);

        // Drained on the main thread; reliable is in-order + de-duplicated, unreliable latest-wins.
        bool TryReceive(out MessageType type, out byte[] payload);

        float RttSeconds { get; }
        bool Connected { get; }

        event Action OnConnected;
        event Action OnDisconnected;

        // Pump retransmit / RTT / timeout timers; call once per step on the main thread.
        void Tick(float dt);
    }
}
