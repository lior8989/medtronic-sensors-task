using Sensor.Control.Processing;
using Sensor.Control.Supervision;

namespace Sensor.WebApi.Hosting;

/// <summary>
/// Runs the backend for the lifetime of the host: the supervisor event loop (UDP receive, ticks,
/// state machine) and the processing worker, both stopped by the host's stopping token.
/// </summary>
/// <param name="eventLoop">The single event loop.</param>
/// <param name="worker">The processing worker.</param>
public sealed class SensorBackendService(SupervisorEventLoop eventLoop, ProcessingWorker worker) : BackgroundService
{
    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(eventLoop.RunAsync(stoppingToken), worker.RunAsync(stoppingToken));
}
