using System.Net;
using Microsoft.Extensions.Logging;
using Sensor.Communication.Protocol;
using Sensor.Control.Sensors;

namespace Sensor.Control.Supervision;

/// <summary>
/// The backend system state machine (Developer-Sensor-Task.md: System States, Transition rules,
/// Fault Handling and Automatic Recovery). Owns both <see cref="SensorControl"/>s and decides when
/// to send Start/Stop.
/// </summary>
/// <remarks>
/// <para>
/// Not thread-safe by design: the single event loop (<see cref="SupervisorEventLoop"/>) calls
/// <see cref="HandlePacketAsync"/> and <see cref="HandleTickAsync"/> strictly in order.
/// </para>
/// <para>
/// After every event the supervisor re-evaluates: first the fault rule (applies in every state and
/// has priority), then the rule of the current state. Each state has one small evaluation method.
/// A full State-pattern class hierarchy was considered and not used: the four states share the same
/// two sensors and the cross-cutting fault rule, so separate classes would mostly pass context around.
/// </para>
/// </remarks>
public sealed partial class SystemSupervisor
{
    private readonly SensorControl[] _sensors;
    private readonly SupervisorOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SystemSupervisor> _logger;

    // Current fault episode (only meaningful while Faulted).
    private readonly Dictionary<SensorId, FaultCause> _faults = [];
    private long _faultTimestamp;

    // When Configuring was entered: Ready heartbeats after this instant are "fresh".
    private long _configuringTimestamp;

    /// <summary>Creates the supervisor in <see cref="SystemState.Initializing"/>.</summary>
    /// <param name="camera">Controller of the Camera sensor.</param>
    /// <param name="pointer">Controller of the Pointer sensor.</param>
    /// <param name="options">Timing settings.</param>
    /// <param name="timeProvider">Monotonic time source (fake in tests).</param>
    /// <param name="logger">Logger.</param>
    public SystemSupervisor(
        SensorControl camera,
        SensorControl pointer,
        SupervisorOptions options,
        TimeProvider timeProvider,
        ILogger<SystemSupervisor> logger)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(pointer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        if (camera.SensorId != SensorId.Camera || pointer.SensorId != SensorId.Pointer)
        {
            throw new ArgumentException("Expected the Camera and Pointer controllers, in that order.");
        }

        Camera = camera;
        Pointer = pointer;
        _sensors = [camera, pointer];
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>The current system state.</summary>
    public SystemState State { get; private set; } = SystemState.Initializing;

    /// <summary>Controller of the Camera sensor.</summary>
    public SensorControl Camera { get; }

    /// <summary>Controller of the Pointer sensor.</summary>
    public SensorControl Pointer { get; }

    /// <summary>Sensors in the current fault episode (empty unless <see cref="SystemState.Faulted"/>).</summary>
    public IReadOnlyCollection<SensorFault> Faults => [.. _faults.Select(f => new SensorFault(f.Key, f.Value))];

    /// <summary>Human-readable fault reason, or <see langword="null"/> when not faulted.</summary>
    public string? FaultReason =>
        _faults.Count == 0 ? null : string.Join("; ", _faults.Select(f => new SensorFault(f.Key, f.Value).ToString()));

    /// <summary>Raised after every state transition (old state, new state). Called on the event-loop thread.</summary>
    public event Action<SystemState, SystemState>? StateChanged;

    /// <summary>Applies one decoded packet to its sensor, then re-evaluates the system.</summary>
    /// <param name="packet">A packet that passed byte-level validation.</param>
    /// <param name="source">The datagram's source endpoint.</param>
    /// <param name="cancellationToken">Cancels command sends.</param>
    /// <returns>How the owning sensor handled the packet.</returns>
    public async ValueTask<PacketDisposition> HandlePacketAsync(
        SensorPacket packet, IPEndPoint source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packet);

        // Route by the header's sensor ID; SensorControl then verifies the source endpoint matches.
        var sensor = packet.Header.SensorId == SensorId.Camera ? Camera : Pointer;
        var disposition = sensor.Accept(packet, source);

        // Invalid or stale packets do not change state (task: "Invalid, malformed, or stale packets ...").
        if (disposition is PacketDisposition.Accepted or PacketDisposition.AcceptedNewSession)
        {
            await EvaluateAsync(cancellationToken).ConfigureAwait(false);
        }

        return disposition;
    }

