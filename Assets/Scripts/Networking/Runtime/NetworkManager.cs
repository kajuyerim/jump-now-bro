using System;
using System.Net;
using UnityEngine;
using JumpNowBro.Gameplay;
using JumpNowBro.Util;

namespace JumpNowBro.Networking
{
    /// The session orchestrator on Bootstrap's Manager. Owns the gameplay socket, (optional) condition
    /// channel, discovery, and the Session (which itself owns the transport). In Hosting it binds the
    /// gameplay port, polls the raw socket for a validated HELLO, and on first match latches that peer
    /// and builds the wire. Client lifecycle lands in the client-lifecycle issue. SinglePlayer is inert
    /// — the game runs from Bootstrap exactly as today.
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-100)]                                         // Awake before PlayerSpawner reads Instance.Role (#78 wiring)
    public sealed class NetworkManager : MonoBehaviour
    {
        public static NetworkManager Instance { get; private set; }

        [SerializeField] GameRole startupRole = GameRole.SinglePlayer;
        [SerializeField] ushort gameplayPort = 7777;
        [SerializeField] string manualHostIp = "127.0.0.1";
        [SerializeField] ushort discoveryPort = 47777;
        [SerializeField] string gameName = "Jump Now Bro!";

        public GameRole Role { get; private set; }
        public Session.SessionState? CurrentSessionState => session?.State;
        public float CurrentRtt => session?.RttSeconds ?? 0f;
        /// Exposed so the #78 spawner can hand the live transport to broadcasters/senders/receivers.
        public IReliableTransport CurrentTransport => transport;
        /// The host's last-consumed client tick — the clock the swap scheduler keys apply_at_tick on when hosting.
        public uint HostConsumedClientTick => currentHostRemote != null ? currentHostRemote.LastConsumedClientTick : 0u;
        /// Diagnostics for the connection panel (#91): reliable backlog + malformed-drop count.
        public int PendingReliableCount => transport != null ? transport.PendingReliableCount : 0;
        public int DroppedDatagrams => transport != null ? transport.DroppedDatagrams : 0;
        /// #132: sustained-degradation flag with hysteresis (RTT + inbound loss); drives the menu's subtle indicator.
        public bool ConnectionUnstable => quality != null && quality.Unstable;

        // ---- v2.3 lobby surface (#143) ----
        /// Pre-game staging state: LobbyUI shows while true. Host: from hosting start (session may still be
        /// null while listening). Client: only once Established (Connecting keeps the dial-status UI). Never
        /// in solo, never mid-level (CurrentLevelIndex >= 0), never under the connection-lost overlay.
        public bool InLobby =>
            !connectionLost
            && LevelManager.Instance != null && LevelManager.Instance.CurrentLevelIndex < 0
            && (Role == GameRole.Hosting
                || (Role == GameRole.Client && CurrentSessionState == Session.SessionState.Established
                    && !clientJoinedPostVictory));
        public bool PeerConnected => CurrentSessionState == Session.SessionState.Established;
        public bool PeerReady => peerReady;          // host's view of the client's ready toggle
        public bool LocalReady => localReady;        // the client's own toggle
        public int LobbySelectedLevel => lobbySelectedLevel;
        /// Connection-loss UX (#90): true while a peer-initiated drop is surfaced (sim paused, overlay up).
        public bool ConnectionLost => connectionLost;
        public bool SoloActive => soloActive;
        public Session.DisconnectReason LostReason => lostReason;
        /// Discovery port, so the menu can run its own passive browse (DiscoveryService.StartClient) while idle.
        public ushort DiscoveryPort => discoveryPort;

        UdpSocket gameplaySocket;
        #pragma warning disable 0649   // assigned only under UNITY_EDITOR (lag-sim); stays null in player builds
        NetworkConditionChannel condChannel;
        #pragma warning restore 0649
        DiscoveryService discovery;
        Session session;
        UdpReliableTransport transport;                                   // kept on the manager so #78 broadcasters/senders can read it via Instance.CurrentTransport
        ConnectionQualityMonitor quality;                                 // #132: lifetime == transport's (fresh per transport, or deltas go negative on rejoin)
        bool listening;                // host is in the listen-for-HELLO phase
        bool connectionLost;           // #90: a peer drop is being surfaced (paused + overlay) until rejoin/menu
        bool soloActive;               // Solo (no-session single-player) is running — keeps the Leave button up
        int lastHostedLevelIndex = -1; // #104: a host Leave remembers its level so the next Host resumes it (v2.3: as a lobby preselect)
        int lobbySelectedLevel;        // v2.3: the host's lobby level pick; mirrored to the client via LobbyState
        bool peerReady;                // v2.3: host-side, the client's LobbyReady flag
        bool localReady;               // v2.3: client-side, its own Ready toggle
        bool clientJoinedPostVictory;  // v2.3: WELCOME carried the all-complete sentinel — show CompleteScreen, not the lobby
        Session.DisconnectReason lostReason;
        string localPlayerName = "";   // #114: this player's display name from the menu (stamped into HELLO/WELCOME)
        byte localColorIndex;          // #125: assigned colour slot — host = 0, client = 1
        double clock;
        readonly byte[] eventSendScratch = new byte[EventBody.MaxSize];   // sized to the largest EVENT variant (Swap)

        // Per-spawn role-aware components — re-bound each PlayerSpawner.OnPlayerSpawned. The dispatch
        // closures (state/input handlers) close over `this`, then read these fields fresh each call, so
        // a Player respawn (level transition) hot-swaps the target without any handler nulling races.
        NetworkRemoteInputSource currentHostRemote;
        ClientStateRenderer currentClientRenderer;

        // Host load barrier (#87): while waiting for the client's LEVEL_READY, hold sim + STATE so the host
        // never advances into a scene the client hasn't loaded (client casts would hit nothing → fall-through).
        bool barrierArmed;
        int barrierScene;
        double barrierDeadline;
        const double BarrierTimeoutSeconds = 2.0;   // < the 5 s liveness teardown: a truly dead peer is handled there, not here

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            // Register the Authority.IsHost check used by the four trigger gates. Default is "act as host";
            // we register a real check that returns false on Client. SinglePlayer + Hosting still pass.
            Authority.RegisterIsHost(
                () => Instance == null || Instance.Role == GameRole.SinglePlayer || Instance.Role == GameRole.Hosting);
            Role = startupRole;
            if (Role == GameRole.SinglePlayer) return;
            Application.runInBackground = true;                           // keep ticking while unfocused so PINGs flow between editors (else the 5s liveness fires)
            FindAnyObjectByType<LevelManager>()?.SuppressAutoStart();     // runs in Awake -> beats LevelManager.Start
            try
            {
                if (Role == GameRole.Hosting) BeginHosting();
                else if (Role == GameRole.Client) BeginClient();
            }
            catch (System.Exception e)
            {
                Debug.LogError($"NetworkManager: failed to start as {Role} — {e.Message}");
                EndSessionFromUi();                                       // unwind any partial init and reset so the ConnectionUI stays usable
            }
        }

        void Start()
        {
            // Subscriptions live for the manager's lifetime — fires for any role. Handlers themselves
            // check Role at call time (so a BeginHostingFromUi flip later in the session works).
            var spawner = FindAnyObjectByType<PlayerSpawner>();
            if (spawner != null) spawner.OnPlayerSpawned += OnPlayerSpawnedDispatch;
            if (LevelManager.Instance != null)
            {
                LevelManager.Instance.OnBeforeLevelLoad += OnLevelLoadBegin;
                LevelManager.Instance.OnLevelLoaded += OnClientLevelLoaded;
            }
        }

        void Update()
        {
            if (Role == GameRole.SinglePlayer) return;                    // SinglePlayer is inert
            clock += Time.deltaTime;                                      // one Σdt clock fed to lag-channel + session/transport + discovery
            if (Role == GameRole.Hosting && listening && gameplaySocket != null) PollForHello();
            condChannel?.Release(clock);
            session?.Tick(Time.deltaTime);                                // session pumps its transport internally — never tick transport directly
            if (transport != null && quality != null)                     // #132: feed smoothed RTT + inbound loss counters.
                // The EMA, not the raw sample: raw PONGs land at 1 Hz and HOLD between arrivals, so a 2 s
                // sustain is really just two samples — jitter + sim frame-quantization then false-trips Fair.
                quality.Tick(Time.deltaTime, transport.RttSeconds, transport.PacketsAccepted, transport.PacketsMissed);
            discovery?.Tick(clock);
            if (barrierArmed && clock >= barrierDeadline)                 // ack lost but link maybe alive: resume best-effort, let liveness own a real death
            {
                Debug.LogWarning($"[Hosting] LEVEL_READY timeout for scene {barrierScene} — resuming.");
                ClearBarrier();
            }
        }

        void OnDestroy()
        {
            var s = session;                                              // capture: SendGoodbye -> SetState -> OnStateChanged nulls the field
            if (s != null)
            {
                s.SendGoodbye(GoodbyeReason.Normal);                      // queue a graceful GOODBYE...
                s.Tick(0);                                                // ...and flush it (firstSend ignores RTO)
            }
            discovery?.Dispose();
            gameplaySocket?.Dispose();                                    // kills the background receive thread on play-stop
            if (Instance == this) Instance = null;
        }

