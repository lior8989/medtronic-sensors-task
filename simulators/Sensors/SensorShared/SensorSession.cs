using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SensorShared;

public sealed class SensorSession
{
    private ulong sequence;

    public SensorId Sensor { get; }
    public ulong Id { get; }
    public SensorState State { get; private set; } = SensorState.Setup;
    public ulong LastCommandSequence { get; private set; }

    public SensorSession(SensorId sensor)
    {
        if (!Enum.IsDefined(sensor))
        {
            throw new ArgumentOutOfRangeException(nameof(sensor));
        }

        Sensor = sensor;
        do
        {
            Id = BinaryPrimitives.ReadUInt64BigEndian(RandomNumberGenerator.GetBytes(8));
        }
        while (Id == 0);
    }

    public bool ApplyCommand(CommandPacket command)
    {
        if (!Enum.IsDefined(command.Command) || command.Sequence == 0)
        {
            throw new ArgumentException("Invalid command.", nameof(command));
        }

        if (command.Sequence <= LastCommandSequence)
        {
            return false;
        }

        LastCommandSequence = command.Sequence;
        if (State != SensorState.Error)
        {
            State = command.Command == SensorCommand.Start ? SensorState.Ready : SensorState.Setup;
        }

        return true;
    }

    public void SetError() => State = SensorState.Error;

    public void ClearError()
    {
        if (State == SensorState.Error)
        {
            State = SensorState.Setup;
        }
    }

    public byte[] CreateHeartbeat() =>
        SensorProtocol.EncodeHeartbeat(Sensor, Id, NextSequence(), State, LastCommandSequence);

    public byte[] CreateMeasurement()
    {
        if (State != SensorState.Ready)
        {
            throw new InvalidOperationException("Measurements require Ready state.");
        }

        byte[] payload;
        if (Sensor == SensorId.Camera)
        {
            payload = new byte[SensorProtocol.ImageByteCount];
            Random.Shared.NextBytes(payload);
            for (var index = 3; index < payload.Length; index += 4)
            {
                payload[index] = 255;
            }
        }
        else
        {
            payload = [(byte)Random.Shared.Next(10), (byte)Random.Shared.Next(10)];
        }

        return SensorProtocol.EncodeMeasurement(Sensor, Id, NextSequence(), payload);
    }

    private ulong NextSequence() => sequence = checked(sequence + 1);
}
