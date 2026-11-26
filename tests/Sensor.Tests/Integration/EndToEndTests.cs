using System.Text.Json;
using Sensor.Communication.Protocol;

namespace Sensor.Tests.Integration;

/// <summary>
/// End-to-end scenarios: the real backend host and two simulated sensors over UDP loopback, asserted
/// through <c>GET /api/status</c>. Real time is used (heartbeats 250 ms, silence fault 1 s), so each
/// scenario takes a few seconds; waits are bounded.
/// </summary>
public sealed class EndToEndTests : IAsyncLifetime
{
    private readonly SensorSystem _system = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _system.DisposeAsync();

    private static string? Str(JsonElement e, string path)
    {
        foreach (var part in path.Split('.'))
        {
            e = e.GetProperty(part);
        }

        return e.ValueKind == JsonValueKind.Null ? null : e.GetString();
    }

    [Fact]
    public async Task Startup_BothSensors_ReachRunningAndCalculateCorrectColors()
    {
        _system.StartSensors();

        await _system.WaitForStateAsync("Running");
        var status = await _system.WaitForAsync(s => s.GetProperty("processing").GetProperty("pairsProcessed").GetInt64() >= 3);

        // The result must be exactly the pixel of the image used at the position used.
        var result = status.GetProperty("latestResult");
        var pixels = Convert.FromBase64String(result.GetProperty("image").GetProperty("rgbaBase64").GetString()!);
        var x = result.GetProperty("position").GetProperty("x").GetInt32();
        var y = result.GetProperty("position").GetProperty("y").GetInt32();
        var offset = 4 * ((10 * y) + x);
        var color = result.GetProperty("color");
        Assert.Equal(pixels[offset], color.GetProperty("r").GetInt32());
        Assert.Equal(pixels[offset + 1], color.GetProperty("g").GetInt32());
        Assert.Equal(pixels[offset + 2], color.GetProperty("b").GetInt32());
        Assert.Equal(255, color.GetProperty("a").GetInt32());
        Assert.InRange(result.GetProperty("imageAgeAtPairingMs").GetDouble(), 0, 50);

        Assert.Equal("Connected", Str(status, "camera.connection"));
        Assert.Equal("Connected", Str(status, "pointer.connection"));
        Assert.Equal([(SensorCommand.Start, 1UL)], _system.Camera.AcceptedCommands);
        Assert.Equal([(SensorCommand.Start, 1UL)], _system.Pointer.AcceptedCommands);
    }

    [Fact]
    public async Task OnlyOneSensor_StaysInitializingAndSendsNoCommands()
    {
        _system.Camera.Start(_system.Backend);

        await _system.WaitForAsync(s => Str(s, "camera.connection") == "Connected");
        await Task.Delay(1000, TestContext.Current.CancellationToken);

        var status = await _system.GetStatusAsync();
        Assert.Equal("Initializing", Str(status, "systemState"));
        Assert.Equal("NeverSeen", Str(status, "pointer.connection"));
        Assert.Empty(_system.Camera.AcceptedCommands);
    }

    [Fact]
    public async Task CameraError_FaultsAndStopsPointerOnce_ClearRecoversAutomatically()
    {
        _system.StartSensors();
        await _system.WaitForStateAsync("Running");

        _system.Camera.SetError();
        var faulted = await _system.WaitForStateAsync("Faulted");
        Assert.Equal("Camera reported Error", Str(faulted, "faultReason"));
        await _system.WaitForAsync(s => Str(s, "pointer.state") == "Setup"); // Pointer obeyed Stop.
        await Task.Delay(700, TestContext.Current.CancellationToken);    // Repeated Error heartbeats...
        Assert.Single(_system.Pointer.AcceptedCommands, c => c.Command == SensorCommand.Stop); // ...no repeated Stop.
        Assert.Equal(SensorState.Error, _system.Camera.State);         // Start never cleared Error.

        _system.Camera.Clear();

        var running = await _system.WaitForStateAsync("Running");
        Assert.Null(Str(running, "faultReason"));
        Assert.Equal(SensorCommand.Start, _system.Pointer.AcceptedCommands[^1].Command);
        var processed = running.GetProperty("processing").GetProperty("pairsProcessed").GetInt64();
        await _system.WaitForAsync(s => s.GetProperty("processing").GetProperty("pairsProcessed").GetInt64() > processed);
    }

    [Fact]
    public async Task PointerSilentWhileReady_FaultsAfterOneSecond_ResumingRecovers()
    {
        _system.StartSensors();
        await _system.WaitForStateAsync("Running");

        _system.Pointer.Pause();
        var started = DateTime.UtcNow;
        var faulted = await _system.WaitForStateAsync("Faulted", TimeSpan.FromSeconds(3));
        var elapsed = DateTime.UtcNow - started;

        Assert.Equal("Pointer silent while Ready", Str(faulted, "faultReason"));
        Assert.InRange(elapsed, TimeSpan.FromMilliseconds(900), TimeSpan.FromMilliseconds(2000));
        await _system.WaitForAsync(s => Str(s, "pointer.connection") == "Disconnected");
        await _system.WaitForAsync(s => Str(s, "camera.state") == "Setup"); // Camera stopped.

        _system.Pointer.Resume(); // Connectivity restored; the Pointer is still Ready (decision A).

        await _system.WaitForStateAsync("Running");
        Assert.Equal(SensorCommand.Start, _system.Camera.AcceptedCommands[^1].Command);
    }

    [Fact]
    public async Task SilenceInSetup_DoesNotFault()
    {
        _system.Camera.SetError(); // Fault first so nothing gets started...
        _system.StartSensors();
        await _system.WaitForStateAsync("Faulted");

        _system.Pointer.Pause(); // ...then the Pointer (in Setup, never started) goes silent.
        await Task.Delay(1500, TestContext.Current.CancellationToken);

        var status = await _system.GetStatusAsync();
        Assert.Equal("Camera reported Error", Str(status, "faultReason")); // No added "Pointer silent" fault.
    }

    [Fact]
    public async Task CameraRestart_NewSessionIsStartedWithSequence1_SystemReturnsToRunning()
    {
        _system.StartSensors();
        var running = await _system.WaitForStateAsync("Running");
        var oldSession = Str(running, "camera.sessionId");

        _system.Camera.Restart();

        var recovered = await _system.WaitForAsync(s =>
            Str(s, "systemState") == "Running" && Str(s, "camera.sessionId") != oldSession);
        Assert.NotNull(Str(recovered, "camera.sessionId"));
        Assert.Equal((SensorCommand.Start, 1UL), _system.Camera.AcceptedCommands[^1]);
    }
}