    /// <summary>Periodic re-evaluation: detects Ready silence and drives Start retries.</summary>
    /// <param name="cancellationToken">Cancels command sends.</param>
    /// <returns>A task that completes when evaluation is done.</returns>
    public ValueTask HandleTickAsync(CancellationToken cancellationToken) => EvaluateAsync(cancellationToken);

    /// <summary>Fault rule first (every state, priority), then the current state's rule.</summary>
    private async ValueTask EvaluateAsync(CancellationToken cancellationToken)
    {
        if (await ApplyFaultRuleAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        switch (State)
        {
            case SystemState.Initializing:
                await EvaluateInitializingAsync(cancellationToken).ConfigureAwait(false);
                break;
            case SystemState.Configuring:
                await EvaluateConfiguringAsync(cancellationToken).ConfigureAwait(false);
                break;
            case SystemState.Running:
                await EvaluateRunningAsync(cancellationToken).ConfigureAwait(false);
                break;
            case SystemState.Faulted:
                await EvaluateFaultedAsync(cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>
    /// Enters Faulted, or extends the current episode, when a sensor reports Error or a Ready sensor is silent.
    /// </summary>
    /// <returns><see langword="true"/> if Faulted was entered now (evaluation for this event ends).</returns>
    private async ValueTask<bool> ApplyFaultRuleAsync(CancellationToken cancellationToken)
    {
        var newFaults = new List<SensorFault>(2);
        foreach (var sensor in _sensors)
        {
            if (DetectFault(sensor) is { } cause)
            {
                // Same sensor & cause within the episode = repeated report of the same fault: ignored.
                if (!_faults.TryGetValue(sensor.SensorId, out var known) || known != cause)
                {
                    newFaults.Add(new SensorFault(sensor.SensorId, cause));
                }
            }
        }

        if (newFaults.Count == 0)
        {
            return false;
        }

        var entering = State != SystemState.Faulted;
        if (entering)
        {
            _faults.Clear();
            _faultTimestamp = _timeProvider.GetTimestamp();
        }

        foreach (var fault in newFaults)
        {
            _faults[fault.Sensor] = fault.Cause;
            LogFault(fault.Sensor, fault.Cause);
        }

        if (!entering)
        {
            return false; // Episode extended; recovery evaluation continues below.
        }

        TransitionTo(SystemState.Faulted);

        // Sensor-specific fault: best-effort Stop to the other sensor, once per episode.
        if (_faults.Count == 1)
        {
            var peer = Other(_faults.Keys.First());
            if (!await peer.SendCommandAsync(SensorCommand.Stop, cancellationToken).ConfigureAwait(false))
            {
                LogStopNotSent(peer.SensorId); // Remains Faulted regardless of delivery.
            }
        }

        return true;
    }

    /// <summary>Error report faults immediately; silence faults only if the latest accepted state is Ready.</summary>
    private FaultCause? DetectFault(SensorControl sensor) => sensor.State switch
    {
        SensorState.Error => FaultCause.SensorError,
        SensorState.Ready when sensor.GetSilenceDuration() >= _options.SilenceTimeout => FaultCause.ReadySilence,
        _ => null,
    };

    /// <summary>Initializing: wait until both sensors have been seen with a reported state. No commands.</summary>
    private async ValueTask EvaluateInitializingAsync(CancellationToken cancellationToken)
    {
        if (Camera.State is null || Pointer.State is null)
        {
            return; // No timeout for a sensor that has never been seen.
        }

        if (Camera.State == SensorState.Ready && Pointer.State == SensorState.Ready)
        {
            TransitionTo(SystemState.Running);
            return;
        }

        // Start the Setup sensors right away rather than on the next tick.
        EnterConfiguring();
        await EvaluateConfiguringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Configuring: Start every sensor reporting Setup (with retries); Running after fresh Ready from both.</summary>
    private async ValueTask EvaluateConfiguringAsync(CancellationToken cancellationToken)
    {
        if (IsFreshReady(Camera, _configuringTimestamp) && IsFreshReady(Pointer, _configuringTimestamp))
        {
            TransitionTo(SystemState.Running);
            return;
        }

        foreach (var sensor in _sensors)
        {
            await StartIfInSetupAsync(sensor, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Running: if a sensor is no longer Ready without a fault (e.g. a quick restart in Setup), reconfigure.</summary>
    private async ValueTask EvaluateRunningAsync(CancellationToken cancellationToken)
    {
        if (Camera.State == SensorState.Ready && Pointer.State == SensorState.Ready)
        {
            return;
        }

        EnterConfiguring();
        await EvaluateConfiguringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Faulted: start faulted sensors once they report Setup after the fault; once all faulted sensors
    /// are Ready, start the other sensor (stopped peer, or a sensor first seen during the fault);
    /// Running when both report Ready after the fault. Never goes to Configuring.
    /// </summary>
    private async ValueTask EvaluateFaultedAsync(CancellationToken cancellationToken)
    {
        if (IsFreshReady(Camera, _faultTimestamp) && IsFreshReady(Pointer, _faultTimestamp))
        {
            LogRecovered();
            _faults.Clear();
            TransitionTo(SystemState.Running);
            return;
        }

        var faultedRecovered = true;
        foreach (var sensor in _sensors)
        {
            if (!_faults.ContainsKey(sensor.SensorId))
            {
                continue;
            }

            // Start never clears Error: StartIfInSetupAsync only acts on a Setup report after the fault.
            if (HeardSinceFault(sensor))
            {
                await StartIfInSetupAsync(sensor, cancellationToken).ConfigureAwait(false);
            }

            faultedRecovered &= IsFreshReady(sensor, _faultTimestamp);
        }

        if (!faultedRecovered)
        {
            return;
        }

        foreach (var sensor in _sensors)
        {
            if (!_faults.ContainsKey(sensor.SensorId))
            {
                await StartIfInSetupAsync(sensor, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Sends Start to a sensor reporting Setup, at most once per retry interval; each retry is a new command sequence.</summary>
    private async ValueTask StartIfInSetupAsync(SensorControl sensor, CancellationToken cancellationToken)
    {
        if (sensor.State != SensorState.Setup)
        {
            return;
        }

        // Throttle only Starts sent during the sensor's current Setup period. A Start from an earlier
        // period (e.g. before a fault) is history and must not delay the first Start of this one.
        var recentlySent = sensor.LastSentCommand == SensorCommand.Start
            && sensor.LastSentCommandTimestamp is { } sentAt
            && sentAt >= sensor.StateChangedTimestamp
            && _timeProvider.GetElapsedTime(sentAt) < _options.StartRetryInterval;

        if (!recentlySent)
        {
            await sensor.SendCommandAsync(SensorCommand.Start, cancellationToken).ConfigureAwait(false);
        }
    }

    private void EnterConfiguring()
    {
        _configuringTimestamp = _timeProvider.GetTimestamp();
        TransitionTo(SystemState.Configuring);
    }

    /// <summary>True if the sensor's latest heartbeat reports Ready and arrived after <paramref name="since"/>.</summary>
    private static bool IsFreshReady(SensorControl sensor, long since) =>
        sensor.State == SensorState.Ready && sensor.LastHeartbeatTimestamp > since;

    private bool HeardSinceFault(SensorControl sensor) => sensor.LastHeartbeatTimestamp > _faultTimestamp;

    private SensorControl Other(SensorId sensor) => sensor == SensorId.Camera ? Pointer : Camera;

    private void TransitionTo(SystemState next)
    {
        var previous = State;
        if (previous == next)
        {
            return;
        }

        State = next;
        LogTransition(previous, next);
        StateChanged?.Invoke(previous, next);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "System {Previous} -> {Next}")]
    private partial void LogTransition(SystemState previous, SystemState next);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fault: {Sensor} {Cause}")]
    private partial void LogFault(SensorId sensor, FaultCause cause);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stop to {Sensor} not sent (unknown session or send failure); remaining Faulted")]
    private partial void LogStopNotSent(SensorId sensor);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recovered: both sensors Ready after the fault")]
    private partial void LogRecovered();
}
