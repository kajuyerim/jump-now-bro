using System;
using JumpNowBro.Util;

namespace JumpNowBro.Networking
{
    /// Application handshake on top of the reliable transport, and the SOLE authority on whether a
    /// session is up. It drives its state off validated HELLO/WELCOME via TryReceive — never the
    /// transport's low-level "first datagram seen" flag — but does honor the transport's OnDisconnected
    /// (reliable give-up / liveness timeout). Client sends HELLO and awaits WELCOME; host awaits the
    /// (already peer-validated, pre-seeded) HELLO and replies WELCOME.
    public sealed class Session
    {
        public enum SessionState { Idle, Connecting, Established, Disconnected }
        /// Why the session ended — read by the UI to word the disconnect and decide whether to surface it.
        public enum DisconnectReason { None, LocalLeave, PeerLeft, ConnectionLost, HandshakeFailed }

        // The client re-probes HELLO at a steady cadence for the whole budget, so a host that starts late
        // (the join-before-host case, #120) is caught by the next probe instead of the client having gone
        // silent under the reliable queue's backoff. The budget is the only FAST teardown for a handshake
        // that never lands; it is long enough for a human to start the host after pressing Join.
        const double ConnectBudgetSeconds = 15.0;
        const double HelloProbeInterval = 0.5;      // client re-sends HELLO this often while Connecting
        const int MaxBody = 128;                    // Welcome/Hello bodies now carry a display name; sized for the 16-char cap + headroom

        readonly IReliableTransport transport;
        readonly bool isHost;
        readonly Func<byte> sceneIndexProvider;     // host only — read at WELCOME-send time so it reflects current scene
        readonly Func<uint> hostTickProvider;       // host only — sampled into WELCOME body for debug / future offset estimation
        readonly Func<string> localNameProvider;    // this player's display name, stamped into HELLO (client) / WELCOME (host)
        readonly Func<byte> localColorProvider;     // this player's colour slot, stamped into HELLO (client) / WELCOME (host)
        readonly byte[] scratch = new byte[MaxBody];

        double clock;
        double connectingSince;
        double lastHelloAt;                         // client only — clock of the last HELLO probe

        public SessionState State { get; private set; } = SessionState.Idle;
        public GoodbyeReason LastGoodbye { get; private set; }
        public DisconnectReason LastDisconnect { get; private set; }
        public float RttSeconds => transport.RttSeconds;
        public event Action<SessionState> OnStateChanged;
        /// Raised on the CLIENT after a valid WELCOME is parsed — carries scene index for mid-game join + peer slot confirmation.
        public event Action<Welcome> OnWelcomeReceived;
        /// Raised on the HOST after a valid HELLO is parsed — carries the client's display name + colour (#114, #125).
        public event Action<Hello> OnHelloReceived;
        /// Raised for message types Session doesn't itself handle (INPUT/STATE/EVENT/PING/PONG bodies).
        /// NetworkManager subscribes and routes to whichever gameplay consumer is registered.
        public event Action<MessageType, byte[]> OnGameplayMessage;

        public Session(IReliableTransport transport, bool isHost,
                       Func<byte> sceneIndexProvider = null, Func<uint> hostTickProvider = null,
                       Func<string> localNameProvider = null, Func<byte> localColorProvider = null)
        {
            this.transport = transport;
            this.isHost = isHost;
            this.sceneIndexProvider = sceneIndexProvider;
            this.hostTickProvider = hostTickProvider;
            this.localNameProvider = localNameProvider;
            this.localColorProvider = localColorProvider;
            transport.OnDisconnected += () => Disconnect(DisconnectReason.ConnectionLost);
        }

        string LocalName() => localNameProvider != null ? localNameProvider() : "";
        byte LocalColor() => localColorProvider != null ? localColorProvider() : (byte)0;

        public void Start()
        {
            connectingSince = clock;
            lastHelloAt = clock;
            SetState(SessionState.Connecting);
            if (!isHost) SendHello();                // client speaks first
        }

