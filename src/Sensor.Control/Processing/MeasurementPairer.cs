using Sensor.Communication.Protocol;
using Sensor.Control.Sensors;

namespace Sensor.Control.Processing;

/// <summary>
/// The pairing rule: each Pointer sample (the slower, 10 Hz stream) triggers one pair with the latest
/// Camera image, if that image was received at most <see cref="PairingOptions.MaxImageAge"/> before it.
/// </summary>
/// <remarks>
/// <para>
/// Rules (see the design document):
/// newer stream — only an image received before the Pointer sample is used; images never trigger;
/// too old — an image older than the max age gives no pair for that sample;
/// older session / older run — the cache only holds samples accepted while Running in the current
/// session, and is cleared whenever the system leaves Running (a sensor restart also leaves Running),
/// so a pair never mixes sessions or runs. Duplicates and out-of-order packets never get here.
/// </para>
/// <para>Runs on the event-loop thread. Counters are written with <see cref="Interlocked"/> so the UI can read them.</para>
/// </remarks>
public sealed class MeasurementPairer : IMeasurementSink
{
    private readonly IProcessingQueue _queue;
    private readonly PairingOptions _options;
    private readonly TimeProvider _timeProvider;

    private ReceivedMeasurement? _latestImage;
    private long _pairsSubmitted;
    private long _skippedNoImage;
    private long _skippedStaleImage;

    /// <summary>Creates the pairer.</summary>
    /// <param name="queue">Where pairs are sent for processing.</param>
    /// <param name="options">Pairing settings.</param>
    /// <param name="timeProvider">Converts receive timestamps to durations.</param>
    public MeasurementPairer(IProcessingQueue queue, PairingOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _queue = queue;
        _options = options;
        _timeProvider = timeProvider;
    }

    /// <summary>Pairs submitted for processing.</summary>
    public long PairsSubmitted => Interlocked.Read(ref _pairsSubmitted);

    /// <summary>Pointer samples skipped because no image had been received in this run yet.</summary>
    public long SkippedNoImage => Interlocked.Read(ref _skippedNoImage);

    /// <summary>Pointer samples skipped because the latest image was older than the max age.</summary>
    public long SkippedStaleImage => Interlocked.Read(ref _skippedStaleImage);

    /// <inheritdoc />
    public void OnRunningMeasurement(SensorId sensor, ReceivedMeasurement measurement)
    {
        switch (measurement.Packet)
        {
            case CameraMeasurement:
                _latestImage = measurement;
                break;

            case PointerMeasurement pointer:
                TryPair(pointer, measurement.ReceivedTimestamp);
                break;
        }
    }

    /// <inheritdoc />
    public void OnProcessingStopped()
    {
        _latestImage = null;
        _queue.Reset();
    }

    private void TryPair(PointerMeasurement pointer, long pointerTimestamp)
    {
        if (_latestImage is not { Packet: CameraMeasurement camera } image)
        {
            Interlocked.Increment(ref _skippedNoImage);
            return;
        }

        if (_timeProvider.GetElapsedTime(image.ReceivedTimestamp, pointerTimestamp) > _options.MaxImageAge)
        {
            Interlocked.Increment(ref _skippedStaleImage);
            return;
        }

        _queue.Submit(new MeasurementPair(
            camera.Image,
            pointer.Position,
            camera.Header.Sequence,
            pointer.Header.Sequence,
            image.ReceivedTimestamp,
            pointerTimestamp));
        Interlocked.Increment(ref _pairsSubmitted);
    }
}
