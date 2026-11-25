using Sensor.Communication.Protocol;

namespace Sensor.Control.Supervision;

/// <summary>Backend system state (Developer-Sensor-Task.md, System States).</summary>
public enum SystemState
{
    /// <summary>Waiting to hear valid traffic (with a reported state) from both sensors for the first time.</summary>
    Initializing,

    /// <summary>Both sensors seen and at least one is in Setup; Start is being sent.</summary>
    Configuring,

    /// <summary>Both sensors Ready; measurements are processed.</summary>
    Running,

    /// <summary>A sensor reported Error, or a Ready sensor was silent for the silence timeout.</summary>
    Faulted,
}

/// <summary>Why a sensor is part of the current fault episode.</summary>
public enum FaultCause
{
    /// <summary>The sensor reported Error.</summary>
    SensorError,

    /// <summary>The sensor's latest accepted state was Ready and it sent no valid traffic for the silence timeout.</summary>
    ReadySilence,
}

/// <summary>One sensor's contribution to a fault episode.</summary>
/// <param name="Sensor">The faulted sensor.</param>
/// <param name="Cause">Error report or Ready silence.</param>
public readonly record struct SensorFault(SensorId Sensor, FaultCause Cause)
{
    /// <summary>Human-readable reason, e.g. for the UI.</summary>
    /// <returns>A short description.</returns>
    public override string ToString() => Cause switch
    {
        FaultCause.SensorError => $"{Sensor} reported Error",
        _ => $"{Sensor} silent while Ready",
    };
}

/// <summary>Timing settings of the supervisor.</summary>
public sealed class SupervisorOptions
{
    /// <summary>A Ready sensor with no valid traffic for this long faults the system. Default 1 s (task requirement).</summary>
    public TimeSpan SilenceTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Minimum interval between Start retries to a sensor that keeps reporting Setup. Default 500 ms (2 heartbeats).</summary>
    public TimeSpan StartRetryInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Period of the timer tick that drives silence detection and retries. Default 50 ms.</summary>
    public TimeSpan TickPeriod { get; set; } = TimeSpan.FromMilliseconds(50);
}