        public void Tick(float dt)
        {
            clock += dt;
            transport.Tick(dt);
            while (transport.TryReceive(out var type, out var payload))
                Handle(type, payload);

            if (State == SessionState.Connecting)
            {
                // Client keeps re-probing until WELCOME lands or the budget runs out (host may start late).
                if (!isHost && clock - lastHelloAt >= HelloProbeInterval) { SendHello(); lastHelloAt = clock; }
                if (clock - connectingSince > ConnectBudgetSeconds)
                    Disconnect(DisconnectReason.HandshakeFailed);
            }
        }

        public void SendGoodbye(GoodbyeReason reason)
        {
            if (State == SessionState.Disconnected) return;
            int n = new Goodbye { Reason = reason }.Write(scratch);
            transport.Send(Channel.Reliable, MessageType.Goodbye, scratch.AsSpan(0, n));
            Disconnect(DisconnectReason.LocalLeave);  // caller pumps one more Tick so the GOODBYE actually flushes
        }

        void Handle(MessageType type, byte[] payload)
        {
            switch (type)
            {
                case MessageType.Hello when isHost:
                    if (State != SessionState.Connecting) break;   // already established: ignore retried/duplicate HELLOs (no second WELCOME)
                    bool ok = Hello.TryRead(payload, out var hello)
                              && hello.Magic == SessionProtocol.Magic && hello.Version == SessionProtocol.Version;
                    if (ok) OnHelloReceived?.Invoke(hello);        // surface client name/colour before Established triggers the level load
                    SendWelcome(ok, ok ? WelcomeReason.Accepted : WelcomeReason.VersionMismatch);
                    if (ok) SetState(SessionState.Established);
                    else Disconnect(DisconnectReason.HandshakeFailed);
                    break;

                case MessageType.Welcome when !isHost:
                    bool wellFormed = Welcome.TryRead(payload, out var w);
                    bool accepted = wellFormed && w.Accepted
                                    && w.Magic == SessionProtocol.Magic && w.Version == SessionProtocol.Version;
                    if (accepted) OnWelcomeReceived?.Invoke(w);   // fires BEFORE state flip so subscribers see Established with welcome in hand
                    if (accepted) SetState(SessionState.Established);
                    else Disconnect(DisconnectReason.HandshakeFailed);
                    break;

                case MessageType.Goodbye:
                    if (Goodbye.TryRead(payload, out var g)) LastGoodbye = g.Reason;
                    Disconnect(DisconnectReason.PeerLeft);
                    break;

                default:
                    OnGameplayMessage?.Invoke(type, payload);             // INPUT/STATE/EVENT/PING/PONG — forward to NetworkManager dispatcher
                    break;
            }
        }

        void SendHello()
        {
            var hello = new Hello
            {
                Magic = SessionProtocol.Magic, Version = SessionProtocol.Version,
                ColorIndex = LocalColor(), Name = LocalName(),
            };
            int n = hello.Write(scratch);
            transport.SendHelloProbe(scratch.AsSpan(0, n));
        }

        void SendWelcome(bool accepted, WelcomeReason reason)
        {
            // v1.4 convention: host owns P1, client owns P2. The byte is confirmation, not configuration —
            // host enforces the binding regardless; client logs/asserts agreement.
            var welcome = new Welcome
            {
                Magic              = SessionProtocol.Magic,
                Version            = SessionProtocol.Version,
                Accepted           = accepted,
                Reason             = reason,
                PeerOwner          = InputOwner.P2,
                CurrentSceneIndex  = sceneIndexProvider != null ? sceneIndexProvider() : (byte)0xFF,
                HostTickAtWelcome  = hostTickProvider   != null ? hostTickProvider()   : 0u,
                ColorIndex         = LocalColor(),
                Name               = LocalName(),
            };
            int n = welcome.Write(scratch);
            transport.Send(Channel.Reliable, MessageType.Welcome, scratch.AsSpan(0, n));
        }

        // Single disconnect choke point: stamp the reason (first wins — Disconnected is terminal) then transition.
        void Disconnect(DisconnectReason reason)
        {
            if (State == SessionState.Disconnected) return;
            LastDisconnect = reason;
            SetState(SessionState.Disconnected);
        }

        void SetState(SessionState s)
        {
            if (State == s || State == SessionState.Disconnected) return;   // Disconnected is terminal for this instance
            State = s;
            OnStateChanged?.Invoke(s);
        }
    }
}
