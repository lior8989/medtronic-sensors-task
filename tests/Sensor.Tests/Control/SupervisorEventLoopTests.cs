using Microsoft.Extensions.Logging.Abstractions;
using Sensor.Communication.Protocol;
using Sensor.Control.Status;
using Sensor.Control.Supervision;

namespace Sensor.Tests.Control;

/// <summary>
/// Tests of the event loop wiring (receive → decode → queue → supervisor, and the periodic tick),
/// running the real loops against the in-memory transport.
/// </summary>
public sealed class SupervisorEventLoopTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);

    private readonly SupervisorHarness _h = new();

    private SystemStatusStore Status => field ??= new SystemStatusStore(_h.Options);

    private SupervisorEventLoop CreateLoop() =>
        new(_h.Transport, _h.Supervisor, Status, _h.Options, _h.Time, NullLogger<SupervisorEventLoop>.Instance);

    /// <summary>Waits (real time, bounded) until the loop thread has produced the expected effect.</summary>
    private static async Task WaitUntil(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(WaitLimit);
        while (!condition())
        {
            await Task.Delay(5, cts.Token);
        }
    }

    [Fact]
    public async Task ReceivedHeartbeats_AreDecodedAndDriveTheSupervisor()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = CreateLoop().RunAsync(stop.Token);

        _h.Transport.Enqueue(_h.Camera.Heartbeat(SensorState.Setup), _h.Camera.Endpoint);
        _h.Transport.Enqueue(_h.Pointer.Heartbeat(SensorState.Setup), _h.Pointer.Endpoint);

        await WaitUntil(() => _h.Transport.Sent.Count == 2);
        Assert.Equal(SystemState.Configuring, _h.State);

        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task MalformedDatagrams_AreIgnored()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = CreateLoop().RunAsync(stop.Token);

        var truncated = _h.Camera.Heartbeat(SensorState.Error)[..^1];
        _h.Transport.Enqueue(truncated, _h.Camera.Endpoint);
        _h.Transport.Enqueue([1, 2, 3], _h.Camera.Endpoint);
        _h.Transport.Enqueue(_h.Camera.Heartbeat(SensorState.Setup), _h.Camera.Endpoint);

        await WaitUntil(() => _h.Supervisor.Camera.HasBeenSeen);
        Assert.Equal(SensorState.Setup, _h.Supervisor.Camera.State); // The malformed Error report changed nothing.
        Assert.Equal(SystemState.Initializing, _h.State);

        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Ticks_DetectReadySilence()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = CreateLoop().RunAsync(stop.Token);

        _h.Transport.Enqueue(_h.Camera.Heartbeat(SensorState.Ready), _h.Camera.Endpoint);
        _h.Transport.Enqueue(_h.Pointer.Heartbeat(SensorState.Ready), _h.Pointer.Endpoint);
        await WaitUntil(() => _h.State == SystemState.Running);

        // Advance fake time in tick-sized steps; each step fires the periodic timer.
        for (var i = 0; i < 25 && _h.State == SystemState.Running; i++)
        {
            _h.Time.Advance(_h.Options.TickPeriod);
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        await WaitUntil(() => _h.State == SystemState.Faulted);

        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task RunAsync_Cancelled_CompletesWithoutThrowing()
    {
        using var stop = new CancellationTokenSource();
        var run = CreateLoop().RunAsync(stop.Token);

        await stop.CancelAsync();

        await run.WaitAsync(WaitLimit, TestContext.Current.CancellationToken);
        Assert.True(run.IsCompletedSuccessfully);
    }
}
