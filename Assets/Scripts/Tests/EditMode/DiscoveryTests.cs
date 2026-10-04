using System;
using System.Net;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using JumpNowBro.Networking;

namespace JumpNowBro.Tests
{
    public class DiscoveryTests
    {
        static bool ContainsPort(DiscoveredHosts hosts, int port)
        {
            foreach (var host in hosts.Hosts)
                if (host.Endpoint.Port == port) return true;
            return false;
        }

        static DiscoveredHosts.Host FindPort(DiscoveredHosts hosts, int port)
        {
            foreach (var host in hosts.Hosts)
                if (host.Endpoint.Port == port) return host;
            return default;
        }

        [Test]
        public void LanBeacon_RoundTrips()
        {
            var buf = new byte[64];
            int n = new LanBeacon { Magic = SessionProtocol.Magic, GameName = "Kerem's game", GameplayPort = 7777 }.Write(buf);
            Assert.IsTrue(LanBeacon.TryRead(buf.AsSpan(0, n), out var b));
            Assert.AreEqual(SessionProtocol.Magic, b.Magic);
            Assert.AreEqual("Kerem's game", b.GameName);
            Assert.AreEqual(7777, b.GameplayPort);
        }

        [Test]
        public void DiscoveredHosts_DedupsByEndpoint()
        {
            var hosts = new DiscoveredHosts();
            var ep = new IPEndPoint(IPAddress.Parse("192.168.1.5"), 7777);
            hosts.Observe(ep, "A", 1.0);
            hosts.Observe(ep, "A", 2.0);                                              // same endpoint → still one
            hosts.Observe(new IPEndPoint(IPAddress.Parse("192.168.1.6"), 7777), "B", 2.0);
            Assert.AreEqual(2, hosts.Count);
        }

        [Test]
        public void DiscoveredHosts_CapsEntriesAtMaxHosts()
        {
            var hosts = new DiscoveredHosts();
            for (int i = 0; i < DiscoveredHosts.MaxHosts + 8; i++)
                hosts.Observe(new IPEndPoint(IPAddress.Loopback, 10_000 + i), "host-" + i, i);

            Assert.AreEqual(DiscoveredHosts.MaxHosts, hosts.Count);
            Assert.IsFalse(ContainsPort(hosts, 10_000));
            Assert.IsTrue(ContainsPort(hosts, 10_000 + DiscoveredHosts.MaxHosts + 7));
        }

        [Test]
        public void DiscoveredHosts_EvictsLeastRecent_AndKeepsRefreshedHost()
        {
            var hosts = new DiscoveredHosts();
            for (int i = 0; i < DiscoveredHosts.MaxHosts; i++)
                hosts.Observe(new IPEndPoint(IPAddress.Loopback, 11_000 + i), "host-" + i, i);

            hosts.Observe(new IPEndPoint(IPAddress.Loopback, 11_000), "refreshed", 100.0);
            hosts.Observe(new IPEndPoint(IPAddress.Loopback, 12_000), "new", 101.0);

            Assert.AreEqual(DiscoveredHosts.MaxHosts, hosts.Count);
            Assert.IsFalse(ContainsPort(hosts, 11_001));
            Assert.IsTrue(ContainsPort(hosts, 11_000));
            Assert.AreEqual("refreshed", FindPort(hosts, 11_000).Name);
            Assert.IsTrue(ContainsPort(hosts, 12_000));
        }

        [Test]
        public void DiscoveredHosts_EqualLastSeen_EvictsOrdinallySmallestEndpoint()
        {
            var hosts = new DiscoveredHosts();
            for (int i = 0; i < DiscoveredHosts.MaxHosts; i++)
                hosts.Observe(new IPEndPoint(IPAddress.Loopback, 13_000 + i), "host-" + i, 1.0);

            hosts.Observe(new IPEndPoint(IPAddress.Loopback, 14_000), "new", 1.0);

            Assert.AreEqual(DiscoveredHosts.MaxHosts, hosts.Count);
            Assert.IsFalse(ContainsPort(hosts, 13_000));
            Assert.IsTrue(ContainsPort(hosts, 13_001));
            Assert.IsTrue(ContainsPort(hosts, 14_000));
        }

        [Test]
        public void DiscoveredHosts_ExpiresStale()
        {
            var hosts = new DiscoveredHosts();
            hosts.Observe(new IPEndPoint(IPAddress.Parse("192.168.1.5"), 7777), "A", 1.0);   // last seen 1.0
            hosts.Observe(new IPEndPoint(IPAddress.Parse("192.168.1.6"), 7777), "B", 5.0);   // last seen 5.0
            hosts.Expire(now: 6.0, ttlSeconds: 4.0);                                         // A is 5s stale → dropped; B (1s) kept
            Assert.AreEqual(1, hosts.Count);
        }

