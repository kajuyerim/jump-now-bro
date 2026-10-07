using System;
using System.Net;
using System.Text;
using System.Threading;
using System.Diagnostics;
using NUnit.Framework;
using JumpNowBro.Networking;

namespace JumpNowBro.Tests
{
    public class UdpSocketTests
    {
        [Test]
        public void SendReceive_OverLoopback_DeliversDatagramAndSender()
        {
            using var a = new UdpSocket(0);
            using var b = new UdpSocket(0);

            var payload = Encoding.ASCII.GetBytes("hello-udp");
            a.Send(payload, new IPEndPoint(IPAddress.Loopback, b.LocalPort));

            // Receive runs on a background thread, so drain with a bounded retry rather than a single Poll.
            byte[] got = null;
            IPEndPoint from = null;
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                if (b.Poll(out got, out from)) break;
                Thread.Sleep(5);
            }

            Assert.IsNotNull(got, "datagram not received within timeout");
            Assert.AreEqual(payload, got);
            Assert.AreEqual(a.LocalPort, from.Port);
        }

        [Test]
        public void Poll_WhenEmpty_ReturnsFalse()
        {
            using var s = new UdpSocket(0);
            Assert.IsFalse(s.Poll(out _, out _));
        }

        [TestCase(8)]
        [TestCase(NetworkTuning.GameplayQueueCapacity)]
        public void BoundedReceiveQueue_DropsOverflowAndAcceptsAfterDrain(int capacity)
        {
            using var receiver = capacity == NetworkTuning.GameplayQueueCapacity
                ? new UdpSocket(0) : new UdpSocket(0, maxQueuedDatagrams: capacity);
            using var sender = new UdpSocket(0);
            var endpoint = new IPEndPoint(IPAddress.Loopback, receiver.LocalPort);
            for (int i = 0; i < capacity; i++)
            {
                sender.Send(new byte[] { 1 }, endpoint);
                Assert.IsTrue(SpinWait.SpinUntil(() => receiver.QueuedDatagramCount == i + 1, 2000));
            }
            for (int i = 0; i < 16; i++)
            {
                sender.Send(new byte[] { 1 }, endpoint);
                Assert.IsTrue(SpinWait.SpinUntil(() => receiver.DroppedDatagramCount == i + 1, 2000));
            }
            Assert.AreEqual(capacity, receiver.QueuedDatagramCount);
            int drained = 0;
            while (receiver.Poll(out var data, out _))
            {
                Assert.AreEqual(new byte[] { 1 }, data);
                drained++;
            }
            Assert.AreEqual(capacity, drained);

            sender.Send(new byte[] { 2 }, endpoint);
            Assert.IsTrue(SpinWait.SpinUntil(() => receiver.QueuedDatagramCount == 1, 2000));
            Assert.IsTrue(receiver.Poll(out var next, out _));
            Assert.AreEqual(new byte[] { 2 }, next);
        }

        [Test]
        public void DefaultReceiveQueue_DoesNotApplyDiscoveryCap()
        {
            using var receiver = new UdpSocket(0);
            using var sender = new UdpSocket(0);
            var endpoint = new IPEndPoint(IPAddress.Loopback, receiver.LocalPort);
            for (int i = 0; i <= DiscoveryService.MaxQueuedDatagrams; i++)
            {
                sender.Send(new byte[] { 1 }, endpoint);
                Assert.IsTrue(SpinWait.SpinUntil(() => receiver.QueuedDatagramCount == i + 1, 2000));
            }
            Assert.AreEqual(0, receiver.DroppedDatagramCount);
        }

        [Test]
        public void NegativeReceiveQueueLimit_IsRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new UdpSocket(0, maxQueuedDatagrams: -1));
        }

        [Test]
        public void Dispose_IsCleanAndIdempotent()
        {
            var s = new UdpSocket(0);
            Assert.DoesNotThrow(() => s.Dispose());
            Assert.DoesNotThrow(() => s.Dispose());
        }

        [Test, Timeout(5000)]
        public void RepeatedIdleAndBusySockets_DisposePromptly()
        {
            var stopwatch = Stopwatch.StartNew();
            // Four pairs can take up to two seconds of receive timeouts; leave scheduling headroom.
            for (int i = 0; i < 4; i++)
            {
                var receiver = new UdpSocket(0);
                var sender = new UdpSocket(0);
                try
                {
                    sender.Send(new byte[] { 1 }, new IPEndPoint(IPAddress.Loopback, receiver.LocalPort));
                }
                finally
                {
                    sender.Dispose();
                    receiver.Dispose();
                }
            }

            Assert.Less(stopwatch.ElapsedMilliseconds, 3000,
                "repeated socket shutdown should not accumulate blocked receive waits");
        }
    }
}
