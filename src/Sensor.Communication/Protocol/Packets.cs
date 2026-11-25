namespace Sensor.Communication.Protocol;

/// <summary>
/// Base type of every decoded packet. Consumers dispatch with pattern matching
/// (<c>switch (packet) { case HeartbeatPacket h: ... }</c>) instead of re-reading the type byte.
/// </summary>
/// <param name="Header">The validated header.</param>
public abstract record SensorPacket(PacketHeader Header);

/// <summary>Sensor → backend state heartbeat (type 1).</summary>
/// <param name="Header">The validated header.</param>
/// <param name="State">The sensor's current state.</param>
/// <param name="LastAcceptedCommandSequence">Sequence of the last command the sensor accepted, or 0 if none.</param>
public sealed record HeartbeatPacket(PacketHeader Header, SensorState State, ulong LastAcceptedCommandSequence)
    : SensorPacket(Header);

/// <summary>Base type of sensor → backend measurements (type 2).</summary>
/// <param name="Header">The validated header.</param>
public abstract record MeasurementPacket(PacketHeader Header) : SensorPacket(Header);

/// <summary>Camera measurement: a 10×10 RGBA image.</summary>
/// <param name="Header">The validated header.</param>
/// <param name="Image">The image (owns a copy of the payload bytes).</param>
public sealed record CameraMeasurement(PacketHeader Header, CameraImage Image) : MeasurementPacket(Header);

/// <summary>Pointer measurement: a pixel position in the Camera image.</summary>
/// <param name="Header">The validated header.</param>
/// <param name="Position">The pointed pixel, inside the 10×10 image.</param>
public sealed record PointerMeasurement(PacketHeader Header, PixelPosition Position) : MeasurementPacket(Header);

/// <summary>Backend → sensor command (type 3).</summary>
/// <param name="Header">The validated header; <see cref="PacketHeader.SensorId"/> is the target sensor.</param>
/// <param name="Command">Start or Stop.</param>
public sealed record CommandPacket(PacketHeader Header, SensorCommand Command) : SensorPacket(Header);
