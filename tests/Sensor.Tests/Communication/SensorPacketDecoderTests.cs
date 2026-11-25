using Sensor.Communication.Protocol;

namespace Sensor.Tests.Communication;

public sealed class SensorPacketDecoderTests
{
    [Fact]
    public void TryDecode_CameraSetupHeartbeatExample_DecodesAllFields()
    {
        Assert.True(SensorPacketDecoder.TryDecode(ProtocolExamples.CameraSetupHeartbeat, out var packet, out var error));

        Assert.Equal(DecodeError.None, error);
        var heartbeat = Assert.IsType<HeartbeatPacket>(packet);
        Assert.Equal(new PacketHeader(MessageType.Heartbeat, SensorId.Camera, ProtocolExamples.SessionId, 1, 9), heartbeat.Header);
        Assert.Equal(SensorState.Setup, heartbeat.State);
        Assert.Equal(0UL, heartbeat.LastAcceptedCommandSequence);
    }

    [Fact]
    public void TryDecode_CameraReadyHeartbeatExample_ReportsReadyAndLastAcceptedCommand()
    {
        Assert.True(SensorPacketDecoder.TryDecode(ProtocolExamples.CameraReadyHeartbeat, out var packet, out _));

        var heartbeat = Assert.IsType<HeartbeatPacket>(packet);
        Assert.Equal(2UL, heartbeat.Header.Sequence);
        Assert.Equal(SensorState.Ready, heartbeat.State);
        Assert.Equal(1UL, heartbeat.LastAcceptedCommandSequence);
    }

    [Fact]
    public void TryDecode_PointerMeasurementExample_DecodesPosition()
    {
        Assert.True(SensorPacketDecoder.TryDecode(ProtocolExamples.PointerMeasurementX3Y7, out var packet, out _));

        var pointer = Assert.IsType<PointerMeasurement>(packet);
        Assert.Equal(SensorId.Pointer, pointer.Header.SensorId);
        Assert.Equal(3UL, pointer.Header.Sequence);
        Assert.Equal(new PixelPosition(3, 7), pointer.Position);
    }

