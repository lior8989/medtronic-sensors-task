using Sensor.Communication.Protocol;
using Sensor.Control.Processing;
using Sensor.Control.Sensors;

namespace Sensor.Tests.Fakes;

/// <summary>Records measurements forwarded while Running and processing-stopped notifications.</summary>
internal sealed class RecordingMeasurementSink : IMeasurementSink
{
    public List<(SensorId Sensor, ReceivedMeasurement Measurement)> Measurements { get; } = [];

    public int StoppedCount { get; private set; }

    public void OnRunningMeasurement(SensorId sensor, ReceivedMeasurement measurement) => Measurements.Add((sensor, measurement));

    public void OnProcessingStopped() => StoppedCount++;
}

/// <summary>Records submitted pairs and resets.</summary>
internal sealed class RecordingProcessingQueue : IProcessingQueue
{
    public List<MeasurementPair> Submitted { get; } = [];

    public int ResetCount { get; private set; }

    public void Submit(MeasurementPair pair) => Submitted.Add(pair);

    public void Reset() => ResetCount++;
}
