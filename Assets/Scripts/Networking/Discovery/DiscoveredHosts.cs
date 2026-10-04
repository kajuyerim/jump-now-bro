using System.Collections.Generic;
using System.Net;

namespace JumpNowBro.Networking
{
    /// The client's view of hosts heard on the LAN: deduped by gameplay endpoint, expired when a host
    /// stops beaconing. Pure logic (no sockets) so it's unit-testable.
    public sealed class DiscoveredHosts
    {
        public const int MaxHosts = 32;

        public readonly struct Host
        {
            public readonly IPEndPoint Endpoint;
            public readonly string Name;
            public readonly double LastSeen;
            public Host(IPEndPoint endpoint, string name, double lastSeen) { Endpoint = endpoint; Name = name; LastSeen = lastSeen; }
        }

        readonly Dictionary<string, Host> hosts = new Dictionary<string, Host>();

        public int Count => hosts.Count;
        public IEnumerable<Host> Hosts => hosts.Values;

        public void Observe(IPEndPoint gameplayEndpoint, string name, double now)
        {
            string key = gameplayEndpoint.ToString();
            if (!hosts.ContainsKey(key) && hosts.Count == MaxHosts)
            {
                // Prefer recently heard hosts; break same-tick ties without dictionary-order dependence.
                string oldest = null;
                double oldestTime = double.PositiveInfinity;
                foreach (var kv in hosts)
                    if (oldest == null || kv.Value.LastSeen < oldestTime ||
                        (kv.Value.LastSeen == oldestTime && string.CompareOrdinal(kv.Key, oldest) < 0))
                    {
                        oldest = kv.Key;
                        oldestTime = kv.Value.LastSeen;
                    }
                hosts.Remove(oldest);
            }
            hosts[key] = new Host(gameplayEndpoint, name, now);
        }

        public void Expire(double now, double ttlSeconds)
        {
            List<string> stale = null;
            foreach (var kv in hosts)
                if (now - kv.Value.LastSeen > ttlSeconds) (stale ??= new List<string>()).Add(kv.Key);
            if (stale != null) foreach (var k in stale) hosts.Remove(k);
        }
    }
}
