using Sensor.Communication.Protocol;

namespace Sensor.Tests.Communication;

public sealed class SensorPacketEncoderTests
{
    [Fact]
    public void CreateCommand_Start_MatchesProtocolExample() =>
        Assert.Equal(
            ProtocolExamples.CameraStartCommand,
            SensorPacketEncoder.CreateCommand(SensorId.Camera, ProtocolExamples.SessionId, 1, SensorCommand.Start));

    [Fact]
    public void CreateCommand_Stop_MatchesProtocolExample() =>
        Assert.Equal(
            ProtocolExamples.CameraStopCommand,
            SensorPacketEncoder.CreateCommand(SensorId.Camera, ProtocolExamples.SessionId, 2, SensorCommand.Stop));

    [Fact]
    public void WriteCommand_IntoLargerBuffer_WritesOnly27Bytes()
    {
        var buffer = new byte[64];

        var written = SensorPacketEncoder.WriteCommand(buffer, SensorId.Camera, ProtocolExamples.SessionId, 1, SensorCommand.Start);

        Assert.Equal(27, written);
        Assert.Equal(ProtocolExamples.CameraStartCommand, buffer[..written]);
        Assert.All(buffer[written..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void CreateHeartbeat_MatchesProtocolExamples()
    {
        Assert.Equal(
            ProtocolExamples.CameraSetupHeartbeat,
            SensorPacketEncoder.CreateHeartbeat(SensorId.Camera, ProtocolExamples.SessionId, 1, SensorState.Setup, 0));
        Assert.Equal(
            ProtocolExamples.CameraReadyHeartbeat,
            SensorPacketEncoder.CreateHeartbeat(SensorId.Camera, ProtocolExamples.SessionId, 2, SensorState.Ready, 1));
    }

    [Fact]
    public void CreatePointerMeasurement_MatchesProtocolExample() =>
        Assert.Equal(
            ProtocolExamples.PointerMeasurementX3Y7,
            SensorPacketEncoder.CreatePointerMeasurement(ProtocolExamples.SessionId, 3, 3, 7));

    [Fact]
    public void CreateCameraMeasurement_MatchesPythonExample()
    {
        byte[] pixels = [.. Enumerable.Repeat<byte[]>([255, 0, 0, 255], 100).SelectMany(p => p)];

        Assert.Equal(
            ProtocolExamples.CameraAllRedMeasurement(),
            SensorPacketEncoder.CreateCameraMeasurement(ProtocolExamples.SessionId, 3, pixels));
    }

    [Fact]
    public void CreateCommand_ThenDecode_RoundTrips()
    {
        var bytes = SensorPacketEncoder.CreateCommand(SensorId.Pointer, ulong.MaxValue, 42, SensorCommand.Stop);

        Assert.True(SensorPacketDecoder.TryDecode(bytes, out var packet, out _));

        var command = Assert.IsType<CommandPacket>(packet);
        Assert.Equal(new PacketHeader(MessageType.Command, SensorId.Pointer, ulong.MaxValue, 42, 1), command.Header);
        Assert.Equal(SensorCommand.Stop, command.Command);
    }

    [Fact]
    public void CreateCommand_ZeroSession_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SensorPacketEncoder.CreateCommand(SensorId.Camera, 0, 1, SensorCommand.Start));

    [Fact]
    public void CreateCommand_ZeroSequence_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SensorPacketEncoder.CreateCommand(SensorId.Camera, 1, 0, SensorCommand.Start));

    [Fact]
    public void WriteCommand_BufferTooSmall_Throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SensorPacketEncoder.WriteCommand(new byte[26], SensorId.Camera, 1, 1, SensorCommand.Start));

    [Fact]
    public void CreateCameraMeasurement_WrongPixelCount_Throws() =>
        Assert.Throws<ArgumentException>(() => SensorPacketEncoder.CreateCameraMeasurement(1, 1, new byte[399]));
}
