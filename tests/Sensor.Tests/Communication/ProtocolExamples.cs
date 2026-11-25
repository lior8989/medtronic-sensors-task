namespace Sensor.Tests.Communication;

/// <summary>Example packets copied verbatim from PROTOCOL.md ("Example packets"), session 0x0102030405060708.</summary>
internal static class ProtocolExamples
{
    public const ulong SessionId = 0x0102030405060708;

    public static readonly byte[] CameraSetupHeartbeat =
        Hex("534E5352 01 01 01 00 0102030405060708 0000000000000001 0009 00 0000000000000000");

    public static readonly byte[] CameraStartCommand =
        Hex("534E5352 01 03 01 00 0102030405060708 0000000000000001 0001 01");

    public static readonly byte[] CameraReadyHeartbeat =
        Hex("534E5352 01 01 01 00 0102030405060708 0000000000000002 0009 01 0000000000000001");

    public static readonly byte[] PointerMeasurementX3Y7 =
        Hex("534E5352 01 02 02 00 0102030405060708 0000000000000003 0002 03 07");

    public static readonly byte[] CameraStopCommand =
        Hex("534E5352 01 03 01 00 0102030405060708 0000000000000002 0001 02");

    /// <summary>The Python example: all pixels opaque red, sequence 3.</summary>
    public static byte[] CameraAllRedMeasurement()
    {
        var header = Hex("534E5352 01 02 01 00 0102030405060708 0000000000000003 0190");
        var pixels = Enumerable.Repeat<byte[]>([255, 0, 0, 255], 100).SelectMany(p => p);
        return [.. header, .. pixels];
    }

    public static byte[] Hex(string text) => Convert.FromHexString(text.Replace(" ", string.Empty, StringComparison.Ordinal));
}
