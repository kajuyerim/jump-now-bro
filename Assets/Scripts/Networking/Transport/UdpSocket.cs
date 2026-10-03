using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace JumpNowBro.Networking
{
    /// Raw UDP datagram pipe. The blocking receive runs on a background thread and enqueues
    /// datagrams; the main thread drains them with Poll (Unity APIs are main-thread-only).
    /// Protocol-agnostic — sequencing, acks, and headers live in the layer above.
    public sealed class UdpSocket : IDisposable
    {
        readonly struct Datagram
        {
            public readonly byte[] Data;
            public readonly IPEndPoint From;
            public Datagram(byte[] data, IPEndPoint from) { Data = data; From = from; }
        }

        readonly UdpClient client;
        readonly Thread receiveThread;
        readonly ConcurrentQueue<Datagram> inbox = new ConcurrentQueue<Datagram>();
        volatile bool running;
        bool disposed;

        public int LocalPort { get; }

        // broadcast=true (discovery socket): permit sending to 255.255.255.255, and SO_REUSEADDR so a host
        // and client on the SAME machine can both bind the discovery port. The gameplay socket leaves both
        // OFF — SO_REUSEADDR there would let a second host bind the gameplay port and steal packets.
        public UdpSocket(int bindPort, bool broadcast = false)
        {
            if (broadcast)
            {
                client = new UdpClient();
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                client.Client.Bind(new IPEndPoint(IPAddress.Any, bindPort));
                client.EnableBroadcast = true;
            }
            else
            {
                client = new UdpClient(new IPEndPoint(IPAddress.Any, bindPort));
            }
            // Closing a socket from another thread does not reliably interrupt Receive on every platform.
            // The timeout gives the loop a portable chance to observe running=false during shutdown.
            client.Client.ReceiveTimeout = 250;
            LocalPort = ((IPEndPoint)client.Client.LocalEndPoint).Port;

            running = true;
            receiveThread = new Thread(ReceiveLoop) { IsBackground = true, Name = $"UdpSocket:{LocalPort}" };
            receiveThread.Start();
        }

        public void Send(ReadOnlySpan<byte> data, IPEndPoint to)
        {
            // Unity's .NET Standard 2.1 has no Span-based SendTo, so copy into a byte[] for the send.
            byte[] buffer = data.ToArray();
            client.Send(buffer, buffer.Length, to);
        }

        /// Pop the next received datagram (FIFO); returns false when none are queued. Main thread only.
        public bool Poll(out byte[] data, out IPEndPoint from)
        {
            if (inbox.TryDequeue(out var dg))
            {
                data = dg.Data;
                from = dg.From;
                return true;
            }
            data = null;
            from = null;
            return false;
        }

        void ReceiveLoop()
        {
            var any = new IPEndPoint(IPAddress.Any, 0);   // overwritten with the sender each Receive
            while (running)
            {
                try
                {
                    byte[] data = client.Receive(ref any);   // blocks; returns a right-sized array
                    inbox.Enqueue(new Datagram(data, new IPEndPoint(any.Address, any.Port)));
                }
                catch (SocketException) when (running)
                {
                    // Transient (e.g. ICMP port-unreachable surfacing as a reset); keep receiving.
                }
                catch
                {
                    break;   // socket closed during Dispose, or unrecoverable -> end the thread.
                }
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            running = false;
            try { receiveThread.Join(500); } catch { }  // let ReceiveTimeout observe running=false first
            try { client.Close(); } catch { }           // fallback for a receive still blocked in native code
            try { receiveThread.Join(500); } catch { }  // final bounded wait for the receive loop to stop
        }
    }
}
