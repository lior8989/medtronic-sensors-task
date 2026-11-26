using Sensor.Communication.Protocol;
using Sensor.Control.Status;
using Sensor.Control.Supervision;

namespace Sensor.Tests.Control;

public sealed class SystemStatusStoreTests
{
    private readonly SupervisorHarness _h = new();
    private readonly SystemStatusStore _store;

    public SystemStatusStoreTests() => _store = new SystemStatusStore(_h.Options);

    private void Publish() => _store.OnEventProcessed(_h.Supervisor);

    [Fact]
    public void Initially_NothingSeen()
    {
        Assert.Equal(SystemState.Initializing, _store.Current.State);
        Assert.Equal(ConnectionStatus.NeverSeen, _store.Current.Camera.Connection);
        Assert.Equal(0, _store.Version);
    }

    [Fact]
    public async Task Publish_ReflectsSupervisorAndSensors()
    {
        await _h.Heartbeats(SensorState.Setup, SensorState.Ready);
        await _h.Measurement(_h.Pointer);

        Publish();

        var status = _store.Current;
        Assert.Equal(SystemState.Configuring, status.State);
        Assert.Equal(SensorState.Setup, status.Camera.State);
        Assert.Equal(ConnectionStatus.Connected, status.Camera.Connection);
        Assert.Equal(_h.Camera.Session, status.Camera.SessionId);
        Assert.IsType<PointerMeasurement>(status.Pointer.LatestMeasurement!.Value.Packet);
    }

    [Fact]
    public async Task Publish_UnchangedContent_DoesNotBumpVersion()
    {
        await _h.Heartbeat(_h.Camera, SensorState.Setup);
        Publish();
        var version = _store.Version;

        Publish();
        Publish();

        Assert.Equal(version, _store.Version);
    }

    [Fact]
    public async Task Publish_NewPacket_BumpsVersion()
    {
        await _h.Heartbeat(_h.Camera, SensorState.Setup);
        Publish();
        var version = _store.Version;

        await _h.Heartbeat(_h.Camera, SensorState.Setup);
        Publish();

        Assert.Equal(version + 1, _store.Version);
    }

    [Theory]
    [InlineData(999, ConnectionStatus.Connected)]
    [InlineData(1000, ConnectionStatus.Connected)]
    [InlineData(1001, ConnectionStatus.Disconnected)]
    public async Task Connection_UsesSilenceTimeout(int silentMs, ConnectionStatus expected)
    {
        await _h.Heartbeat(_h.Camera, SensorState.Setup);
        _h.Time.Advance(TimeSpan.FromMilliseconds(silentMs));

        Publish();

        Assert.Equal(expected, _store.Current.Camera.Connection);
    }
}
