using System.Net;
using System.Net.Sockets;

namespace Sensor.Communication.Transport;

/// <summary>
/// UDP implementation of <see cref="IDatagramTransport"/> over a single bound <see cref="Socket"/>
/// (Adapter over the socket API). Receives into caller-supplied buffers, so the receive loop does
/// not allocate a new array per datagram.
/// </summary>
/// <remarks>
/// Thread-safety: one receive and concurrent sends may be outstanding at the same time (supported by
/// <see cref="Socket"/>); do not issue concurrent receives.
/// </remarks>
public sealed class UdpDatagramTransport : IDatagramTransport
{
    /// <summary>
    /// Windows <c>SIO_UDP_CONNRESET</c> control code. By default Windows turns an ICMP "port unreachable"
    /// (e.g. a command sent to a stopped sensor) into a <see cref="SocketError.ConnectionReset"/> on the
    /// next receive, which would break the shared receive loop. This option disables that behavior.
    /// </summary>
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    private readonly Socket _socket;

    // Reused wildcard used by ReceiveFromAsync to learn the sender's address family.
    private readonly EndPoint _anyEndPoint;

    /// <summary>Creates the socket and binds it to <paramref name="localEndPoint"/>.</summary>
    /// <param name="localEndPoint">Address and port to bind (port 0 picks a free port).</param>
    /// <exception cref="SocketException">The endpoint cannot be bound (e.g. the port is in use).</exception>
    public UdpDatagramTransport(IPEndPoint localEndPoint)
    {
        ArgumentNullException.ThrowIfNull(localEndPoint);

        _socket = new Socket(localEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                _socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
            }

            _socket.Bind(localEndPoint);
        }
        catch
        {
            _socket.Dispose();
            throw;
        }

        LocalEndPoint = (IPEndPoint)_socket.LocalEndPoint!;
        _anyEndPoint = new IPEndPoint(
            localEndPoint.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
    }

    /// <inheritdoc />
    public IPEndPoint LocalEndPoint { get; }

    /// <inheritdoc />
    public async ValueTask<DatagramReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        // Exits by: returning a datagram, cancellation (checked here and inside ReceiveFromAsync, which
        // also interrupts a pending wait), or any non-reset socket error / disposal propagating out.
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, _anyEndPoint, cancellationToken).ConfigureAwait(false);
                return new DatagramReceiveResult(result.ReceivedBytes, (IPEndPoint)result.RemoteEndPoint);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                // Defensive: an ICMP-triggered reset is not a receive failure for UDP; keep receiving.
            }
        }

        throw new OperationCanceledException(cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask SendAsync(ReadOnlyMemory<byte> datagram, IPEndPoint remoteEndPoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(remoteEndPoint);
        await _socket.SendToAsync(datagram, SocketFlags.None, remoteEndPoint, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose() => _socket.Dispose();
}
