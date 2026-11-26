using Sensor.Communication.Protocol;
using Sensor.Control.Sensors;
using Sensor.Control.Supervision;

namespace Sensor.Control.Status;

/// <summary>A sensor's connection status as shown to the operator.</summary>
public enum ConnectionStatus
{
    /// <summary>No valid traffic has ever been received.</summary>
    NeverSeen,

    /// <summary>Valid traffic within the silence timeout (default 1 s).</summary>
    Connected,

    /// <summary>No valid traffic for longer than the silence timeout.</summary>
    Disconnected,
}

/// <summary>Immutable view of one sensor at the time the snapshot was taken.</summary>
/// <param name="Sensor">Which sensor.</param>
/// <param name="State">Latest reported state, or <see langword="null"/> if unknown.</param>
/// <param name="Connection">Connection status.</param>
/// <param name="SessionId">Current session ID, or <see langword="null"/> if never seen.</param>
/// <param name="LastValidTrafficTimestamp">Timestamp of the latest valid traffic.</param>
/// <param name="LatestMeasurement">Latest raw measurement of the current session (any system state).</param>
public sealed record SensorStatus(
    SensorId Sensor,
    SensorState? State,
    ConnectionStatus Connection,
    ulong? SessionId,
    long? LastValidTrafficTimestamp,
    ReceivedMeasurement? LatestMeasurement);

/// <summary>Immutable view of the system, published by the event loop and read by any thread.</summary>
/// <param name="State">System state.</param>
/// <param name="FaultReason">Fault reason, or <see langword="null"/> when not faulted.</param>
/// <param name="Camera">Camera view.</param>
/// <param name="Pointer">Pointer view.</param>
public sealed record SystemStatus(SystemState State, string? FaultReason, SensorStatus Camera, SensorStatus Pointer);
