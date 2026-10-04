using System.Collections;
using System.Net;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;
using JumpNowBro.Networking;

namespace JumpNowBro.Tests.PlayMode
{
    /// PlayMode coverage for #168: discovered gameplay endpoints must survive the UI join path and rejoin,
    /// while manual IP entry continues to use NetworkManager's configured default gameplay port.
    public class DiscoveredHostJoinTests
    {
        NetworkManager client;
        MainMenuUI menu;
        UdpHost advertisedHost;
        UdpHost defaultHost;
        DiscoveryService browse;
        ushort previousGameplayPort;
        ushort previousDiscoveryPort;
        string previousManualHostIp;
        ushort? previousClientHostPort;
        bool fieldsSaved;
        bool previousRunInBackground;

        [UnitySetUp]
        public IEnumerator SetUp()
        {
            advertisedHost = null;
            defaultHost = null;
            browse = null;
            fieldsSaved = false;
            if (NetworkManager.Instance == null)
            {
                yield return SceneManager.LoadSceneAsync("Bootstrap", LoadSceneMode.Single);
                yield return null;
            }
            client = NetworkManager.Instance;
            menu = Object.FindAnyObjectByType<MainMenuUI>();
            Assert.IsNotNull(client, "#168 PlayMode tests require the Bootstrap NetworkManager");
            Assert.IsNotNull(menu, "#168 PlayMode tests require the Bootstrap MainMenuUI");

            if (client.Role != GameRole.SinglePlayer || client.CurrentSessionState != null)
                client.EndSessionFromUi();
            yield return null;

            previousGameplayPort = Get<ushort>(client, "gameplayPort");
            previousDiscoveryPort = Get<ushort>(client, "discoveryPort");
            previousManualHostIp = Get<string>(client, "manualHostIp");
            previousClientHostPort = GetNullableUShort(client, "clientHostPort");
            previousRunInBackground = Application.runInBackground;
            fieldsSaved = true;
            Invoke(menu, "DisposeBrowse");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            try
            {
                if (menu != null) Invoke(menu, "DisposeBrowse");
                if (client != null && (client.Role != GameRole.SinglePlayer || client.CurrentSessionState != null))
                    client.EndSessionFromUi();
                advertisedHost?.Dispose();
                defaultHost?.Dispose();
                browse?.Dispose();
                yield return null;
            }
            finally
            {
                // Update may reopen the browser while teardown yields; close that temporary listener too.
                if (menu != null) Invoke(menu, "DisposeBrowse");
                if (client != null && fieldsSaved)
                {
                    Set(client, "gameplayPort", previousGameplayPort);
                    Set(client, "discoveryPort", previousDiscoveryPort);
                    Set(client, "manualHostIp", previousManualHostIp);
                    Set(client, "clientHostPort", previousClientHostPort);
                    Application.runInBackground = previousRunInBackground;
                }
            }
        }

        [UnityTest]
        public IEnumerator DiscoveredButtonJoinsAdvertisedPort_ManualJoinUsesDefault_RejoinRetainsPort()
        {
            advertisedHost = new UdpHost();
            defaultHost = new UdpHost();
            Set(client, "gameplayPort", defaultHost.Port);
            Set(client, "discoveryPort", (ushort)0);

            browse = DiscoveryService.StartClient(0);
            Set(menu, "browse", browse);
            advertisedHost.Advertise(Get<UdpSocket>(browse, "socket").LocalPort);
            float discoveryDeadline = Time.realtimeSinceStartup + 3f;
            while (browse.Hosts.Count == 0 && Time.realtimeSinceStartup < discoveryDeadline)
            {
                browse.Tick(Time.timeAsDouble);
                yield return null;
            }
            Assert.AreEqual(1, browse.Hosts.Count, "the real discovery listener must receive the host beacon");
            Invoke(menu, "RefreshHosts");

            var hostList = Get<GameObject>(menu, "hostList");
            var button = hostList.GetComponentInChildren<Button>();
            Assert.IsNotNull(button, "RefreshHosts should generate a discovered-host button");
            button.onClick.Invoke();
            yield return WaitForEstablished(advertisedHost, 8f);
            Assert.AreEqual(Session.SessionState.Established, client.CurrentSessionState,
                "the generated button must connect to the beacon's gameplay port");
            Assert.AreEqual(advertisedHost.Port, GetNullableUShort(client, "clientHostPort"));

            // Leave the discovered session, then use the ordinary manual path. Its null override must fall back
            // to the configured local gameplayPort (the second real host socket).
            client.EndSessionFromUi();
            advertisedHost.ResetSession();
            yield return null;
            client.BeginClientFromUi(IPAddress.Loopback.ToString(), "manual-client");
            yield return WaitForEstablished(defaultHost, 8f);
            Assert.AreEqual(Session.SessionState.Established, client.CurrentSessionState,
                "manual IP entry must use the configured default gameplay port");
            Assert.IsNull(GetNullableUShort(client, "clientHostPort"));

            // Start a discovered-port session again, then make the host send a peer GOODBYE. RejoinFromUi must
            // keep the endpoint override from the discovered join when it creates the replacement transport.
            client.EndSessionFromUi();
            defaultHost.ResetSession();
            yield return null;
            client.BeginClientFromUi(IPAddress.Loopback.ToString(), "discovered-again", advertisedHost.Port);
            yield return WaitForEstablished(advertisedHost, 8f);
            Assert.AreEqual(Session.SessionState.Established, client.CurrentSessionState);
            advertisedHost.SendGoodbye();
            yield return WaitForConnectionLost(advertisedHost, 8f);
            Assert.IsTrue(client.ConnectionLost, "a peer goodbye should expose the resumable client state");

            advertisedHost.ResetSession();
            client.RejoinFromUi();
            yield return WaitForEstablished(advertisedHost, 8f);
            Assert.AreEqual(Session.SessionState.Established, client.CurrentSessionState,
                "rejoin must retain and redial the discovered gameplay endpoint");
            Assert.AreEqual(advertisedHost.Port, GetNullableUShort(client, "clientHostPort"));
        }