    [Fact]
    public void TryDecode_CameraAllRedExample_DecodesImage()
    {
        Assert.True(SensorPacketDecoder.TryDecode(ProtocolExamples.CameraAllRedMeasurement(), out var packet, out _));

        var camera = Assert.IsType<CameraMeasurement>(packet);
        Assert.Equal(400, camera.Header.PayloadLength);
        Assert.Equal(new RgbaColor(255, 0, 0, 255), camera.Image.GetPixel(new PixelPosition(9, 9)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void TryDecode_CommandExamples_DecodesCommand(byte command)
    {
        var bytes = command == 1 ? ProtocolExamples.CameraStartCommand : ProtocolExamples.CameraStopCommand;

        Assert.True(SensorPacketDecoder.TryDecode(bytes, out var packet, out _));

        Assert.Equal((SensorCommand)command, Assert.IsType<CommandPacket>(packet).Command);
    }

    [Fact]
    public void TryDecode_CameraMeasurement_ImageIsIndependentOfReceiveBuffer()
    {
        // The receive buffer is reused for the next datagram; the decoded image must own a copy.
        var buffer = ProtocolExamples.CameraAllRedMeasurement();
        Assert.True(SensorPacketDecoder.TryDecode(buffer, out var packet, out _));

        buffer.AsSpan(26).Clear();

        Assert.Equal(new RgbaColor(255, 0, 0, 255), ((CameraMeasurement)packet).Image.GetPixel(new PixelPosition(0, 0)));
    }

    public static TheoryData<string, int, byte, DecodeError> HeaderMutations => new()
    {
        { "magic", 0, (byte)'X', DecodeError.InvalidMagic },
        { "version", 4, 2, DecodeError.UnsupportedVersion },
        { "type 0", 5, 0, DecodeError.UnknownMessageType },
        { "type 4", 5, 4, DecodeError.UnknownMessageType },
        { "sensor 0", 6, 0, DecodeError.UnknownSensorId },
        { "sensor 3", 6, 3, DecodeError.UnknownSensorId },
        { "flags", 7, 1, DecodeError.NonZeroReservedFlags },
        { "state 3", 26, 3, DecodeError.UnknownState },
    };

    [Theory]
    [MemberData(nameof(HeaderMutations))]
    public void TryDecode_InvalidField_IsRejectedWithReason(string field, int offset, byte value, DecodeError expected)
    {
        _ = field; // Shown in the test name only.
        var bytes = (byte[])ProtocolExamples.CameraSetupHeartbeat.Clone();
        bytes[offset] = value;

        Assert.False(SensorPacketDecoder.TryDecode(bytes, out var packet, out var error));

        Assert.Null(packet);
        Assert.Equal(expected, error);
    }

    [Fact]
    public void TryDecode_ZeroSessionId_IsRejected()
    {
        var bytes = (byte[])ProtocolExamples.CameraSetupHeartbeat.Clone();
        bytes.AsSpan(8, 8).Clear();

        AssertRejected(bytes, DecodeError.ZeroSessionId);
    }

    [Fact]
    public void TryDecode_ZeroSequence_IsRejected()
    {
        var bytes = (byte[])ProtocolExamples.CameraSetupHeartbeat.Clone();
        bytes.AsSpan(16, 8).Clear();

        AssertRejected(bytes, DecodeError.ZeroSequence);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(25)]
    public void TryDecode_ShorterThanHeader_IsTruncated(int length) =>
        AssertRejected(ProtocolExamples.CameraSetupHeartbeat.AsSpan(0, length).ToArray(), DecodeError.Truncated);

    [Fact]
    public void TryDecode_MissingPayloadBytes_IsTruncated() =>
        AssertRejected(ProtocolExamples.CameraSetupHeartbeat[..^1], DecodeError.Truncated);

    [Fact]
    public void TryDecode_ExtraByte_IsTrailingBytes() =>
        AssertRejected([.. ProtocolExamples.CameraSetupHeartbeat, 0], DecodeError.TrailingBytes);

    [Fact]
    public void TryDecode_DeclaredLengthWrongForType_IsInvalidPayloadLength()
    {
        // A self-consistent datagram (declared == actual) whose length is wrong for a Pointer measurement.
        var bytes = ProtocolExamples.Hex("534E5352 01 02 02 00 0102030405060708 0000000000000003 0003 03 07 00");

        AssertRejected(bytes, DecodeError.InvalidPayloadLength);
    }

    [Fact]
    public void TryDecode_CameraSizedPayloadFromPointer_IsInvalidPayloadLength()
    {
        var bytes = ProtocolExamples.CameraAllRedMeasurement();
        bytes[6] = (byte)SensorId.Pointer;

        AssertRejected(bytes, DecodeError.InvalidPayloadLength);
    }

    [Theory]
    [InlineData(10, 0)]
    [InlineData(0, 10)]
    [InlineData(255, 255)]
    public void TryDecode_PointerOutsideImage_IsInvalidCoordinate(byte x, byte y) =>
        AssertRejected(SensorPacketEncoder.CreatePointerMeasurement(1, 1, x, y), DecodeError.InvalidCoordinate);

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public void TryDecode_CameraPixelWithAlphaNot255_IsInvalidAlpha(int pixelIndex)
    {
        var bytes = ProtocolExamples.CameraAllRedMeasurement();
        bytes[26 + (4 * pixelIndex) + 3] = 254;

        AssertRejected(bytes, DecodeError.InvalidAlpha);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void TryDecode_UnknownCommandValue_IsUnknownCommand(byte value)
    {
        var bytes = (byte[])ProtocolExamples.CameraStartCommand.Clone();
        bytes[26] = value;

        AssertRejected(bytes, DecodeError.UnknownCommand);
    }

    private static void AssertRejected(byte[] bytes, DecodeError expected)
    {
        Assert.False(SensorPacketDecoder.TryDecode(bytes, out var packet, out var error));
        Assert.Null(packet);
        Assert.Equal(expected, error);
    }
}
