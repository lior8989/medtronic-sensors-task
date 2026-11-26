using System.Globalization;
using Sensor.Communication.Protocol;
using Sensor.Control.Processing;
using Sensor.Control.Sensors;
using Sensor.Control.Status;

namespace Sensor.WebApi.Contracts;

/// <summary>
/// Builds the API's <see cref="StatusResponse"/> from the thread-safe sources (status snapshot,
/// processing worker and pairer counters). Ages are computed at mapping time.
/// </summary>
/// <param name="store">The published system snapshot.</param>
/// <param name="worker">Latest result and processing counters.</param>
/// <param name="pairer">Pairing counters.</param>
/// <param name="timeProvider">Converts monotonic timestamps to ages.</param>
public sealed class StatusMapper(
    SystemStatusStore store,
    ProcessingWorker worker,
    MeasurementPairer pairer,
    TimeProvider timeProvider)
{
    /// <summary>Change marker for the push stream: (snapshot version, latest result reference).</summary>
    /// <returns>A value that differs whenever anything shown would change.</returns>
    public (long Version, ProcessingResult? Result) GetChangeMarker() => (store.Version, worker.LatestResult);

    /// <summary>Maps the current state to the response.</summary>
    /// <returns>The status response.</returns>
    public StatusResponse Map()
    {
        var version = store.Version;
        var status = store.Current;

        return new StatusResponse(
            version,
            status.State,
            status.FaultReason,
            new CameraStatusDto(
                status.Camera.State,
                status.Camera.Connection,
                FormatSession(status.Camera.SessionId),
                AgeMs(status.Camera.LastValidTrafficTimestamp),
                status.Camera.LatestMeasurement is { Packet: CameraMeasurement camera } image
                    ? ToImage(camera.Image, camera.Header.Sequence, image.ReceivedTimestamp)
                    : null),
            new PointerStatusDto(
                status.Pointer.State,
                status.Pointer.Connection,
                FormatSession(status.Pointer.SessionId),
                AgeMs(status.Pointer.LastValidTrafficTimestamp),
                status.Pointer.LatestMeasurement is { Packet: PointerMeasurement pointer } sample
                    ? new PositionSampleDto(pointer.Header.Sequence, AgeMs(sample.ReceivedTimestamp)!.Value, pointer.Position.X, pointer.Position.Y)
                    : null),
            worker.LatestResult is { } result ? ToResult(result) : null,
            new ProcessingStatsDto(
                pairer.PairsSubmitted,
                worker.ProcessedPairs,
                worker.DroppedPairs,
                pairer.SkippedNoImage,
                pairer.SkippedStaleImage));
    }

    private ResultDto ToResult(ProcessingResult result)
    {
        var pair = result.Pair;
        return new ResultDto(
            new ColorDto(result.Color.R, result.Color.G, result.Color.B, result.Color.A),
            new PositionDto(pair.Position.X, pair.Position.Y),
            ToImage(pair.Image, pair.CameraSequence, pair.CameraReceivedTimestamp),
            pair.PointerSequence,
            timeProvider.GetElapsedTime(pair.CameraReceivedTimestamp, pair.PointerReceivedTimestamp).TotalMilliseconds,
            AgeMs(result.CompletedTimestamp)!.Value);
    }

    private ImageDto ToImage(CameraImage image, ulong sequence, long receivedTimestamp) =>
        new(
            sequence,
            AgeMs(receivedTimestamp)!.Value,
            ProtocolConstants.ImageWidth,
            ProtocolConstants.ImageHeight,
            Convert.ToBase64String(image.Pixels));

    private double? AgeMs(long? timestamp) =>
        timestamp is { } t ? timeProvider.GetElapsedTime(t).TotalMilliseconds : null;

    private static string? FormatSession(ulong? sessionId) =>
        sessionId?.ToString("X16", CultureInfo.InvariantCulture);
}
