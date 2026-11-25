using System.Net;

namespace Sensor.Communication.Transport;

/// <summary>
/// A bound, message-oriented datagram endpoint: one socket used both to receive sensor data and to
/// send commands (the sensors reject commands from any other source endpoint).
/// </summary>
/// <remarks>
/// Abstraction seam for testing: the receive loop and command sending can be exercised with an
/// in-memory fake instead of real sockets. <see cref="UdpDatagramTransport"/> is the production implementation.
/// </remarks>
public interface IDatagramTransport : IDisposable
{
    /// <summary>The local endpoint the transport is bound to (useful when bound to port 0).</summary>
    IPEndPoint LocalEndPoint { get; }

    /// <summary>
    /// Receives exactly one datagram into <paramref name="buffer"/>. The buffer is caller-owned and may
    /// be reused between calls; data is valid only until the next receive into the same buffer.
    /// </summary>
    /// <param name="buffer">
    /// Destination buffer. Size it larger than the largest valid packet so oversized datagrams are
    /// detected (by length) rather than truncated.
    /// </param>
    /// <param name="cancellationToken">Cancels the pending receive.</param>
    /// <returns>The byte count and the sender's endpoint.</returns>
    ValueTask<DatagramReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    /// <summary>Sends one datagram to <paramref name="remoteEndPoint"/>. Delivery is best-effort.</summary>
    /// <param name="datagram">The complete packet bytes.</param>
    /// <param name="remoteEndPoint">Destination endpoint.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>A task that completes when the datagram has been handed to the OS.</returns>
    ValueTask SendAsync(ReadOnlyMemory<byte> datagram, IPEndPoint remoteEndPoint, CancellationToken cancellationToken);
}

/// <summary>Result of <see cref="IDatagramTransport.ReceiveAsync"/>.</summary>
/// <param name="BytesReceived">Datagram length in bytes.</param>
/// <param name="RemoteEndPoint">The sender's endpoint, used to verify the datagram's source.</param>
public readonly record struct DatagramReceiveResult(int BytesReceived, IPEndPoint RemoteEndPoint);
