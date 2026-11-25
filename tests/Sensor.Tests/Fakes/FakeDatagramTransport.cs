using System.Net;
using System.Net.Sockets;
using Sensor.Communication.Transport;

namespace Sensor.Tests.Fakes;

/// <summary>In-memory transport: records sent datagrams; can be told to fail sends. Receive is not used by these tests.</summary>
internal sealed class FakeDatagramTransport : IDatagramTransport
{
    public List<(byte[] Datagram, IPEndPoint Destination)> Sent { get; } = [];

    public bool FailSends { get; set; }

    public IPEndPoint LocalEndPoint { get; } = new(IPAddress.Loopback, 7450);

    public ValueTask<DatagramReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Use the UDP transport for receive tests.");

    public ValueTask SendAsync(ReadOnlyMemory<byte> datagram, IPEndPoint remoteEndPoint, CancellationToken cancellationToken)
    {
        if (FailSends)
        {
            throw new SocketException((int)SocketError.NetworkUnreachable);
        }

        Sent.Add((datagram.ToArray(), remoteEndPoint));
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
    }
}
