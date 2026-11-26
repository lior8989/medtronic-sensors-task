using Sensor.Communication.Protocol;
using Sensor.Control.Sensors;

namespace Sensor.Control.Processing;

/// <summary>A Camera image and a Pointer position selected together by the pairing rule.</summary>
/// <param name="Image">The Camera image (latest received before the Pointer sample, within the max age).</param>
/// <param name="Position">The Pointer position that triggered the pairing.</param>
/// <param name="CameraSequence">Output sequence of the Camera measurement.</param>
/// <param name="PointerSequence">Output sequence of the Pointer measurement.</param>
/// <param name="CameraReceivedTimestamp">Backend receive time of the image (<see cref="TimeProvider.GetTimestamp"/>).</param>
/// <param name="PointerReceivedTimestamp">Backend receive time of the position.</param>
public sealed record MeasurementPair(
    CameraImage Image,
    PixelPosition Position,
    ulong CameraSequence,
    ulong PointerSequence,
    long CameraReceivedTimestamp,
    long PointerReceivedTimestamp);

/// <summary>A computed result: the color for a pair.</summary>
/// <param name="Pair">The image and position that were used.</param>
/// <param name="Color">The computed RGBA color.</param>
/// <param name="CompletedTimestamp">When the computation finished (<see cref="TimeProvider.GetTimestamp"/>).</param>
public sealed record ProcessingResult(MeasurementPair Pair, RgbaColor Color, long CompletedTimestamp);

/// <summary>Pairing rule settings.</summary>
public sealed class PairingOptions
{
    /// <summary>
    /// Maximum age of the latest Camera image, relative to the Pointer sample's receive time, for the two
    /// to be paired. Default 50 ms (~2.5 image intervals: tolerates one lost image plus jitter).
    /// </summary>
    public TimeSpan MaxImageAge { get; set; } = TimeSpan.FromMilliseconds(50);
}

/// <summary>
/// The computation applied to each pair (Strategy). The production algorithm is expensive; this
/// exercise uses a pixel lookup (<see cref="PixelLookupProcessor"/>).
/// </summary>
public interface IMeasurementProcessor
{
    /// <summary>Computes the result color for an image and a position.</summary>
    /// <param name="image">The Camera image.</param>
    /// <param name="position">The Pointer position inside the image.</param>
    /// <param name="cancellationToken">Cancelled when processing stops (system left Running) or on shutdown.</param>
    /// <returns>The computed color.</returns>
    ValueTask<RgbaColor> ProcessAsync(CameraImage image, PixelPosition position, CancellationToken cancellationToken);
}

/// <summary>Receives measurements accepted while the system is Running. Called on the event-loop thread.</summary>
public interface IMeasurementSink
{
    /// <summary>A measurement of the current session was accepted while Running.</summary>
    /// <param name="sensor">The sensor that sent it.</param>
    /// <param name="measurement">The measurement with its receive time.</param>
    void OnRunningMeasurement(SensorId sensor, ReceivedMeasurement measurement);

    /// <summary>The system left Running: discard cached samples and stop processing.</summary>
    void OnProcessingStopped();
}

/// <summary>Accepts pairs for background processing. Called on the event-loop thread.</summary>
public interface IProcessingQueue
{
    /// <summary>Queues a pair; a newer pair replaces any pair still waiting (latest wins).</summary>
    /// <param name="pair">The pair to process.</param>
    void Submit(MeasurementPair pair);

    /// <summary>Drops the waiting pair, cancels the computation in progress and discards its result.</summary>
    void Reset();
}
