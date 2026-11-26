using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using Sensor.WebApi.Contracts;

namespace Sensor.WebApi.Endpoints;

/// <summary>Minimal API endpoints for the live status.</summary>
public static class StatusEndpoints
{
    /// <summary>
    /// Push cadence of the event stream: 20 updates/s at most, and only when something changed.
    /// Bounds browser load (the Camera alone produces 50 images/s) while staying smooth.
    /// </summary>
    internal static readonly TimeSpan PushInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>Maps <c>GET /api/status</c> and <c>GET /api/events</c>.</summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The same builder.</returns>
    public static IEndpointRouteBuilder MapStatusEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").WithTags("Status");

        api.MapGet("/status", (StatusMapper mapper) => TypedResults.Ok(mapper.Map()))
            .WithName("GetStatus")
            .WithSummary("Current system status")
            .WithDescription(
                "Snapshot of the system state and fault reason, each sensor's state and connection, the latest raw "
                + "Camera image and Pointer position, the latest calculated pixel color with the image and position "
                + "used, and processing counters.");

        api.MapGet("/events", (StatusMapper mapper, TimeProvider timeProvider, CancellationToken cancellationToken) =>
                TypedResults.ServerSentEvents(StreamAsync(mapper, timeProvider, cancellationToken)))
            .WithName("StreamStatus")
            .WithSummary("Live status stream (Server-Sent Events)")
            .WithDescription(
                "text/event-stream of 'status' events, each carrying the same JSON as GET /api/status. Sent at most "
                + "every 50 ms and only when something changed. Use the browser's EventSource; best tested from the web UI.");

        return app;
    }

    /// <summary>Yields the status immediately, then on each push tick if it changed, until the client disconnects.</summary>
    private static async IAsyncEnumerable<SseItem<StatusResponse>> StreamAsync(
        StatusMapper mapper,
        TimeProvider timeProvider,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PushInterval, timeProvider);
        var last = mapper.GetChangeMarker();
        yield return new SseItem<StatusResponse>(mapper.Map(), "status");

        while (!cancellationToken.IsCancellationRequested && await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var current = mapper.GetChangeMarker();
            if (current == last)
            {
                continue;
            }

            last = current;
            yield return new SseItem<StatusResponse>(mapper.Map(), "status");
        }
    }
}
