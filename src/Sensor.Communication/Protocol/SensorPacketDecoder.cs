using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace Sensor.Communication.Protocol;

/// <summary>
/// Parses and validates one UDP datagram (exactly one packet) into a <see cref="SensorPacket"/>.
/// </summary>
/// <remarks>
/// <para>
/// Pure and stateless: it validates everything that can be judged from the bytes alone
/// (PROTOCOL.md, Validation). Endpoint, session and sequence freshness are stateful checks and
/// belong to the receiver.
/// </para>
/// <para>
/// The message type selects the payload decoder in exactly one place (<see cref="DecodePayload"/>);
/// each payload has its own small method. Only Camera measurements allocate a payload copy.
/// </para>
/// </remarks>
public static class SensorPacketDecoder
{
    // Header field offsets (PROTOCOL.md, Header).
    private const int VersionOffset = 4;
    private const int MessageTypeOffset = 5;
    private const int SensorIdOffset = 6;
    private const int FlagsOffset = 7;
    private const int SessionOffset = 8;
    private const int SequenceOffset = 16;
    private const int PayloadLengthOffset = 24;

    /// <summary>Decodes a datagram.</summary>
    /// <param name="datagram">The complete datagram bytes. They are not retained.</param>
    /// <param name="packet">The decoded packet when successful; otherwise <see langword="null"/>.</param>
    /// <param name="error">The rejection reason, or <see cref="DecodeError.None"/> when successful.</param>
    /// <returns><see langword="true"/> when the datagram is a valid packet.</returns>
    public static bool TryDecode(
        ReadOnlySpan<byte> datagram,
        [NotNullWhen(true)] out SensorPacket? packet,
        out DecodeError error)
    {
        packet = null;
        error = TryReadHeader(datagram, out var header);
        if (error != DecodeError.None)
        {
            return false;
        }

        packet = DecodePayload(header, datagram[ProtocolConstants.HeaderSize..], out error);
        return packet is not null;
    }

    /// <summary>Validates the header and the overall datagram length against the declared payload length.</summary>
    private static DecodeError TryReadHeader(ReadOnlySpan<byte> datagram, out PacketHeader header)
    {
        header = default;

        if (datagram.Length < ProtocolConstants.HeaderSize)
        {
            return DecodeError.Truncated;
        }

        if (!datagram[..ProtocolConstants.Magic.Length].SequenceEqual(ProtocolConstants.Magic))
        {
            return DecodeError.InvalidMagic;
        }

        if (datagram[VersionOffset] != ProtocolConstants.Version)
        {
            return DecodeError.UnsupportedVersion;
        }

        var messageType = (MessageType)datagram[MessageTypeOffset];
        if (!Enum.IsDefined(messageType))
        {
            return DecodeError.UnknownMessageType;
        }

        var sensorId = (SensorId)datagram[SensorIdOffset];
        if (!Enum.IsDefined(sensorId))
        {
            return DecodeError.UnknownSensorId;
        }

        if (datagram[FlagsOffset] != ProtocolConstants.ReservedFlags)
        {
            return DecodeError.NonZeroReservedFlags;
        }

        var sessionId = BinaryPrimitives.ReadUInt64BigEndian(datagram[SessionOffset..]);
        if (sessionId == 0)
        {
            return DecodeError.ZeroSessionId;
        }

        var sequence = BinaryPrimitives.ReadUInt64BigEndian(datagram[SequenceOffset..]);
        if (sequence == 0)
        {
            return DecodeError.ZeroSequence;
        }

        // The declared length must account for every byte: fewer bytes = truncated, more = trailing.
        var payloadLength = BinaryPrimitives.ReadUInt16BigEndian(datagram[PayloadLengthOffset..]);
        var actualPayloadLength = datagram.Length - ProtocolConstants.HeaderSize;
        if (actualPayloadLength < payloadLength)
        {
            return DecodeError.Truncated;
        }

        if (actualPayloadLength > payloadLength)
        {
            return DecodeError.TrailingBytes;
        }

        if (payloadLength != ExpectedPayloadLength(messageType, sensorId))
        {
            return DecodeError.InvalidPayloadLength;
        }

        header = new PacketHeader(messageType, sensorId, sessionId, sequence, payloadLength);
        return DecodeError.None;
    }

