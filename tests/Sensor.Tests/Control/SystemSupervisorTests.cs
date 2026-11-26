using Sensor.Communication.Protocol;
using Sensor.Control.Supervision;

namespace Sensor.Tests.Control;

/// <summary>Scenario tests of the system state machine, named after the task's rules.</summary>
public sealed class SystemSupervisorTests
{
    private readonly SupervisorHarness _h = new();

    // ---------------- Initializing ----------------

    [Fact]
    public async Task Initializing_OnlyOneSensorSeen_StaysAndSendsNothing()
    {
        await _h.Heartbeat(_h.Camera, SensorState.Setup);
        await _h.Elapse(5000); // No timeout for a sensor that has never been seen.

        Assert.Equal(SystemState.Initializing, _h.State);
        Assert.Empty(_h.Commands);
    }

    [Fact]
    public async Task Initializing_BothSeenInSetup_EntersConfiguringAndStartsBoth()
    {
        await _h.Heartbeats(SensorState.Setup, SensorState.Setup);

        Assert.Equal(SystemState.Configuring, _h.State);
        Assert.Equal(
            [(SensorId.Camera, SensorCommand.Start, 1UL, _h.Camera.Session), (SensorId.Pointer, SensorCommand.Start, 1UL, _h.Pointer.Session)],
            _h.Commands);
    }

    [Fact]
    public async Task Initializing_BothSeenReady_EntersRunningDirectly()
    {
        await _h.Heartbeats(SensorState.Ready, SensorState.Ready);

        Assert.Equal(SystemState.Running, _h.State);
        Assert.Empty(_h.Commands);
    }

    [Fact]
    public async Task Initializing_SeenOnlyByMeasurement_WaitsForAReportedState()
    {
        await _h.Heartbeat(_h.Camera, SensorState.Ready);
        await _h.Measurement(_h.Pointer);

        Assert.Equal(SystemState.Initializing, _h.State);
    }

    [Fact]
    public async Task Initializing_ErrorReport_FaultsImmediately()
    {
        await _h.Heartbeat(_h.Camera, SensorState.Error);

        Assert.Equal(SystemState.Faulted, _h.State);
        Assert.Equal("Camera reported Error", _h.Supervisor.FaultReason);
    }

    // ---------------- Configuring ----------------

    [Fact]
    public async Task Configuring_StillSetup_RetriesStartEvery500msWithHigherSequence()
    {
        await _h.Heartbeats(SensorState.Setup, SensorState.Ready);
        Assert.Single(_h.Commands);

        await _h.Elapse(250);
        await _h.Heartbeat(_h.Camera, SensorState.Setup);
        Assert.Single(_h.Commands); // < 500 ms since the last Start.

        await _h.Elapse(250);
        Assert.Equal((SensorId.Camera, SensorCommand.Start, 2UL, _h.Camera.Session), _h.Commands[^1]);
    }

    [Fact]
    public async Task Configuring_ReadySensor_IsNotSentStart()
    {
        await _h.Heartbeats(SensorState.Setup, SensorState.Ready);

        Assert.DoesNotContain(_h.Commands, c => c.Target == SensorId.Pointer);
    }

    [Fact]
    public async Task Configuring_FreshReadyFromBoth_EntersRunning()
    {
        await _h.Heartbeats(SensorState.Setup, SensorState.Ready);
        await _h.Heartbeat(_h.Camera, SensorState.Ready);
        Assert.Equal(SystemState.Configuring, _h.State); // Pointer's Ready report predates Configuring.

        await _h.Heartbeat(_h.Pointer, SensorState.Ready);

        Assert.Equal(SystemState.Running, _h.State);
    }

    [Fact]
    public async Task Configuring_SilenceInSetup_DoesNotFault()
    {
        await _h.Heartbeats(SensorState.Setup, SensorState.Setup);

        await _h.Elapse(3000);

        Assert.Equal(SystemState.Configuring, _h.State);
    }

    // ---------------- Running ----------------

    [Fact]
    public async Task Running_ReadySilenceUnder1s_DoesNotFault()
    {
        await _h.BringToRunning();
        await _h.Heartbeat(_h.Camera, SensorState.Ready);
        await _h.Heartbeat(_h.Pointer, SensorState.Ready);

        await _h.Elapse(998); // Camera's last valid traffic was 999 ms ago (1 ms per injected packet).

        Assert.Equal(SystemState.Running, _h.State);
    }

