using System.Buffers.Binary;

namespace Sensor.Communication.Protocol;

/// <summary>
/// Builds protocol v1 packets. The backend uses the command methods; the sensor-direction methods
/// (heartbeat, measurements) serve fake sensors in tests.
/// </summary>
/// <remarks>
/// Each <c>Write*</c> method writes into a caller-supplied buffer (no allocation) and returns the
/// byte count; each <c>Create*</c> method is a convenience that allocates an exact-size array.
/// Arguments that would produce a packet the receiver must reject (zero session or sequence) throw,
/// so invalid packets can only be produced deliberately (e.g. by mutating bytes in tests).
/// </remarks>
public static class SensorPacketEncoder
{
    /// <summary>Command packet size: 27 bytes.</summary>
    public const int CommandPacketSize = ProtocolConstants.HeaderSize + ProtocolConstants.CommandPayloadSize;

    /// <summary>Heartbeat packet size: 35 bytes.</summary>
    public const int HeartbeatPacketSize = ProtocolConstants.HeaderSize + ProtocolConstants.HeartbeatPayloadSize;

    /// <summary>Camera measurement packet size: 426 bytes.</summary>
    public const int CameraPacketSize = ProtocolConstants.HeaderSize + ProtocolConstants.CameraPayloadSize;

    /// <summary>Pointer measurement packet size: 28 bytes.</summary>
    public const int PointerPacketSize = ProtocolConstants.HeaderSize + ProtocolConstants.PointerPayloadSize;

    /// <summary>Writes a backend → sensor command.</summary>
    /// <param name="destination">Buffer of at least <see cref="CommandPacketSize"/> bytes.</param>
    /// <param name="target">The sensor the command is for.</param>
    /// <param name="sessionId">The target sensor's current session ID (from its heartbeat).</param>
    /// <param name="commandSequence">The backend's next command sequence for that session.</param>
    /// <param name="command">Start or Stop.</param>
    /// <returns>Bytes written (<see cref="CommandPacketSize"/>).</returns>
    public static int WriteCommand(
        Span<byte> destination, SensorId target, ulong sessionId, ulong commandSequence, SensorCommand command)
    {
        var payload = WriteHeader(destination, MessageType.Command, target, sessionId, commandSequence, ProtocolConstants.CommandPayloadSize);
        payload[0] = (byte)command;
        return CommandPacketSize;
    }

    /// <summary>Creates a backend → sensor command as a new array.</summary>
    /// <inheritdoc cref="WriteCommand" path="/param[position() > 1]"/>
    /// <returns>The 27-byte packet.</returns>
    public static byte[] CreateCommand(SensorId target, ulong sessionId, ulong commandSequence, SensorCommand command)
    {
        var packet = new byte[CommandPacketSize];
        WriteCommand(packet, target, sessionId, commandSequence, command);
        return packet;
    }

    /// <summary>Writes a sensor → backend heartbeat.</summary>
    /// <param name="destination">Buffer of at least <see cref="HeartbeatPacketSize"/> bytes.</param>
    /// <param name="source">The sending sensor.</param>
    /// <param name="sessionId">The sensor's session ID.</param>
    /// <param name="sequence">The sensor's output sequence.</param>
    /// <param name="state">The reported state.</param>
    /// <param name="lastAcceptedCommandSequence">Last accepted backend command sequence, or 0.</param>
    /// <returns>Bytes written (<see cref="HeartbeatPacketSize"/>).</returns>
    public static int WriteHeartbeat(
        Span<byte> destination, SensorId source, ulong sessionId, ulong sequence, SensorState state, ulong lastAcceptedCommandSequence)
    {
        var payload = WriteHeader(destination, MessageType.Heartbeat, source, sessionId, sequence, ProtocolConstants.HeartbeatPayloadSize);
        payload[0] = (byte)state;
        BinaryPrimitives.WriteUInt64BigEndian(payload[1..], lastAcceptedCommandSequence);
        return HeartbeatPacketSize;
    }

    /// <summary>Creates a sensor → backend heartbeat as a new array.</summary>
    /// <inheritdoc cref="WriteHeartbeat" path="/param[position() > 1]"/>
    /// <returns>The 35-byte packet.</returns>
    public static byte[] CreateHeartbeat(
        SensorId source, ulong sessionId, ulong sequence, SensorState state, ulong lastAcceptedCommandSequence)
    {
        var packet = new byte[HeartbeatPacketSize];
        WriteHeartbeat(packet, source, sessionId, sequence, state, lastAcceptedCommandSequence);
        return packet;
    }

    /// <summary>Creates a Camera measurement as a new array.</summary>
    /// <param name="sessionId">The Camera's session ID.</param>
    /// <param name="sequence">The Camera's output sequence.</param>
    /// <param name="rgbaPixels">Exactly 400 RGBA bytes, row-major.</param>
    /// <returns>The 426-byte packet.</returns>
    /// <exception cref="ArgumentException"><paramref name="rgbaPixels"/> is not 400 bytes.</exception>
    public static byte[] CreateCameraMeasurement(ulong sessionId, ulong sequence, ReadOnlySpan<byte> rgbaPixels)
    {
        if (rgbaPixels.Length != ProtocolConstants.CameraPayloadSize)
        {
            throw new ArgumentException(
                $"A Camera image must be exactly {ProtocolConstants.CameraPayloadSize} bytes.", nameof(rgbaPixels));
        }

        var packet = new byte[CameraPacketSize];
        var payload = WriteHeader(packet, MessageType.Measurement, SensorId.Camera, sessionId, sequence, ProtocolConstants.CameraPayloadSize);
        rgbaPixels.CopyTo(payload);
        return packet;
    }

    /// <summary>Creates a Pointer measurement as a new array.</summary>
    /// <param name="sessionId">The Pointer's session ID.</param>
    /// <param name="sequence">The Pointer's output sequence.</param>
    /// <param name="x">Column (not range-checked, so tests can build invalid coordinates).</param>
    /// <param name="y">Row (not range-checked, so tests can build invalid coordinates).</param>
    /// <returns>The 28-byte packet.</returns>
    public static byte[] CreatePointerMeasurement(ulong sessionId, ulong sequence, byte x, byte y)
    {
        var packet = new byte[PointerPacketSize];
        var payload = WriteHeader(packet, MessageType.Measurement, SensorId.Pointer, sessionId, sequence, ProtocolConstants.PointerPayloadSize);
        payload[0] = x;
        payload[1] = y;
        return packet;
    }

    /// <summary>Writes the 26-byte header and returns the payload slice that follows it.</summary>
    private static Span<byte> WriteHeader(
        Span<byte> destination, MessageType type, SensorId sensorId, ulong sessionId, ulong sequence, ushort payloadLength)
    {
        ArgumentOutOfRangeException.ThrowIfZero(sessionId);
        ArgumentOutOfRangeException.ThrowIfZero(sequence);
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, ProtocolConstants.HeaderSize + payloadLength, nameof(destination));

        ProtocolConstants.Magic.CopyTo(destination);
        destination[4] = ProtocolConstants.Version;
        destination[5] = (byte)type;
        destination[6] = (byte)sensorId;
        destination[7] = ProtocolConstants.ReservedFlags;
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], sessionId);
        BinaryPrimitives.WriteUInt64BigEndian(destination[16..], sequence);
        BinaryPrimitives.WriteUInt16BigEndian(destination[24..], payloadLength);
        return destination.Slice(ProtocolConstants.HeaderSize, payloadLength);
    }
}
