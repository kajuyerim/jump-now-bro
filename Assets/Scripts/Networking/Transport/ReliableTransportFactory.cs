namespace JumpNowBro.Networking
{
    /// Constructs a message transport over the current LAN byte channel. A native online backend
    /// also needs its own connection bootstrap; this factory does not abstract sockets or discovery.
    public interface IReliableTransportFactory
    {
        IReliableTransport Create(IDatagramChannel channel);
    }

    public sealed class UdpReliableTransportFactory : IReliableTransportFactory
    {
        public IReliableTransport Create(IDatagramChannel channel) =>
            new UdpReliableTransport(channel, pingIntervalSeconds: 0.2);
    }
}
