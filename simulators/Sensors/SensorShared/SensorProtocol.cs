using System.Buffers.Binary;

namespace SensorShared;

public enum SensorId : byte
{
    Camera = 1,
    Pointer = 2
}

public enum SensorState : byte
{
    Setup = 0,
    Ready = 1,
    Error = 2
}

public enum SensorCommand : byte
{
    Start = 1,
    Stop = 2
}

public readonly record struct CommandPacket(ulong Sequence, SensorCommand Command);

public static class SensorProtocol
{
    public const int HeaderSize = 26;
    public const int ImageWidth = 10;
    public const int ImageHeight = 10;
    public const int ImageByteCount = ImageWidth * ImageHeight * 4;

    public static byte[] EncodeHeartbeat(
        SensorId sensor, ulong session, ulong sequence, SensorState state, ulong lastCommandSequence)
    {
        var packet = CreatePacket(sensor, session, sequence, messageType: 1, payloadLength: 9);
        packet[HeaderSize] = (byte)state;
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(HeaderSize + 1), lastCommandSequence);
        return packet;
    }

    public static byte[] EncodeMeasurement(
        SensorId sensor, ulong session, ulong sequence, ReadOnlySpan<byte> payload)
    {
        var expectedLength = sensor == SensorId.Camera ? ImageByteCount : 2;
        if (payload.Length != expectedLength)
        {
            throw new ArgumentException($"Expected {expectedLength} measurement bytes.", nameof(payload));
        }

        var packet = CreatePacket(sensor, session, sequence, messageType: 2, payloadLength: payload.Length);
        payload.CopyTo(packet.AsSpan(HeaderSize));
        return packet;
    }

    public static bool TryDecodeCommand(
        ReadOnlySpan<byte> packet, SensorId expectedSensor, ulong expectedSession,
        out CommandPacket command, out string error)
    {
        command = default;
        error = "";
        if (packet.Length != HeaderSize + 1)
        {
            error = "Command datagram must contain exactly 27 bytes.";
        }
        else if (!packet[..4].SequenceEqual("SNSR"u8))
        {
            error = "Invalid magic bytes.";
        }
        else if (packet[4] != 1 || packet[5] != 3 || packet[7] != 0)
        {
            error = "Unsupported version, message type, or reserved flags.";
        }
        else if (packet[6] != (byte)expectedSensor)
        {
            error = "Command targets a different sensor.";
        }
        else if (BinaryPrimitives.ReadUInt64BigEndian(packet[8..]) != expectedSession)
        {
            error = "Command targets a different sensor session.";
        }
        else if (BinaryPrimitives.ReadUInt64BigEndian(packet[16..]) == 0)
        {
            error = "Command sequence must be nonzero.";
        }
        else if (BinaryPrimitives.ReadUInt16BigEndian(packet[24..]) != 1)
        {
            error = "Command payload length must be 1.";
        }
        else if (packet[HeaderSize] is not ((byte)SensorCommand.Start or (byte)SensorCommand.Stop))
        {
            error = "Unknown command.";
        }
        else
        {
            command = new CommandPacket(
                BinaryPrimitives.ReadUInt64BigEndian(packet[16..]), (SensorCommand)packet[HeaderSize]);
            return true;
        }

        return false;
    }

    private static byte[] CreatePacket(
        SensorId sensor, ulong session, ulong sequence, byte messageType, int payloadLength)
    {
        if (!Enum.IsDefined(sensor) || session == 0 || sequence == 0)
        {
            throw new ArgumentException("Sensor, session, and sequence must be valid and nonzero.");
        }

        var packet = new byte[HeaderSize + payloadLength];
        "SNSR"u8.CopyTo(packet);
        packet[4] = 1;
        packet[5] = messageType;
        packet[6] = (byte)sensor;
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(8), session);
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(16), sequence);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(24), checked((ushort)payloadLength));
        return packet;
    }
}