        [Test]
        public void DiscoveredHosts_ExpiryAtExactTtl_IsNotStale()
        {
            var hosts = new DiscoveredHosts();
            hosts.Observe(new IPEndPoint(IPAddress.Loopback, 15_000), "A", 1.0);

            hosts.Expire(now: 5.0, ttlSeconds: 4.0);
            Assert.AreEqual(1, hosts.Count);

            hosts.Expire(now: 5.001, ttlSeconds: 4.0);
            Assert.AreEqual(0, hosts.Count);
        }

        [Test]
        public void BroadcastSocket_Constructs()
        {
            // exercises the broadcast ctor branch (unbound → SO_REUSEADDR → bind → EnableBroadcast)
            Assert.DoesNotThrow(() => { using var s = new UdpSocket(0, broadcast: true); });
        }

        static UdpSocket SocketOf(DiscoveryService discovery) =>
            (UdpSocket)typeof(DiscoveryService).GetField("socket", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(discovery);

        static void QueueDatagram(UdpSocket sender, UdpSocket receiver, byte[] data)
        {
            int queued = receiver.QueuedDatagramCount;
            sender.Send(data, new IPEndPoint(IPAddress.Loopback, receiver.LocalPort));
            Assert.IsTrue(SpinWait.SpinUntil(() => receiver.QueuedDatagramCount == queued + 1, 2000),
                "loopback datagram did not reach the discovery queue");
        }

        static byte[] BeaconBytes(uint magic = SessionProtocol.Magic)
        {
            var data = new byte[64];
            int size = new LanBeacon { Magic = magic, GameName = "local-host", GameplayPort = 7777 }.Write(data);
            return data.AsSpan(0, size).ToArray();
        }

        [Test]
        public void ClientTick_BudgetIncludesMalformedAndForeignDatagrams()
        {
            using var discovery = DiscoveryService.StartClient(0);
            using var sender = new UdpSocket(0);
            var receiver = SocketOf(discovery);
            for (int i = 0; i < DiscoveryService.MaxDatagramsPerTick - 1; i++)
                QueueDatagram(sender, receiver, new byte[] { 0 });
            QueueDatagram(sender, receiver, BeaconBytes(0));
            QueueDatagram(sender, receiver, BeaconBytes());

            discovery.Tick(1.0);
            Assert.AreEqual(0, discovery.Hosts.Count);
            Assert.AreEqual(1, receiver.QueuedDatagramCount, "the valid beacon must wait for the next tick");
            discovery.Tick(1.0);
            Assert.AreEqual(1, discovery.Hosts.Count);
            Assert.AreEqual(0, receiver.QueuedDatagramCount);
        }

        [Test]
        public void HostTick_DiscardsInboundTrafficWithinBudget()
        {
            using var discovery = DiscoveryService.StartHost(0,
                new LanBeacon { Magic = SessionProtocol.Magic, GameName = "host", GameplayPort = 7777 });
            using var sender = new UdpSocket(0);
            var receiver = SocketOf(discovery);
            for (int i = 0; i <= DiscoveryService.MaxDatagramsPerTick; i++)
                QueueDatagram(sender, receiver, BeaconBytes());

            discovery.Tick(1.0);
            Assert.AreEqual(1, receiver.QueuedDatagramCount);
            Assert.AreEqual(0, discovery.Hosts.Count, "a host does not browse incoming advertisements");
            discovery.Tick(1.0);
            Assert.AreEqual(0, receiver.QueuedDatagramCount);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DiscoverySocket_BoundsQueueEvenWithoutTicks(bool isHost)
        {
            using var discovery = isHost
                ? DiscoveryService.StartHost(0, default)
                : DiscoveryService.StartClient(0);
            using var sender = new UdpSocket(0);
            var receiver = SocketOf(discovery);
            for (int i = 0; i < DiscoveryService.MaxQueuedDatagrams; i++)
                QueueDatagram(sender, receiver, BeaconBytes());
            for (int i = 0; i < 16; i++)
            {
                sender.Send(BeaconBytes(), new IPEndPoint(IPAddress.Loopback, receiver.LocalPort));
                Assert.IsTrue(SpinWait.SpinUntil(() => receiver.DroppedDatagramCount == i + 1, 2000));
            }
            Assert.AreEqual(DiscoveryService.MaxQueuedDatagrams, receiver.QueuedDatagramCount);
        }
    }
}
