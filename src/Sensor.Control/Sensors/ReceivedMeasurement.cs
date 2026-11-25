using Sensor.Communication.Protocol;

namespace Sensor.Control.Sensors;

/// <summary>An accepted measurement together with the backend's monotonic receive time.</summary>
/// <param name="Packet">The measurement (current session at the time it was accepted).</param>
/// <param name="ReceivedTimestamp">
/// <see cref="TimeProvider.GetTimestamp"/> value at acceptance. Sensors send no timestamps, so
/// freshness and pairing are judged on the backend's receive time.
/// </param>
public readonly record struct ReceivedMeasurement(MeasurementPacket Packet, long ReceivedTimestamp);
