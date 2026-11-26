using Sensor.Communication.Protocol;
using Sensor.Control.Sensors;
using Sensor.Control.Supervision;

namespace Sensor.Control.Status;

/// <summary>Notified by the event loop after each processed event (packet or tick), on the event-loop thread.</summary>
public interface ISupervisorObserver
{
    /// <summary>The supervisor has finished handling one event.</summary>
    /// <param name="supervisor">The supervisor; safe to read only during this call.</param>
    void OnEventProcessed(SystemSupervisor supervisor);
}

/// <summary>
/// Publishes an immutable <see cref="SystemStatus"/> snapshot after each event, so HTTP threads can read
/// the system without touching the single-threaded supervisor and sensor state.
/// </summary>
/// <remarks>
/// The snapshot reference is swapped atomically (<see cref="Volatile"/>). <see cref="Version"/> only
/// increases when the content actually changes, so readers can cheaply skip unchanged snapshots.
/// Images are immutable and shared by reference, so a snapshot never copies pixel data.
/// </remarks>
public sealed class SystemStatusStore : ISupervisorObserver
{
    private readonly SupervisorOptions _options;

    private SystemStatus _current;
    private long _version;

    /// <summary>Creates the store with an initial "nothing seen yet" snapshot.</summary>
    /// <param name="options">Supplies the silence timeout used for <see cref="ConnectionStatus.Connected"/>.</param>
    public SystemStatusStore(SupervisorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _current = new SystemStatus(
            SystemState.Initializing,
            null,
            new SensorStatus(SensorId.Camera, null, ConnectionStatus.NeverSeen, null, null, null),
            new SensorStatus(SensorId.Pointer, null, ConnectionStatus.NeverSeen, null, null, null));
    }

    /// <summary>The latest published snapshot.</summary>
    public SystemStatus Current => Volatile.Read(ref _current);

    /// <summary>Increases by one each time the snapshot content changes.</summary>
    public long Version => Interlocked.Read(ref _version);

    /// <inheritdoc />
    public void OnEventProcessed(SystemSupervisor supervisor)
    {
        ArgumentNullException.ThrowIfNull(supervisor);

        var next = new SystemStatus(
            supervisor.State,
            supervisor.FaultReason,
            Describe(supervisor.Camera),
            Describe(supervisor.Pointer));

        // Record equality: unchanged content (the common case on ticks) publishes nothing.
        if (next == Volatile.Read(ref _current))
        {
            return;
        }

        Volatile.Write(ref _current, next);
        Interlocked.Increment(ref _version);
    }

    private SensorStatus Describe(SensorControl sensor)
    {
        var connection = sensor.GetSilenceDuration() switch
        {
            null => ConnectionStatus.NeverSeen,
            var silence when silence <= _options.SilenceTimeout => ConnectionStatus.Connected,
            _ => ConnectionStatus.Disconnected,
        };

        return new SensorStatus(
            sensor.SensorId,
            sensor.State,
            connection,
            sensor.SessionId,
            sensor.LastValidTrafficTimestamp,
            sensor.LatestMeasurement);
    }
}
