namespace Sensor.Communication.Protocol;

/// <summary>
/// Decoded packet header fields. Magic, version and reserved flags are validated during decoding
/// and are constant for v1, so they are not stored.
/// </summary>
/// <param name="MessageType">Heartbeat, Measurement or Command.</param>
/// <param name="SensorId">Source sensor, or the target sensor for commands.</param>
/// <param name="SessionId">Nonzero random identity of the sensor process (boot).</param>
/// <param name="Sequence">Nonzero sequence number (sensor output counter, or backend command counter).</param>
/// <param name="PayloadLength">Exact payload byte count, excluding the header.</param>
public readonly record struct PacketHeader(
    MessageType MessageType,
    SensorId SensorId,
    ulong SessionId,
    ulong Sequence,
    ushort PayloadLength);

/// <summary>
/// A pixel position in the Camera image. (0, 0) is the top-left pixel; X grows right, Y grows down.
/// </summary>
/// <param name="X">Column index, 0..9.</param>
/// <param name="Y">Row index, 0..9.</param>
public readonly record struct PixelPosition(byte X, byte Y)
{
    /// <summary>True when the position lies inside the fixed 10×10 image.</summary>
    public bool IsInsideImage => X < ProtocolConstants.ImageWidth && Y < ProtocolConstants.ImageHeight;
}

/// <summary>An RGBA color; for valid Camera pixels <see cref="A"/> is always 255.</summary>
/// <param name="R">Red, 0..255.</param>
/// <param name="G">Green, 0..255.</param>
/// <param name="B">Blue, 0..255.</param>
/// <param name="A">Alpha, 255 (opaque) for valid Camera pixels.</param>
public readonly record struct RgbaColor(byte R, byte G, byte B, byte A);