#if UNITY_EDITOR
        // Editor-only network-condition simulator (#91). NOT a serialized/shipped field — selected per
        // ParrelSync editor process via the ConnectionUI cycle button, read at channel-build time below.
        // Compiles out of player builds entirely; the live transport is then always the raw socket.
        public enum LagProfile { Clean, Fair, Stress }
        public static LagProfile EditorSimProfile = LagProfile.Clean;

        // Wrap the raw datagram channel in the loss/latency sim when a profile is active. Latency is ONE-WAY
        // (RTT ≈ 2×); host/client use different seeds so the two egress loss streams are uncorrelated.
        IDatagramChannel WrapForSim(IDatagramChannel inner, bool hostSide)
        {
            if (EditorSimProfile == LagProfile.Clean) { condChannel = null; return inner; }
            (double lat, double jit, double loss) = EditorSimProfile == LagProfile.Fair
                ? (0.075, 0.020, 0.05)
                : (0.125, 0.050, 0.10);
            condChannel = new NetworkConditionChannel(inner, latencySeconds: lat, jitterSeconds: jit,
                                                      lossProb: loss, seed: hostSide ? 1337 : 7331);
            return condChannel;
        }
#endif

        // ---- host lifecycle ----

        void BeginHosting()
        {
            gameplaySocket = new UdpSocket(gameplayPort);                 // gameplay socket: NOT broadcast (the discovery socket gets that)
            try
            {
                discovery = DiscoveryService.StartHost(discoveryPort, new LanBeacon
                {
                    Magic = SessionProtocol.Magic,
                    GameName = gameName,
                    GameplayPort = gameplayPort,
                });
            }
            catch                                                         // discovery is best-effort; unwind cleanly so Update doesn't see a half-built host
            {
                gameplaySocket.Dispose();
                gameplaySocket = null;
                throw;
            }
            listening = true;
        }

        // Drains the raw socket while no peer is latched. Drops anything that isn't a valid HELLO; on the
        // first valid one, latches that sender as the peer and builds the channel + transport + session.
        void PollForHello()
        {
            while (gameplaySocket.Poll(out var data, out var from))
            {
                if (SessionProtocol.IsValidHello(data)) { LatchPeer(from, data); return; }
                RejectWrongVersionHello(data, from);
                // otherwise not a valid HELLO (junk / wrong magic): drop and keep draining
            }
        }

        void LatchPeer(IPEndPoint peer, byte[] helloDatagram)
        {
            var inner = new UdpDatagramChannel(gameplaySocket, peer);
            inner.PreSeed(helloDatagram);                                 // transport processes the validated HELLO immediately
            IDatagramChannel ch = inner;
#if UNITY_EDITOR
            ch = WrapForSim(inner, hostSide: true);                       // editor-only lag-sim (no-op at Clean)
#endif
            transport = new UdpReliableTransport(ch, pingIntervalSeconds: 0.2);      // ~5 Hz keepalive — v1.2's only traffic until #76's Established hook flips to 1 Hz
            transport.Logger = msg => Debug.LogWarning($"[net] {msg}");              // surface should-never-happen drops (oversized send)
            quality = new ConnectionQualityMonitor();                                // #132: fresh monitor per transport (counter baselines line up)
            // Providers sampled at WELCOME-send time so currentSceneIndex reflects the actual scene
            // the host is on (mid-game join case); 0xFF sentinel means "no level loaded yet".
            session = new Session(transport, isHost: true,
                sceneIndexProvider: () => {
                    var lmp = LevelManager.Instance;
                    int idx = lmp != null ? lmp.CurrentLevelIndex : -1;
                    if (idx < 0) return (byte)0xFF;                                  // pre-load: client waits (lobby)
                    if (idx >= lmp.LevelCount) return LevelManager.AllLevelsCompleteSentinel;   // post-victory join
                    return (byte)idx;                                                // was raw out-of-range post-victory: client LoadByIndex errored to a blank screen
                },
                hostTickProvider: () => TickClock.Instance != null ? TickClock.Instance.Current : 0u,
                localNameProvider: () => localPlayerName,
                localColorProvider: () => localColorIndex);
            session.OnStateChanged += OnSessionStateChanged;
            session.OnGameplayMessage += OnGameplayMessageDispatch;
            session.OnHelloReceived += OnHostHelloReceived;               // learn the client's name/colour for the HUD
            session.Start();                                              // queues WELCOME; flushes on the next session.Tick
            listening = false;
        }

        // A stale-build peer's HELLO (right magic, wrong version) would otherwise be dropped silently here and
        // read as a network failure after the client's 15 s dial-out. One raw not-accepted WELCOME makes the
        // version skew fail fast and loud instead (#142 bumped the protocol to v3 for the lobby kinds).
        void RejectWrongVersionHello(byte[] datagram, IPEndPoint from)
        {
            if (!PacketHeader.TryRead(datagram, out var h) || h.type != MessageType.Hello) return;
            int bodyOffset = PacketHeader.Size + 2;                       // header + reliable message-seq
            if (datagram.Length < bodyOffset) return;
            if (!Hello.TryRead(new ReadOnlySpan<byte>(datagram, bodyOffset, datagram.Length - bodyOffset), out var hello)) return;
            if (hello.Magic != SessionProtocol.Magic || hello.Version == SessionProtocol.Version) return;

            var welcome = new Welcome
            {
                Magic = SessionProtocol.Magic, Version = SessionProtocol.Version,
                Accepted = false, Reason = WelcomeReason.VersionMismatch,
                PeerOwner = JumpNowBro.Util.InputOwner.P2, CurrentSceneIndex = 0xFF, Name = "",
            };
            var buf = new byte[128];
            new PacketHeader { type = MessageType.Welcome, seq = 1, ack = 0, ackBits = 0, timestamp = 0 }.Write(buf);
            int off = PacketHeader.Size;
            buf[off++] = 0; buf[off++] = 1;                               // message-seq 1: the peer's fresh receive buffer delivers it first
            int n = welcome.Write(new Span<byte>(buf, off, buf.Length - off));
            gameplaySocket.Send(new ReadOnlySpan<byte>(buf, 0, off + n), from);
        }

        // ---- client lifecycle ----

        void BeginClient()
        {
            gameplaySocket = new UdpSocket(0);                            // ephemeral port: host + client coexist on one machine
            discovery = DiscoveryService.StartClient(discoveryPort);      // host list builds for the connection UI
            var host = new IPEndPoint(IPAddress.Parse(manualHostIp), gameplayPort);
            var inner = new UdpDatagramChannel(gameplaySocket, host);
            IDatagramChannel ch = inner;
#if UNITY_EDITOR
            ch = WrapForSim(inner, hostSide: false);                      // editor-only lag-sim (no-op at Clean)
#endif
            transport = new UdpReliableTransport(ch, pingIntervalSeconds: 0.2);
            transport.Logger = msg => Debug.LogWarning($"[net] {msg}");
            quality = new ConnectionQualityMonitor();                     // #132: fresh monitor per transport
            session = new Session(transport, isHost: false,
                localNameProvider: () => localPlayerName,
                localColorProvider: () => localColorIndex);
            session.OnStateChanged += OnSessionStateChanged;              // subscribe BEFORE Start so we observe Idle->Connecting
            session.OnWelcomeReceived += OnClientWelcomeReceived;         // mid-game join: load whichever scene host is on
            session.OnGameplayMessage += OnGameplayMessageDispatch;
            session.Start();                                              // sends HELLO; awaits WELCOME
        }

        // Client: ack each completed additive load so the host can lift its barrier. Subscribed once in Start.
        void OnClientLevelLoaded(int sceneIndex)
        {
            if (Role != GameRole.Client || transport == null) return;
            int n = EventBody.LevelReady((byte)sceneIndex).Write(eventSendScratch);
            transport.Send(Channel.Reliable, MessageType.Event, new ReadOnlySpan<byte>(eventSendScratch, 0, n));
        }

        void OnGameplayMessageDispatch(MessageType type, byte[] payload)
        {
            switch (type)
            {
                case MessageType.State:
                    if (currentClientRenderer != null) currentClientRenderer.ApplyPayload(payload);
                    break;
                case MessageType.Input:
                    if (currentHostRemote != null
                        && InputBody.TryRead(payload, out var baseTick, out _, out var packed))
                        currentHostRemote.EnqueueFromInputBody(baseTick, packed);
                    break;
                case MessageType.Event:
                    if (EventBody.TryRead(payload, out var ev)) DispatchEvent(ev);
                    break;
                // Ping/Pong are transport-internal — handled inside UdpReliableTransport, never bubble up here.
            }
        }

        // ---- spawn-time role-aware wiring ----

        // Called for every PlayerSpawner.OnPlayerSpawned (every level load). SinglePlayer leaves the
        // prefab's PlayerBootstrap to wire local keyboards; Host/Client swap in the network-aware ones.
        void OnPlayerSpawnedDispatch(GameObject instance)
        {
            if (Role == GameRole.SinglePlayer) return;

            var bootstrap = instance.GetComponent<PlayerBootstrap>();
            if (bootstrap != null) Destroy(bootstrap);

            if (Role == GameRole.Hosting) WireHosting(instance);
            else if (Role == GameRole.Client) WireClient(instance);
        }

        // Networked play points the local keyboard at one shared layout (#117) so both players use the same
        // keys on their own machine; the split Player1/Player2 maps are only for solo single-keyboard play.
        const string NetPlayerMap = "NetPlayer";

        void WireHosting(GameObject instance)
        {
            var ctrl  = instance.GetComponent<PlayerController>();
            var keyP1 = instance.GetComponent<KeyboardInputSource_P1>();
            var keyP2 = instance.GetComponent<KeyboardInputSource_P2>();
            if (keyP2 != null) Destroy(keyP2);                            // host's P2 input comes over the wire
            if (keyP1 != null) keyP1.Rebind(NetPlayerMap);               // host drives P1 with the shared layout

            currentHostRemote = instance.AddComponent<NetworkRemoteInputSource>();
            if (ctrl != null)
            {
                ctrl.Inject(keyP1, currentHostRemote);
                // Send a reliable DEATH EVENT when the host dies — carries the checkpoint map so the client
                // resets ownership in-order (after any swaps), plus the death tick for v1.7 anchoring.
                ctrl.OnDeath += _ => SendDeathEvent(HostConsumedClientTick, ctrl.CheckpointMap);
            }

            var bcast = instance.AddComponent<NetworkStateBroadcaster>();
            // Note: transport intentionally NOT passed — broadcaster reads NetworkManager.CurrentTransport
            // dynamically so a client-rejoin (new transport, same host Player) auto-picks up the new endpoint.
            bcast.Bind(ControlMapStore.Instance, LevelManager.Instance,
                       () => currentHostRemote != null ? currentHostRemote.LastConsumedClientTick : 0u);

            // #124 intent: host reads P1 from its local keyboard, P2 from the client's decoded input.
            GhostIntentSources.Register(
                () => GhostIntentSources.From(keyP1),
                () => GhostIntentSources.From(currentHostRemote));
        }

        void WireClient(GameObject instance)
        {
            // Client predicts via ClientPredictor (v1.5) but the host stays authoritative. PlayerController is
            // removed — capture the tuning it carries FIRST, since the predictor needs the same MovementTuning
            // and the serialized fields vanish with the component. The collision config + Rigidbody2D survive.
            var ctrl = instance.GetComponent<PlayerController>();
            PlayerTuning tuning = ctrl != null ? ctrl.Tuning : null;
            float fallLimitY    = ctrl != null ? ctrl.FallLimitY : -20f;
            if (ctrl != null) Destroy(ctrl);
            var keyP1 = instance.GetComponent<KeyboardInputSource_P1>();
            if (keyP1 != null) Destroy(keyP1);

            // Client = P2 by convention; sampler reads the local P2 keyboard.
            var keyP2 = instance.GetComponent<KeyboardInputSource_P2>();
            if (keyP2 != null) keyP2.Rebind(NetPlayerMap);               // client drives P2 with the shared layout

            var sender = instance.AddComponent<ClientInputSender>();
            sender.Bind(keyP2, transport, TickClock.Instance);

            currentClientRenderer = instance.AddComponent<ClientStateRenderer>();

            var rb = instance.GetComponent<Rigidbody2D>();
            var collisionConfig = instance.GetComponent<PlayerCollisionConfig>();
            var visualChild = instance.transform.Find("Visual");      // render-only child (#107); null falls back to no smoothing
            var predictor = instance.AddComponent<ClientPredictor>();
            predictor.Bind(sender, currentClientRenderer, TickClock.Instance, ControlMapStore.Instance,
                           rb, collisionConfig != null ? collisionConfig.CreateWorld(rb) : null,
                           tuning, fallLimitY, visualChild);

            // #124 intent: client reads P1 from the host's STATE frame, P2 from its local keyboard.
            GhostIntentSources.Register(
                () => currentClientRenderer != null ? GhostIntentSources.From(currentClientRenderer.LastRemoteHostFrame) : default,
                () => GhostIntentSources.From(keyP2));
        }

        // Route a decoded EVENT by (role, kind). The reliable EVENT stream is shared by both directions, so each
        // role handles only the kinds it should receive and ignores the rest (D11). LevelReady/Death land later.
        void DispatchEvent(in EventBody ev)
        {
            if (Role == GameRole.Client)
            {
                switch (ev.kind)
                {
                    case EventKind.LevelLoad:
                        LevelManager.Instance?.LoadByIndex(ev.sceneIndex);
                        break;
                    case EventKind.Swap:
                        SwapScheduleDriver.Instance?.Scheduler.Schedule(ev.tick, ev.map, ev.triggerId);
                        break;
                    case EventKind.Death:
                        // Ordered cancel: this EVENT is delivered after the swaps it supersedes, so dropping all
                        // pending swaps and resetting to the checkpoint map can't be undone by a stale swap. The
                        // death count + flash still come via STATE.deathCount → DeathNotifier (mid-join-safe).
                        SwapScheduleDriver.Instance?.Scheduler.ResetTo(ev.map);
                        ControlMapStore.Instance?.Apply(ev.map);
                        SwapTrigger.ReconcileBannersTo(ev.map);   // #111: re-arm post-checkpoint banners on the client
                        break;
                    case EventKind.LobbyState:
                        lobbySelectedLevel = ev.sceneIndex;       // v2.3: the host's level pick, shown in the client's lobby
                        break;
                }
            }
            else if (Role == GameRole.Hosting)
            {
                switch (ev.kind)
                {
                    case EventKind.LevelReady:
                        // Scene-matched so a stale ack for a previous load can't unfreeze us into the wrong scene.
                        if (barrierArmed && ev.sceneIndex == barrierScene) ClearBarrier();
                        break;
                    case EventKind.LobbyReady:
                        // Honored only pre-game: StartGameFromLobby's LoadByIndex sets the index synchronously,
                        // so a late un-ready can't stop a load that already started (LevelLoad is authoritative).
                        if (LevelManager.Instance != null && LevelManager.Instance.CurrentLevelIndex < 0)
                            peerReady = ev.ready == 1;
                        break;
                }
            }
        }

        void OnLevelLoadBegin(int sceneIndex)
        {
            if (Role != GameRole.Hosting || transport == null) return;
            int n = EventBody.LevelLoad((byte)sceneIndex).Write(eventSendScratch);
            transport.Send(Channel.Reliable, MessageType.Event, new ReadOnlySpan<byte>(eventSendScratch, 0, n));

            // Arm the load barrier ONLY for a real scene with a connected client. Carve-outs (else the host
            // would freeze forever waiting for a LEVEL_READY no one sends): an Established session is required
            // (excludes solo / no-client / departed-client), and the 0xFE all-levels-complete sentinel loads no
            // scene on the client (it shows the victory screen) so it must not arm.
            var lm = LevelManager.Instance;
            bool realScene = lm != null && sceneIndex >= 0 && sceneIndex < lm.LevelCount;
            bool clientConnected = session != null && session.State == Session.SessionState.Established;
            if (realScene && clientConnected)
            {
                barrierArmed = true;
                barrierScene = sceneIndex;
                barrierDeadline = clock + BarrierTimeoutSeconds;
                lm.SimPaused = true;
            }
        }

        // Lift the barrier: resume the host sim + STATE broadcast.
        void ClearBarrier()
        {
            barrierArmed = false;
            if (LevelManager.Instance != null) LevelManager.Instance.SimPaused = false;
        }

        // EndSession-lite for a mid-game partner Leave: tear the run down (player, HUD, death totals) but
        // keep the socket + listening so the host lands back in its lobby, current level preselected, ready
        // for the next partner. The stale level scene stays loaded behind the opaque lobby; the next Start
        // unload-then-loads through the existing LoadLevelRoutine handoff (same pattern as EndSessionFromUi).
        void ReturnHostToLobby()
        {
            var lm = LevelManager.Instance;
            if (lm != null)
                lobbySelectedLevel = Mathf.Clamp(lm.CurrentLevelIndex, 0, Mathf.Max(0, lm.LevelCount - 1));
            if (PlayerSpawner.Instance != null && PlayerSpawner.Instance.CurrentPlayerInstance != null)
                Destroy(PlayerSpawner.Instance.CurrentPlayerInstance);
            currentHostRemote = null;
            lm?.ResetIndex();                                            // index < 0 -> InLobby true, lobby shows
            if (lm != null) lm.SimPaused = false;
            CompleteScreen.Instance?.HidePanel();
            FindAnyObjectByType<LevelHud>()?.Clear();
            DeathNotifier.Instance?.Reset();
            PlayerSpawner.Instance?.ResetDeathAccumulation();
            GhostIntentSources.Reset();
        }

        // ---- v2.3 lobby actions (#143) ----

        /// Host: pick the level the lobby will start. Clamped; mirrored to the client via LobbyState.
        public void SetLobbyLevel(int index)
        {
            if (Role != GameRole.Hosting) return;
            var lm = LevelManager.Instance;
            lobbySelectedLevel = Mathf.Clamp(index, 0, lm != null ? Mathf.Max(0, lm.LevelCount - 1) : 0);
            SendLobbyState();
        }

        void SendLobbyState()
        {
            if (Role != GameRole.Hosting || transport == null) return;   // pre-peer: stored locally, sent on Established
            int n = EventBody.LobbyState((byte)lobbySelectedLevel).Write(eventSendScratch);
            transport.Send(Channel.Reliable, MessageType.Event, new ReadOnlySpan<byte>(eventSendScratch, 0, n));
        }

        /// Client: toggle the pre-game Ready flag (reliable, so the host's Start gate can't miss it).
        public void SetLobbyReady(bool ready)
        {
            if (Role != GameRole.Client || transport == null || !PeerConnected) return;
            localReady = ready;
            int n = EventBody.LobbyReady(ready).Write(eventSendScratch);
            transport.Send(Channel.Reliable, MessageType.Event, new ReadOnlySpan<byte>(eventSendScratch, 0, n));
        }

        /// Host: start the picked level. Requires a connected, ready client; drives the EXISTING
        /// LoadByIndex path (LevelLoad EVENT + SimPaused barrier + client LevelReady ack, all unchanged).
        public void StartGameFromLobby()
        {
            if (Role != GameRole.Hosting || !PeerConnected || !peerReady) return;
            peerReady = false; localReady = false;
            LevelManager.Instance?.LoadByIndex(lobbySelectedLevel);
        }

        /// Host: send a scheduled control swap on the reliable channel. apply_at_tick is a client input-tick so
        /// both ends flip ControlMapStore at the same point in the input stream. No-op off the host.
        public void SendSwapEvent(uint applyTick, ControlMap map, byte triggerId)
        {
            if (Role != GameRole.Hosting || transport == null) return;
            int n = EventBody.Swap(applyTick, map, triggerId).Write(eventSendScratch);
            transport.Send(Channel.Reliable, MessageType.Event, new ReadOnlySpan<byte>(eventSendScratch, 0, n));
        }

        /// Host: send the reliable DEATH EVENT carrying the checkpoint map (ordered after any pending swaps so
        /// the client's cancel + map reset can't be clobbered by a late swap). No-op off the host.
        public void SendDeathEvent(uint deathTick, ControlMap checkpointMap)
        {
            if (Role != GameRole.Hosting || transport == null) return;
            int n = EventBody.Death(deathTick, checkpointMap).Write(eventSendScratch);
            transport.Send(Channel.Reliable, MessageType.Event, new ReadOnlySpan<byte>(eventSendScratch, 0, n));
        }

        // ---- shared ----

        void OnSessionStateChanged(Session.SessionState state)
        {
            Debug.Log($"[{Role}] Session: {state}");                      // visibility until ConnectionUI surfaces this
            if (state == Session.SessionState.Established)
            {
                connectionLost = false;                                   // (re)connected — clear any prior loss overlay + resume the sim
                if (LevelManager.Instance != null) LevelManager.Instance.SimPaused = false;
                transport?.SetPingInterval(1.0);                          // INPUT/STATE keep liveness warm — restore DESIGN §8 PING cadence
                peerReady = false; localReady = false;                    // v2.3: a fresh (or re-established) session starts un-ready
                if (Role == GameRole.Hosting && LevelManager.Instance != null && LevelManager.Instance.CurrentLevelIndex < 0)
                {
                    // v2.3: hold in the lobby instead of the old auto-load; the game starts via
                    // StartGameFromLobby once the client readies (#104's resume became the lobby preselect,
                    // seeded in BeginHostingFromUi). Rejoin into a running game is untouched: index >= 0
                    // skips this and the WELCOME carried the live scene.
                    SendLobbyState();
                }
            }
            if (state != Session.SessionState.Disconnected) return;

            var reason = session != null ? session.LastDisconnect : Session.DisconnectReason.None;
            ClearBarrier();                                              // don't stay frozen waiting for an ack from a peer that just left
            session = null;
            transport = null;
            quality = null;
            condChannel = null;
            peerReady = false; localReady = false;                       // v2.3: a departed peer is not ready

            // A local Leave runs the full EndSessionFromUi teardown — nothing to pause or surface. A peer-initiated
            // drop (their GOODBYE / timeout / exhaustion), or a client that can't reach the host, pauses + surfaces
            // so the session stays resumable: the host keeps its level/pose/score and listens; the client can Rejoin.
            // (A host that merely rejected a bad HELLO just keeps listening silently.)
            // v2.3: a HOST losing its peer PRE-GAME doesn't surface the heavy "partner disconnected" overlay —
            // the lobby simply reverts to its waiting card while listening resumes below. The CLIENT still
            // surfaces (there is a host to Retry against).
            bool hostInLobby = Role == GameRole.Hosting && LevelManager.Instance != null
                               && LevelManager.Instance.CurrentLevelIndex < 0;
            // A partner who deliberately LEAVES mid-game (GOODBYE -> PeerLeft) returns the host to the lobby
            // with the current level preselected; a crash/timeout (ConnectionLost) keeps the overlay because
            // that path preserves the exact in-level state for a rejoin-resume.
            bool hostBackToLobby = Role == GameRole.Hosting && reason == Session.DisconnectReason.PeerLeft
                                   && LevelManager.Instance != null && LevelManager.Instance.CurrentLevelIndex >= 0;
            if (hostBackToLobby) ReturnHostToLobby();
            bool surface = reason != Session.DisconnectReason.LocalLeave
                           && !(Role == GameRole.Hosting && reason == Session.DisconnectReason.HandshakeFailed)
                           && !hostInLobby && !hostBackToLobby;
            if (surface)
            {
                connectionLost = true;
                lostReason = reason;
                if (LevelManager.Instance != null) LevelManager.Instance.SimPaused = true;
            }
            if (Role == GameRole.Hosting)
            {
                listening = true;                                        // keep listening so a rejoin resumes into the preserved level
                Debug.Log(surface ? "[Hosting] Partner disconnected — listening for rejoin..."
                                  : "[Hosting] Listening for a new client...");
            }
        }

        void OnClientWelcomeReceived(Welcome w)
        {
            if (w.PeerOwner != JumpNowBro.Util.InputOwner.P2)
                Debug.LogWarning($"[Client] WELCOME peerOwner={w.PeerOwner}, expected P2 — version skew?");
            // Identity: the client is P2; the host (P1) just told us its name/colour in the WELCOME (#114, #125).
            PlayerIdentity.Set(InputOwner.P1, w.Name, w.ColorIndex);
            PlayerIdentity.Set(InputOwner.P2, localPlayerName, localColorIndex);
            // Joining a finished game (0xFE): LoadByIndex fires the CompleteScreen but never sets the level
            // index, which would leave InLobby true and the lobby covering the victory screen — flag it so
            // InLobby stays false. Self-clearing: any later WELCOME (rejoin) overwrites it.
            clientJoinedPostVictory = w.CurrentSceneIndex == LevelManager.AllLevelsCompleteSentinel;
            // currentSceneIndex == 0xFF means host hasn't loaded yet; LoadByIndex is a no-op in that case
            // (the client sits in the lobby until the host's Start sends LEVEL_LOAD).
            LevelManager.Instance?.LoadByIndex(w.CurrentSceneIndex);
        }

        // Host side: the client (P2) just identified itself in its HELLO; the host is P1.
        void OnHostHelloReceived(Hello h)
        {
            PlayerIdentity.Set(InputOwner.P1, localPlayerName, localColorIndex);
            PlayerIdentity.Set(InputOwner.P2, h.Name, h.ColorIndex);
        }

        // ---- UI API ----

        public void BeginSoloFromUi()
        {
            if (Role != GameRole.SinglePlayer || session != null) return;
            soloActive = true;
            lastHostedLevelIndex = -1;          // a fresh Solo discards any pending host-resume (#104)
            PlayerIdentity.Reset();             // solo shows the P1/P2 labels with the default slot colours
            var lm = LevelManager.Instance;
            if (lm != null) lm.LoadByIndex(lm.PendingStartIndex);   // start at the menu's level pick (default 0)
        }

        public void BeginHostingFromUi(string lobbyName = null, string playerName = null)
        {
            if (Role != GameRole.SinglePlayer || session != null) return;
            if (!string.IsNullOrWhiteSpace(lobbyName)) gameName = lobbyName.Trim();   // beacon display name for LAN discovery
            localPlayerName = playerName ?? "";   // host's in-game display name (#114)
            localColorIndex = 0;                  // host = slot 0 (#125)

            // v2.3: the lobby PRESELECTS the remembered level (a re-host resumes with one Start click; an
            // auto-load would skip the ready handshake — deliberate change to #104's behavior), else the
            // menu's pick. Clamped: a stale post-victory index must not leak in as out-of-range.
            var lm = LevelManager.Instance;
            int seed = lastHostedLevelIndex >= 0 ? lastHostedLevelIndex
                     : lm != null ? lm.PendingStartIndex : 0;
            lobbySelectedLevel = Mathf.Clamp(seed, 0, lm != null ? Mathf.Max(0, lm.LevelCount - 1) : 0);
            lastHostedLevelIndex = -1;
            peerReady = localReady = false;

            Role = GameRole.Hosting;
            Application.runInBackground = true;
            try { BeginHosting(); }
            catch (System.Exception e) { Debug.LogError($"BeginHosting failed: {e.Message}"); EndSessionFromUi(); }
        }

        public void BeginClientFromUi(string hostIp, string playerName = null)
        {
            if (Role != GameRole.SinglePlayer || session != null) return;
            if (string.IsNullOrWhiteSpace(hostIp)) return;
            lastHostedLevelIndex = -1;          // joining as a client discards any pending host-resume (#104)
            localPlayerName = playerName ?? "";   // client's in-game display name (#114)
            localColorIndex = 1;                  // client = slot 1 (#125)
            Role = GameRole.Client;
            manualHostIp = hostIp;
            Application.runInBackground = true;
            try { BeginClient(); }
            catch (System.Exception e) { Debug.LogError($"BeginClient failed: {e.Message}"); EndSessionFromUi(); }
        }

        /// Client: reconnect to the same host after a connection loss, resuming into the host's current level via
        /// the WELCOME scene-index path. connectionLost + SimPaused stay set until the new session establishes (so a
        /// failed rejoin keeps the overlay up); a successful one clears them in OnSessionStateChanged(Established).
        public void RejoinFromUi()
        {
            if (Role != GameRole.Client || session != null) return;
            // Drop the stale client and force a fresh scene load on the rejoin's WELCOME/LEVEL_LOAD — the verified
            // Leave→Join mid-game-join path, minus the return to SinglePlayer. Without this, the old Player stays
            // bound to the dead transport (its sender/renderer never reach the new socket) and rejoin "connects"
            // but the character is frozen — especially against a re-hosted host (the #104 stale-wiring case).
            if (PlayerSpawner.Instance != null && PlayerSpawner.Instance.CurrentPlayerInstance != null)
                Destroy(PlayerSpawner.Instance.CurrentPlayerInstance);
            currentClientRenderer = null;
            LevelManager.Instance?.ResetIndex();
            discovery?.Dispose(); discovery = null;
            gameplaySocket?.Dispose(); gameplaySocket = null;            // dispose the stale socket before BeginClient opens a new one
            try { BeginClient(); }
            catch (System.Exception e) { Debug.LogError($"Rejoin failed: {e.Message}"); EndSessionFromUi(); }
        }

        public void EndSessionFromUi()
        {
            var s = session;
            if (s != null) { s.SendGoodbye(GoodbyeReason.Normal); s.Tick(0); }

            // Tear down the spawned Player BEFORE disposing the socket. ClientInputSender and
            // NetworkStateBroadcaster fire on every FixedUpdate; if they're still alive when the
            // socket goes away, they'd throw ObjectDisposedException on next Send. Destroying the
            // GameObject removes all of them in one stroke.
            if (PlayerSpawner.Instance != null && PlayerSpawner.Instance.CurrentPlayerInstance != null)
                Destroy(PlayerSpawner.Instance.CurrentPlayerInstance);
            currentHostRemote = null;
            currentClientRenderer = null;

            // Remember the host's current level so a Leave-then-Host resumes it (as the lobby preselect since
            // v2.3). ResetIndex below clears CurrentLevelIndex, so capture first; a fresh Solo/Join clears it
            // again. The < LevelCount guard keeps the post-victory index (== LevelCount) from leaking in: it
            // made the next re-host LoadByIndex(out-of-range) and error into a levelless session.
            if (Role == GameRole.Hosting && LevelManager.Instance != null
                && LevelManager.Instance.CurrentLevelIndex >= 0
                && LevelManager.Instance.CurrentLevelIndex < LevelManager.Instance.LevelCount)
                lastHostedLevelIndex = LevelManager.Instance.CurrentLevelIndex;
            peerReady = false; localReady = false;                       // v2.3: leaving abandons any ready state
            clientJoinedPostVictory = false;

            // Reset LevelManager's index so the next Solo/Host/Join isn't tricked into a no-op by
            // LoadByIndex's idempotence check (which keys on currentLevelIndex + currentlyLoadedScene).
            // currentlyLoadedScene stays so LoadLevelRoutine can yield on the unload before re-loading.
            LevelManager.Instance?.ResetIndex();
            if (LevelManager.Instance != null) LevelManager.Instance.SimPaused = false;   // never leak the connection-loss pause into the next session
            connectionLost = false;
            soloActive = false;

            // Clear the end-of-run UI that outlives the session: the persistent CompleteScreen overlay and the
            // level HUD labels. We do NOT unload the level scene here — a fire-and-forget unload from this path
            // raced the next load (see LevelManager.ResetIndex); the next session's load clears it. DeathNotifier
            // is zeroed silently (no OnDeath) so the next session's HUD starts at 0 without a teardown shake.
            CompleteScreen.Instance?.HidePanel();
            FindAnyObjectByType<LevelHud>()?.Clear();
            DeathNotifier.Instance?.Reset();
            PlayerSpawner.Instance?.ResetDeathAccumulation();                  // the folded total leaked across sessions otherwise
            PlayerIdentity.Reset();                                            // next session starts from the P1/P2 defaults
            GhostIntentSources.Reset();                                        // drop intent sources closing over the destroyed player (#124)

            session = null;
            transport = null;
            quality = null;
            condChannel = null;
            discovery?.Dispose(); discovery = null;
            gameplaySocket?.Dispose(); gameplaySocket = null;
            listening = false;
            Role = GameRole.SinglePlayer;
        }
    }
}