    [Fact]
    public async Task Running_ReadySilence1s_FaultsAndStopsPeerOnce()
    {
        await _h.BringToRunning();
        await _h.Heartbeat(_h.Pointer, SensorState.Ready); // Pointer stays fresh; Camera goes silent.
        var before = _h.Commands.Count;

        await _h.Elapse(999);
        await _h.Heartbeat(_h.Pointer, SensorState.Ready);
        await _h.Elapse(50);

        Assert.Equal(SystemState.Faulted, _h.State);
        Assert.Equal("Camera silent while Ready", _h.Supervisor.FaultReason);
        Assert.Equal((SensorId.Pointer, SensorCommand.Stop), (_h.Commands[before].Target, _h.Commands[before].Command));

        await _h.Elapse(500);
        Assert.Equal(before + 1, _h.Commands.Count); // No repeated Stop for the same episode.
    }

    [Fact]
    public async Task Running_MeasurementsKeepSensorFresh()
    {
        await _h.BringToRunning();

        for (var i = 0; i < 30; i++)
        {
            await _h.Elapse(100);
            await _h.Measurement(_h.Camera);
            await _h.Measurement(_h.Pointer);
        }

        Assert.Equal(SystemState.Running, _h.State);
    }

    [Fact]
    public async Task Running_ErrorReport_FaultsAndStopsPeerOnlyOnce()
    {
        await _h.BringToRunning();

        await _h.Heartbeat(_h.Pointer, SensorState.Error);
        await _h.Heartbeat(_h.Pointer, SensorState.Error);
        await _h.Heartbeat(_h.Pointer, SensorState.Error);

        Assert.Equal(SystemState.Faulted, _h.State);
        Assert.Single(_h.Commands, c => c.Command == SensorCommand.Stop);
        Assert.Equal((SensorId.Camera, SensorCommand.Stop), (_h.Commands[^1].Target, _h.Commands[^1].Command));
    }

    [Fact]
    public async Task Running_QuickRestartInSetup_EntersConfiguringAndStartsIt()
    {
        await _h.BringToRunning();
        _h.Camera.Restart();

        await _h.Heartbeat(_h.Camera, SensorState.Setup);

        Assert.Equal(SystemState.Configuring, _h.State);
        Assert.Equal((SensorId.Camera, SensorCommand.Start, 1UL, _h.Camera.Session), _h.Commands[^1]);
    }

    [Fact]
    public async Task Running_RestartAfterLongGap_IsSilenceFault()
    {
        await _h.BringToRunning();
        await _h.Heartbeat(_h.Pointer, SensorState.Ready);

        await _h.Elapse(600);
        await _h.Heartbeat(_h.Pointer, SensorState.Ready);
        await _h.Elapse(600); // Camera silent > 1 s while Ready.
        Assert.Equal(SystemState.Faulted, _h.State);

        _h.Camera.Restart();
        await _h.Heartbeat(_h.Camera, SensorState.Setup); // New session's Setup is not itself a fault...

        Assert.Equal(SystemState.Faulted, _h.State);
        Assert.Equal((SensorId.Camera, SensorCommand.Start, 1UL, _h.Camera.Session), _h.Commands[^1]); // ...and gets Start.
    }

    // ---------------- Faulted & recovery ----------------

    [Fact]
    public async Task Faulted_FullRecoveryFromError_StartsFaultedThenPeerThenRunning()
    {
        await _h.BringToRunning();
        await _h.Heartbeat(_h.Camera, SensorState.Error);       // Fault; Stop -> Pointer.
        await _h.Heartbeat(_h.Pointer, SensorState.Setup);      // Pointer obeyed Stop.
        var afterFault = _h.Commands.Count;

        await _h.Heartbeat(_h.Camera, SensorState.Setup);       // Operator cleared Error.
        Assert.Equal((SensorId.Camera, SensorCommand.Start), (_h.Commands[^1].Target, _h.Commands[^1].Command));
        Assert.Equal(afterFault + 1, _h.Commands.Count);        // Peer not started before the faulted sensor is Ready.

        await _h.Heartbeat(_h.Camera, SensorState.Ready);       // Faulted sensor recovered.
        Assert.Equal((SensorId.Pointer, SensorCommand.Start), (_h.Commands[^1].Target, _h.Commands[^1].Command));
        Assert.Equal(SystemState.Faulted, _h.State);

        await _h.Heartbeat(_h.Pointer, SensorState.Ready);

        Assert.Equal(SystemState.Running, _h.State);
        Assert.Null(_h.Supervisor.FaultReason);
    }

