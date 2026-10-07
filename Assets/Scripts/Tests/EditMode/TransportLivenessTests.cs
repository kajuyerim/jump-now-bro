using NUnit.Framework;
using JumpNowBro.Networking;

namespace JumpNowBro.Tests
{
    public class TransportLivenessTests
    {
        [Test]
        public void LevelBarrierTimeout_PrecedesTransportSilenceTimeout()
        {
            Assert.Less(NetworkTuning.LevelBarrierTimeoutSeconds, NetworkTuning.SilenceTimeoutSeconds);
        }

        [Test]
        public void Liveness_FiresAfterSilence()
        {
            var (ca, cb) = InMemoryDatagramChannel.Pair();
            var a = new UdpReliableTransport(ca, silenceTimeoutSeconds: 0.5);
            var b = new UdpReliableTransport(cb);
            bool aDown = false;
            a.OnDisconnected += () => aDown = true;

            b.Send(Channel.Unreliable, MessageType.State, new byte[] { 1 });    // arm a's liveness
            for (int i = 0; i < 3; i++) { a.Tick(0.05f); b.Tick(0.05f); }
            Assert.IsFalse(aDown, "alive right after receiving");

            for (int i = 0; i < 20; i++) a.Tick(0.05f);                         // b goes silent; 1.0 s > 0.5 s timeout
            Assert.IsTrue(aDown);
        }

        [Test]
        public void Liveness_StaysAliveWhileTrafficFlows()
        {
            var (ca, cb) = InMemoryDatagramChannel.Pair();
            var a = new UdpReliableTransport(ca, silenceTimeoutSeconds: 0.5);
            var b = new UdpReliableTransport(cb);
            bool aDown = false;
            a.OnDisconnected += () => aDown = true;

            for (int i = 0; i < 60; i++)                                        // 3 s of continuous traffic
            {
                b.Send(Channel.Unreliable, MessageType.State, new byte[] { (byte)i });
                a.Tick(0.05f);
                b.Tick(0.05f);
            }
            Assert.IsFalse(aDown);
        }

        [Test]
        public void FastKeepalive_AloneHoldsLiveness()
        {
            var (ca, cb) = InMemoryDatagramChannel.Pair();
            var a = new UdpReliableTransport(ca, pingIntervalSeconds: 0.05, silenceTimeoutSeconds: 0.5);
            var b = new UdpReliableTransport(cb, pingIntervalSeconds: 0.05);
            bool aDown = false;
            a.OnDisconnected += () => aDown = true;

            // no app traffic at all — only the keepalive PING/PONG flows, fast enough to hold the 0.5 s timer
            for (int i = 0; i < 60; i++) { a.Tick(0.05f); b.Tick(0.05f); }
            Assert.IsFalse(aDown, "fast keepalive should hold the link open with no app traffic");
        }
    }
}