    /// <summary>Exact payload size required for a message type (and, for measurements, the sensor).</summary>
    private static int ExpectedPayloadLength(MessageType messageType, SensorId sensorId) => messageType switch
    {
        MessageType.Heartbeat => ProtocolConstants.HeartbeatPayloadSize,
        MessageType.Command => ProtocolConstants.CommandPayloadSize,
        MessageType.Measurement when sensorId == SensorId.Camera => ProtocolConstants.CameraPayloadSize,
        MessageType.Measurement => ProtocolConstants.PointerPayloadSize,
        _ => -1,
    };

    /// <summary>The single dispatch point from message type to payload decoder.</summary>
    private static SensorPacket? DecodePayload(PacketHeader header, ReadOnlySpan<byte> payload, out DecodeError error) =>
        header.MessageType switch
        {
            MessageType.Heartbeat => DecodeHeartbeat(header, payload, out error),
            MessageType.Measurement when header.SensorId == SensorId.Camera => DecodeCamera(header, payload, out error),
            MessageType.Measurement => DecodePointer(header, payload, out error),
            MessageType.Command => DecodeCommand(header, payload, out error),
            _ => Fail(DecodeError.UnknownMessageType, out error),
        };

    private static HeartbeatPacket? DecodeHeartbeat(PacketHeader header, ReadOnlySpan<byte> payload, out DecodeError error)
    {
        var state = (SensorState)payload[0];
        if (!Enum.IsDefined(state))
        {
            return Fail<HeartbeatPacket>(DecodeError.UnknownState, out error);
        }

        error = DecodeError.None;
        return new HeartbeatPacket(header, state, BinaryPrimitives.ReadUInt64BigEndian(payload[1..]));
    }

    private static CameraMeasurement? DecodeCamera(PacketHeader header, ReadOnlySpan<byte> payload, out DecodeError error)
    {
        // Alpha is the 4th byte of every pixel and must be 255; validate before copying the image.
        for (var alpha = ProtocolConstants.BytesPerPixel - 1; alpha < payload.Length; alpha += ProtocolConstants.BytesPerPixel)
        {
            if (payload[alpha] != ProtocolConstants.OpaqueAlpha)
            {
                return Fail<CameraMeasurement>(DecodeError.InvalidAlpha, out error);
            }
        }

        error = DecodeError.None;
        return new CameraMeasurement(header, CameraImage.FromRgba(payload));
    }

    private static PointerMeasurement? DecodePointer(PacketHeader header, ReadOnlySpan<byte> payload, out DecodeError error)
    {
        var position = new PixelPosition(payload[0], payload[1]);
        if (!position.IsInsideImage)
        {
            return Fail<PointerMeasurement>(DecodeError.InvalidCoordinate, out error);
        }

        error = DecodeError.None;
        return new PointerMeasurement(header, position);
    }

    private static CommandPacket? DecodeCommand(PacketHeader header, ReadOnlySpan<byte> payload, out DecodeError error)
    {
        var command = (SensorCommand)payload[0];
        if (!Enum.IsDefined(command))
        {
            return Fail<CommandPacket>(DecodeError.UnknownCommand, out error);
        }

        error = DecodeError.None;
        return new CommandPacket(header, command);
    }

    private static SensorPacket? Fail(DecodeError reason, out DecodeError error) => Fail<SensorPacket>(reason, out error);

    private static T? Fail<T>(DecodeError reason, out DecodeError error)
        where T : SensorPacket
    {
        error = reason;
        return null;
    }
}
