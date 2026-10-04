using System;
using System.Net;

namespace JumpNowBro.Networking
{
    /// Host beacon broadcaster + client listener over the discovery port. Engine-free; pumped by Tick(now)
    /// from the network manager. Broadcast is BEST-EFFORT — many networks (phone hotspots) block it via
    /// AP/client isolation, so manual IP entry is the reliable path. The broadcast send is wrapped so a
    /// blocked/no-route broadcast can't crash the loop.
    public sealed class DiscoveryService : IDisposable
    {
        const double BeaconIntervalSeconds = 1.0;
        const double HostTtlSeconds = 4.0;
        public const int MaxDatagramsPerTick = 64;
        public const int MaxQueuedDatagrams = 128;

        readonly UdpSocket socket;
        readonly int discoveryPort;
        readonly bool isHost;
        readonly LanBeacon beacon;
        readonly byte[] scratch = new byte[256];
        double lastBeaconAt = double.NegativeInfinity;

        public DiscoveredHosts Hosts { get; } = new DiscoveredHosts();

        DiscoveryService(int discoveryPort, bool isHost, LanBeacon beacon)
        {
            this.discoveryPort = discoveryPort;
            this.isHost = isHost;
            this.beacon = beacon;
            socket = new UdpSocket(discoveryPort, broadcast: true, maxQueuedDatagrams: MaxQueuedDatagrams);
        }

        public static DiscoveryService StartHost(int discoveryPort, LanBeacon beacon) => new DiscoveryService(discoveryPort, true, beacon);
        public static DiscoveryService StartClient(int discoveryPort) => new DiscoveryService(discoveryPort, false, default);

        public void Tick(double now)
        {
            if (isHost)
            {
                if (now - lastBeaconAt >= BeaconIntervalSeconds) { Broadcast(); lastBeaconAt = now; }
            }
            Hosts.Expire(now, HostTtlSeconds);
            // Hosts discard incoming beacons too; their broadcast socket also receives LAN traffic.
            for (int i = 0; i < MaxDatagramsPerTick && socket.Poll(out var data, out var from); i++)
                if (!isHost && LanBeacon.TryRead(data, out var b) && b.Magic == SessionProtocol.Magic)
                    Hosts.Observe(new IPEndPoint(from.Address, b.GameplayPort), b.GameName, now);
        }

        void Broadcast()
        {
            int n = beacon.Write(scratch);
            try { socket.Send(scratch.AsSpan(0, n), new IPEndPoint(IPAddress.Broadcast, discoveryPort)); }
            catch { /* best-effort: a blocked/no-route broadcast must not crash the pump */ }
        }

        public void Dispose() => socket.Dispose();
    }
}