    [Fact]
    public async Task Faulted_SensorStillInError_IsNeverSentStart()
    {
        await _h.BringToRunning();
        await _h.Heartbeat(_h.Camera, SensorState.Error);
        var afterFault = _h.Commands.Count;

        for (var i = 0; i < 10; i++)
        {
            await _h.Elapse(250);
            await _h.Heartbeat(_h.Camera, SensorState.Error);
            await _h.Heartbeat(_h.Pointer, SensorState.Setup);
        }

        // Neither the Error sensor (Start must not clear Error) nor the stopped peer (faulted sensor not Ready yet).
        Assert.DoesNotContain(_h.Commands.Skip(afterFault), c => c.Command == SensorCommand.Start);
    }

    [Fact]
    public async Task Faulted_SensorLeftInSetup_StaysFaultedNotConfiguring()
    {
        await _h.BringToRunning();
        await _h.Heartbeat(_h.Camera, SensorState.Error);

        await _h.Heartbeat(_h.Camera, SensorState.Setup);
        await _h.Heartbeat(_h.Pointer, SensorState.Setup);
        await _h.Elapse(2000);

        Assert.Equal(SystemState.Faulted, _h.State);
    }

    [Fact]
    public async Task Faulted_SilenceFaultedSensorReturnsReady_CountsAsRecovered()
    {
        // Decision A: a sensor that comes back directly in Ready (never restarted) is recovered.
        await _h.BringToRunning();
        await _h.Heartbeat(_h.Camera, SensorState.Ready);
        await _h.Elapse(600);
        await _h.Heartbeat(_h.Camera, SensorState.Ready);
        await _h.Elapse(600); // Pointer silent while Ready -> fault, Stop -> Camera.
        Assert.Equal(SystemState.Faulted, _h.State);
        await _h.Heartbeat(_h.Camera, SensorState.Setup);

        await _h.Heartbeat(_h.Pointer, SensorState.Ready); // Connectivity restored, still Ready.

        Assert.Equal((SensorId.Camera, SensorCommand.Start), (_h.Commands[^1].Target, _h.Commands[^1].Command));
        await _h.Heartbeat(_h.Camera, SensorState.Ready);
        Assert.Equal(SystemState.Running, _h.State);
    }

    [Fact]
    public async Task Faulted_StaleReadyFromBeforeFault_DoesNotRecover()
    {
        await _h.BringToRunning();
        await _h.Heartbeat(_h.Camera, SensorState.Error);
        await _h.Heartbeat(_h.Camera, SensorState.Setup);
        await _h.Heartbeat(_h.Camera, SensorState.Ready);

        // Pointer's last Ready report predates the fault; it must report Ready again.
        Assert.Equal(SystemState.Faulted, _h.State);
    }

    [Fact]
    public async Task Faulted_BeforeOtherSensorSeen_WaitsForItAndStartsIt()
    {
        await _h.Heartbeat(_h.Camera, SensorState.Error);    // Pointer never seen: no Stop possible.
        Assert.Equal(SystemState.Faulted, _h.State);
        Assert.Empty(_h.Commands);

        await _h.Heartbeat(_h.Camera, SensorState.Setup);
        await _h.Heartbeat(_h.Camera, SensorState.Ready);
        await _h.Elapse(5000);                                // Recovery keeps waiting for the unseen sensor.
        await _h.Heartbeat(_h.Camera, SensorState.Ready);
        Assert.Equal(SystemState.Faulted, _h.State);

        await _h.Heartbeat(_h.Pointer, SensorState.Setup);   // First seen during the fault.
        Assert.Equal((SensorId.Pointer, SensorCommand.Start), (_h.Commands[^1].Target, _h.Commands[^1].Command));

        await _h.Heartbeat(_h.Pointer, SensorState.Ready);
        Assert.Equal(SystemState.Running, _h.State);
    }

    [Fact]
    public async Task Faulted_SecondSensorFaultsDuringEpisode_ExtendsEpisodeWithoutNewStop()
    {
        await _h.BringToRunning();
        await _h.Heartbeat(_h.Camera, SensorState.Error);
        await _h.Heartbeat(_h.Pointer, SensorState.Error);

        Assert.Equal(SystemState.Faulted, _h.State);
        Assert.Equal(2, _h.Supervisor.Faults.Count);
        Assert.Single(_h.Commands, c => c.Command == SensorCommand.Stop);
    }

