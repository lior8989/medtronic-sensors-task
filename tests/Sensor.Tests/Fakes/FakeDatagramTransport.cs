using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Sensor.Communication.Transport;

namespace Sensor.Tests.Fakes;

/// <summary>
/// In-memory transport. Sent datagrams are recorded (thread-safe); received datagrams are fed with
/// <see cref="Enqueue"/>. Sends can be made to fail.
/// </summary>
internal sealed class FakeDatagramTransport : IDatagramTransport
{
    private readonly Lock _gate = new();
    private readonly List<(byte[] Datagram, IPEndPoint Destination)> _sent = [];
    private readonly Channel<(byte[] Datagram, IPEndPoint Source)> _incoming = Channel.CreateUnbounded<(byte[], IPEndPoint)>();

    /// <summary>Snapshot of everything sent so far, in order.</summary>
    public IReadOnlyList<(byte[] Datagram, IPEndPoint Destination)> Sent
    {
        get
        {
            lock (_gate)
            {
                return [.. _sent];
            }
        }
    }

    public bool FailSends { get; set; }

    public IPEndPoint LocalEndPoint { get; } = new(IPAddress.Loopback, 7450);

    /// <summary>Makes <paramref name="datagram"/> available to the next receive, as if sent from <paramref name="source"/>.</summary>
    public void Enqueue(byte[] datagram, IPEndPoint source) => _incoming.Writer.TryWrite((datagram, source));

    public async ValueTask<DatagramReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var (datagram, source) = await _incoming.Reader.ReadAsync(cancellationToken);
        datagram.CopyTo(buffer);
        return new DatagramReceiveResult(datagram.Length, source);
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> datagram, IPEndPoint remoteEndPoint, CancellationToken cancellationToken)
    {
        if (FailSends)
        {
            throw new SocketException((int)SocketError.NetworkUnreachable);
        }

        lock (_gate)
        {
            _sent.Add((datagram.ToArray(), remoteEndPoint));
        }

        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
    }
}
