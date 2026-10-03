using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using NUnit.Framework;
using JumpNowBro.Networking;

namespace JumpNowBro.Tests
{
    public class UdpDatagramChannelTests
    {
        // Receive runs on a background thread, so poll with a bounded retry rather than a single call.
        static byte[] DrainWithRetry(IDatagramChannel ch, double seconds = 2)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline)
            {
                if (ch.TryReceive(out var dg)) return dg;
                Thread.Sleep(5);
            }
            return null;
        }

        static IPEndPoint Loopback(UdpSocket s) => new IPEndPoint(IPAddress.Loopback, s.LocalPort);

        [Test]
        public void RoundTrips_OverLoopback()
        {
            using var hostSock = new UdpSocket(0);
            using var clientSock = new UdpSocket(0);
            var hostCh = new UdpDatagramChannel(hostSock, Loopback(clientSock));
            var clientCh = new UdpDatagramChannel(clientSock, Loopback(hostSock));

            clientCh.Send(new byte[] { 1, 2, 3 });
            var got = DrainWithRetry(hostCh);
            Assert.IsNotNull(got, "host did not receive the peer's datagram");
            Assert.AreEqual(new byte[] { 1, 2, 3 }, got);
        }

        [Test, Timeout(5000)]
        public void Inbound_FromNonPeer_IsDropped()
        {
            using var hostSock = new UdpSocket(0);
            using var clientSock = new UdpSocket(0);
            using var strangerSock = new UdpSocket(0);
            var hostCh = new UdpDatagramChannel(hostSock, Loopback(clientSock));   // host's peer = client
            var hostEp = Loopback(hostSock);

            strangerSock.Send(new byte[] { 9, 9 }, hostEp);        // junk from a non-peer
            clientSock.Send(new byte[] { 1, 2, 3 }, hostEp);       // the real datagram

            // Collect everything the channel surfaces over a window: only the peer's datagram should appear.
            var received = new List<byte[]>();
            var deadline = DateTime.UtcNow.AddSeconds(1);
            while (DateTime.UtcNow < deadline)
            {
                if (hostCh.TryReceive(out var dg)) received.Add(dg);
                else Thread.Sleep(5);
            }

            Assert.AreEqual(1, received.Count, "only the bound peer's datagram should surface");
            Assert.AreEqual(new byte[] { 1, 2, 3 }, received[0]);
        }

        [Test]
        public void PreSeed_SurfacesBeforeSocketTraffic()
        {
            using var sock = new UdpSocket(0);
            var ch = new UdpDatagramChannel(sock, new IPEndPoint(IPAddress.Loopback, 9));   // peer irrelevant here

            ch.PreSeed(new byte[] { 7, 7, 7 });
            Assert.IsTrue(ch.TryReceive(out var dg));
            Assert.AreEqual(new byte[] { 7, 7, 7 }, dg);
            Assert.IsFalse(ch.TryReceive(out _));      // nothing else queued
        }
    }
}