        [UnityTest]
        public IEnumerator DiscoveryMenu_ListsSeveralHosts_AndStaysBoundedDuringFlood()
        {
            Set(client, "discoveryPort", (ushort)0);
            browse = DiscoveryService.StartClient(0);
            Set(menu, "browse", browse);
            var receiver = Get<UdpSocket>(browse, "socket");
            using var sender = new UdpSocket(0);
            var endpoint = new IPEndPoint(IPAddress.Loopback, receiver.LocalPort);
            var data = new byte[64];
            for (ushort port = 10000; port < 10003; port++) SendBeacon(sender, endpoint, data, port);
            float deadline = Time.realtimeSinceStartup + 3f;
            while (browse.Hosts.Count < 3 && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.AreEqual(3, browse.Hosts.Count);
            Invoke(menu, "RefreshHosts");
            yield return null;
            var hostList = Get<GameObject>(menu, "hostList");
            Assert.AreEqual(3, hostList.GetComponentsInChildren<Button>().Length);

            for (int frame = 0; frame < 12; frame++)
            {
                for (int i = 0; i < 256; i++)
                    SendBeacon(sender, endpoint, data, (ushort)(11000 + frame * 256 + i));
                yield return null;
                Assert.LessOrEqual(browse.Hosts.Count, DiscoveredHosts.MaxHosts);
                Assert.LessOrEqual(receiver.QueuedDatagramCount, DiscoveryService.MaxQueuedDatagrams);
            }
            Assert.AreEqual(DiscoveredHosts.MaxHosts, browse.Hosts.Count);
            Assert.Greater(receiver.DroppedDatagramCount, 0, "the burst should exercise queue overflow");
            Invoke(menu, "RefreshHosts");
            yield return null; // Unity removes the previous buttons at the end of the frame.
            Assert.AreEqual(DiscoveredHosts.MaxHosts, hostList.GetComponentsInChildren<Button>().Length);
        }

        static void SendBeacon(UdpSocket sender, IPEndPoint endpoint, byte[] data, ushort port)
        {
            int size = new LanBeacon { Magic = SessionProtocol.Magic, GameName = "lan-host", GameplayPort = port }.Write(data);
            sender.Send(new System.ReadOnlySpan<byte>(data, 0, size), endpoint);
        }

        IEnumerator WaitForEstablished(UdpHost host, float timeoutSeconds)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (Time.realtimeSinceStartup < deadline
                   && client.CurrentSessionState != Session.SessionState.Established)
            {
                host.Pump(0.02f);
                yield return null;
            }
        }

        IEnumerator WaitForConnectionLost(UdpHost host, float timeoutSeconds)
        {
            float deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (Time.realtimeSinceStartup < deadline && !client.ConnectionLost)
            {
                host.Pump(0.02f);
                yield return null;
            }
        }

        sealed class UdpHost
        {
            readonly UdpSocket socket;
            Session session;

            public ushort Port => (ushort)socket.LocalPort;

            public UdpHost() => socket = new UdpSocket(0);

            public void Advertise(int discoveryPort)
            {
                var beacon = new LanBeacon
                {
                    Magic = SessionProtocol.Magic, GameName = "test-host", GameplayPort = Port
                };
                var data = new byte[256];
                int size = beacon.Write(data);
                socket.Send(new System.ReadOnlySpan<byte>(data, 0, size), new IPEndPoint(IPAddress.Loopback, discoveryPort));
            }

            public void Pump(float dt)
            {
                if (session == null)
                {
                    while (socket.Poll(out var datagram, out var from))
                    {
                        if (!SessionProtocol.IsValidHello(datagram)) continue;
                        var channel = new UdpDatagramChannel(socket, from);
                        channel.PreSeed(datagram);
                        session = new Session(new UdpReliableTransport(channel), isHost: true,
                            sceneIndexProvider: () => 0xFF);
                        session.Start();
                        break;
                    }
                }
                session?.Tick(dt);
            }

            public void SendGoodbye()
            {
                Assert.IsNotNull(session, "host must be connected before sending GOODBYE");
                session.SendGoodbye(GoodbyeReason.Normal);
                session.Tick(0f);
            }

            public void ResetSession() => session = null;

            public void Dispose() => socket.Dispose();
        }

        static T Get<T>(object target, string field) =>
            (T)target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target);

        static ushort? GetNullableUShort(object target, string field)
        {
            var value = target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target);
            return value == null ? (ushort?)null : (ushort)value;
        }

        static void Set(object target, string field, object value) =>
            target.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(target, value);

        static void Invoke(object target, string method) =>
            target.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(target, null);
    }
}