    [Fact]
    public async Task Faulted_BothFaultedInOneEvaluation_SendsNoStop()
    {
        await _h.BringToRunning();
        await _h.Heartbeat(_h.Pointer, SensorState.Ready);
        await _h.Heartbeat(_h.Camera, SensorState.Ready);

        await _h.Elapse(1500); // Both silent while Ready, detected on the same tick.

        Assert.Equal(SystemState.Faulted, _h.State);
        Assert.Equal(2, _h.Supervisor.Faults.Count);
        Assert.DoesNotContain(_h.Commands, c => c.Command == SensorCommand.Stop);
    }

    [Fact]
    public async Task Faulted_StopSendFails_RemainsFaulted()
    {
        await _h.BringToRunning();
        _h.Transport.FailSends = true;

        await _h.Heartbeat(_h.Camera, SensorState.Error);

        Assert.Equal(SystemState.Faulted, _h.State);
    }

    // ---------------- Invalid traffic ----------------

    [Fact]
    public async Task InvalidSource_DoesNotRefreshFreshnessOrChangeState()
    {
        await _h.BringToRunning();
        await _h.Heartbeat(_h.Pointer, SensorState.Ready);

        // Camera-looking packets from a wrong endpoint keep arriving, but Camera is really silent.
        for (var i = 0; i < 12; i++)
        {
            await _h.Elapse(100);
            Assert.True(SensorPacketDecoder.TryDecode(_h.Camera.Heartbeat(SensorState.Ready), out var packet, out _));
            await _h.Supervisor.HandlePacketAsync(packet, SupervisorHarness.PointerEndpoint, TestContext.Current.CancellationToken);
            await _h.Heartbeat(_h.Pointer, SensorState.Ready);
        }

        Assert.Equal(SystemState.Faulted, _h.State);
    }

    // ---------------- Processing hook ----------------

    [Fact]
    public async Task Measurements_AreForwardedOnlyWhileRunning()
    {
        await _h.Heartbeats(SensorState.Setup, SensorState.Setup);
        await _h.Measurement(_h.Camera); // Configuring: not forwarded (raw data is still tracked by SensorControl).
        Assert.NotNull(_h.Supervisor.Camera.LatestMeasurement);

        await _h.Heartbeats(SensorState.Ready, SensorState.Ready);
        await _h.Measurement(_h.Camera);
        await _h.Measurement(_h.Pointer);

        Assert.Equal([SensorId.Camera, SensorId.Pointer], _h.Sink.Measurements.Select(m => m.Sensor));
    }

    [Fact]
    public async Task LeavingRunning_StopsProcessing()
    {
        await _h.BringToRunning();
        Assert.Equal(0, _h.Sink.StoppedCount);

        await _h.Heartbeat(_h.Camera, SensorState.Error);
        await _h.Measurement(_h.Pointer); // In flight after the fault: not forwarded.

        Assert.Equal(1, _h.Sink.StoppedCount);
        Assert.Empty(_h.Sink.Measurements);
    }

    [Fact]
    public async Task MeasurementThatRevealsSilenceFault_IsNotForwarded()
    {
        await _h.BringToRunning();
        await _h.Heartbeat(_h.Pointer, SensorState.Ready);
        _h.Time.Advance(TimeSpan.FromMilliseconds(1100)); // Camera silent, no tick ran yet.

        await _h.Measurement(_h.Pointer); // Evaluation faults first, so this sample is not processed.

        Assert.Equal(SystemState.Faulted, _h.State);
        Assert.Empty(_h.Sink.Measurements);
    }

    [Fact]
    public async Task StateChanged_IsRaisedForEachTransition()
    {
        var transitions = new List<(SystemState, SystemState)>();
        _h.Supervisor.StateChanged += (from, to) => transitions.Add((from, to));

        await _h.BringToRunning();
        await _h.Heartbeat(_h.Camera, SensorState.Error);

        Assert.Equal(
            [(SystemState.Initializing, SystemState.Configuring), (SystemState.Configuring, SystemState.Running), (SystemState.Running, SystemState.Faulted)],
            transitions);
    }
}
