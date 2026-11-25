namespace Sensor.Communication.Protocol;

/// <summary>Identifies a sensor on the wire (header offset 6). For commands it is the target sensor.</summary>
public enum SensorId : byte
{
    /// <summary>The Camera sensor (10×10 RGBA images at 50 Hz).</summary>
    Camera = 1,

    /// <summary>The Pointer sensor (pixel position at 10 Hz).</summary>
    Pointer = 2,
}

/// <summary>Message type (header offset 5).</summary>
public enum MessageType : byte
{
    /// <summary>Sensor → backend state heartbeat, sent every 250 ms and after accepted commands.</summary>
    Heartbeat = 1,

    /// <summary>Sensor → backend measurement (Camera image or Pointer position).</summary>
    Measurement = 2,

    /// <summary>Backend → sensor command (Start / Stop).</summary>
    Command = 3,
}

/// <summary>Sensor state reported in the heartbeat payload (payload offset 0).</summary>
public enum SensorState : byte
{
    /// <summary>Not producing measurements; waiting for a Start command.</summary>
    Setup = 0,

    /// <summary>Producing measurements.</summary>
    Ready = 1,

    /// <summary>Operator-set error; no measurements until cleared (→ Setup) and started again.</summary>
    Error = 2,
}

/// <summary>Command carried in the command payload (payload offset 0).</summary>
public enum SensorCommand : byte
{
    /// <summary>Move Setup (or Ready) to Ready and start measurements. Ignored in Error.</summary>
    Start = 1,

    /// <summary>Move Ready (or Setup) to Setup and stop measurements. Ignored in Error.</summary>
    Stop = 2,
}
