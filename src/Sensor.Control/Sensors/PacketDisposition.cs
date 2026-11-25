namespace Sensor.Control.Sensors;

/// <summary>Outcome of <see cref="SensorControl.Accept"/> for one decoded packet.</summary>
public enum PacketDisposition
{
    /// <summary>Valid traffic for the current session; state/measurement and freshness were updated.</summary>
    Accepted,

    /// <summary>
    /// Valid traffic that started a new session (first packet ever, or a sensor restart). The previous
    /// session was retired and per-session tracking was reset before the packet was applied.
    /// </summary>
    AcceptedNewSession,

    /// <summary>Rejected: the datagram did not come from this sensor's configured endpoint.</summary>
    WrongSource,

    /// <summary>Rejected: the header's sensor ID does not match this sensor.</summary>
    WrongSensor,

    /// <summary>Rejected: a command packet; sensors never send commands to the backend.</summary>
    UnexpectedMessageType,

    /// <summary>Rejected: the packet belongs to a retired (older) session.</summary>
    RetiredSession,

    /// <summary>Rejected: duplicate or older sequence within the current session.</summary>
    StaleSequence,
}
