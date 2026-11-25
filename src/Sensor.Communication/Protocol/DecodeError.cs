namespace Sensor.Communication.Protocol;

/// <summary>Reason a datagram was rejected by <see cref="SensorPacketDecoder"/>; used for logging and tests.</summary>
public enum DecodeError
{
    /// <summary>The datagram is a valid packet.</summary>
    None = 0,

    /// <summary>Shorter than the 26-byte header, or shorter than header + declared payload length.</summary>
    Truncated,

    /// <summary>Longer than header + declared payload length.</summary>
    TrailingBytes,

    /// <summary>Magic is not ASCII "SNSR".</summary>
    InvalidMagic,

    /// <summary>Version is not 1.</summary>
    UnsupportedVersion,

    /// <summary>Message type is not 1, 2 or 3.</summary>
    UnknownMessageType,

    /// <summary>Sensor ID is not 1 (Camera) or 2 (Pointer).</summary>
    UnknownSensorId,

    /// <summary>Reserved flags byte is not 0.</summary>
    NonZeroReservedFlags,

    /// <summary>Session ID is 0.</summary>
    ZeroSessionId,

    /// <summary>Sequence is 0.</summary>
    ZeroSequence,

    /// <summary>Declared payload length does not match the exact size required for the message type and sensor.</summary>
    InvalidPayloadLength,

    /// <summary>Heartbeat state is not 0, 1 or 2.</summary>
    UnknownState,

    /// <summary>Pointer x or y is outside 0..9.</summary>
    InvalidCoordinate,

    /// <summary>A Camera pixel has alpha other than 255.</summary>
    InvalidAlpha,

    /// <summary>Command value is not 1 (Start) or 2 (Stop).</summary>
    UnknownCommand,
}
