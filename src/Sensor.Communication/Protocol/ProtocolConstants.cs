namespace Sensor.Communication.Protocol;

/// <summary>
/// Fixed values of Sensor UDP Protocol v1 (see PROTOCOL.md). Every valid packet has an exact,
/// type-specific size, so all sizes are constants.
/// </summary>
public static class ProtocolConstants
{
    /// <summary>The only supported protocol version (header offset 4).</summary>
    public const byte Version = 1;

    /// <summary>Required value of the reserved flags byte (header offset 7).</summary>
    public const byte ReservedFlags = 0;

    /// <summary>Header size in bytes; identical in both directions.</summary>
    public const int HeaderSize = 26;

    /// <summary>Heartbeat payload: 1 state byte + 8-byte last accepted command sequence.</summary>
    public const int HeartbeatPayloadSize = 9;

    /// <summary>Camera measurement payload: 10 × 10 pixels × 4 bytes (RGBA).</summary>
    public const int CameraPayloadSize = ImageWidth * ImageHeight * BytesPerPixel;

    /// <summary>Pointer measurement payload: x byte + y byte.</summary>
    public const int PointerPayloadSize = 2;

    /// <summary>Command payload: 1 command byte.</summary>
    public const int CommandPayloadSize = 1;

    /// <summary>Camera image width in pixels (not transmitted; fixed by the protocol).</summary>
    public const int ImageWidth = 10;

    /// <summary>Camera image height in pixels (not transmitted; fixed by the protocol).</summary>
    public const int ImageHeight = 10;

    /// <summary>Bytes per pixel, in R, G, B, A order.</summary>
    public const int BytesPerPixel = 4;

    /// <summary>The only valid alpha value; every Camera pixel is fully opaque.</summary>
    public const byte OpaqueAlpha = 255;

    /// <summary>
    /// Largest valid packet (Camera measurement). Receive buffers must be larger than this so
    /// oversized datagrams are detected and rejected instead of silently truncated.
    /// </summary>
    public const int MaxPacketSize = HeaderSize + CameraPayloadSize;

    /// <summary>Magic bytes at header offset 0: ASCII "SNSR".</summary>
    public static ReadOnlySpan<byte> Magic => "SNSR"u8;
}
