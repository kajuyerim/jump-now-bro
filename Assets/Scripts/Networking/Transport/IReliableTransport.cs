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

        // Start/repeat the client's HELLO while connecting, before any other reliable sends. Repeated
        // probes must reach a late-starting peer without leaving gaps in later reliable delivery.
        // The backend owns retry/dedup details; callers do not choose a transport sequence number.
        void SendHelloProbe(ReadOnlySpan<byte> payload);

        // Drained on the main thread; reliable is in-order + de-duplicated, unreliable latest-wins.
        bool TryReceive(out MessageType type, out byte[] payload);

        float RttSeconds { get; }
        bool Connected { get; }

        // Per-connection diagnostics. Misses are cumulative and may decrease when reordering is repaired.
        int PendingReliableCount { get; }
        int DroppedDatagrams { get; }
        int PacketsAccepted { get; }
        int PacketsMissed { get; }
        Action<string> Logger { get; set; }
        void SetPingInterval(double seconds);

        event Action OnConnected;
        event Action OnDisconnected;

        // Pump retransmit / RTT / timeout timers; call once per step on the main thread.
        void Tick(float dt);
    }
}
