using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Sensor.Communication.Protocol;
using Sensor.Control.Sensors;
using Sensor.Tests.Fakes;

namespace Sensor.Tests.Control;

public sealed class SensorControlTests
{
    private const ulong Session = 0xA1;
    private const ulong OtherSession = 0xB2;

    private static readonly IPEndPoint CameraEndpoint = new(IPAddress.Loopback, 8100);

    private readonly FakeTimeProvider _time = new();
    private readonly FakeDatagramTransport _transport = new();
    private readonly SensorControl _camera;

    public SensorControlTests() =>
        _camera = new SensorControl(SensorId.Camera, CameraEndpoint, _transport, _time, NullLogger<SensorControl>.Instance);

    private static SensorPacket Decode(byte[] bytes)
    {
        Assert.True(SensorPacketDecoder.TryDecode(bytes, out var packet, out _));
        return packet;
    }

    private static SensorPacket Heartbeat(ulong session, ulong sequence, SensorState state = SensorState.Setup, ulong lastCommand = 0) =>
        Decode(SensorPacketEncoder.CreateHeartbeat(SensorId.Camera, session, sequence, state, lastCommand));

    private static SensorPacket Image(ulong session, ulong sequence)
    {
        var pixels = new byte[400];
        for (var a = 3; a < 400; a += 4)
        {
            pixels[a] = 255;
        }

        return Decode(SensorPacketEncoder.CreateCameraMeasurement(session, sequence, pixels));
    }

    private PacketDisposition Accept(SensorPacket packet) => _camera.Accept(packet, CameraEndpoint);

    // ---- Sessions ----

    [Fact]
    public void Accept_FirstHeartbeat_AdoptsSessionAndState()
    {
        Assert.False(_camera.HasBeenSeen);

        Assert.Equal(PacketDisposition.AcceptedNewSession, Accept(Heartbeat(Session, 1)));

        Assert.True(_camera.HasBeenSeen);
        Assert.Equal(Session, _camera.SessionId);
        Assert.Equal(SensorState.Setup, _camera.State);
        Assert.Equal(_time.GetTimestamp(), _camera.LastValidTrafficTimestamp);
        Assert.Equal(_time.GetTimestamp(), _camera.LastHeartbeatTimestamp);
    }

    [Fact]
    public void Accept_NewSession_RetiresOldAndResetsTracking()
    {
        Accept(Heartbeat(Session, 10, SensorState.Ready, lastCommand: 5));
        Accept(Image(Session, 11));

        // Restarted sensor: new session, sequence restarts at 1, back in Setup.
        Assert.Equal(PacketDisposition.AcceptedNewSession, Accept(Heartbeat(OtherSession, 1)));

        Assert.Equal(OtherSession, _camera.SessionId);
        Assert.Equal(SensorState.Setup, _camera.State);
        Assert.Equal(0UL, _camera.LastAcceptedCommandSequence);
        Assert.Null(_camera.LatestMeasurement);
    }

    [Fact]
    public void Accept_PacketFromRetiredSession_IsRejectedAndDoesNotSwitchBack()
    {
        Accept(Heartbeat(Session, 5, SensorState.Ready));
        Accept(Heartbeat(OtherSession, 1));
        var freshness = _camera.LastValidTrafficTimestamp;
        _time.Advance(TimeSpan.FromMilliseconds(100));

        Assert.Equal(PacketDisposition.RetiredSession, Accept(Heartbeat(Session, 6, SensorState.Ready)));

        Assert.Equal(OtherSession, _camera.SessionId);
        Assert.Equal(SensorState.Setup, _camera.State);
        Assert.Equal(freshness, _camera.LastValidTrafficTimestamp);
    }

    [Fact]
    public void Accept_MeasurementAsFirstPacket_AdoptsSessionWithUnknownState()
    {
        Assert.Equal(PacketDisposition.AcceptedNewSession, Accept(Image(Session, 3)));

        Assert.True(_camera.HasBeenSeen);
        Assert.Null(_camera.State);
        Assert.NotNull(_camera.LatestMeasurement);
    }

    [Fact]
    public void Accept_ManyRestarts_RetiredSetIsBounded()
    {
        // Session IDs 1..40 then 41: the oldest retired IDs are evicted beyond the capacity.
        for (ulong session = 1; session <= 41; session++)
        {
            Accept(Heartbeat(session, 1));
        }

        Assert.Equal(PacketDisposition.RetiredSession, Accept(Heartbeat(40, 2)));
        Assert.Equal(PacketDisposition.AcceptedNewSession, Accept(Heartbeat(1, 2)));
    }

    // ---- Sequences ----

    [Fact]
    public void Accept_DuplicateSequence_IsStale()
    {
        Accept(Heartbeat(Session, 5));

        Assert.Equal(PacketDisposition.StaleSequence, Accept(Heartbeat(Session, 5, SensorState.Ready)));
        Assert.Equal(SensorState.Setup, _camera.State);
    }

    [Fact]
    public void Accept_OlderSequence_IsStaleAndDoesNotRefreshFreshness()
    {
        Accept(Heartbeat(Session, 5));
        var freshness = _camera.LastValidTrafficTimestamp;
        _time.Advance(TimeSpan.FromMilliseconds(300));

        Assert.Equal(PacketDisposition.StaleSequence, Accept(Heartbeat(Session, 4, SensorState.Ready)));

        Assert.Equal(freshness, _camera.LastValidTrafficTimestamp);
        Assert.Equal(SensorState.Setup, _camera.State);
    }

    [Fact]
    public void Accept_SequenceIsSharedByHeartbeatsAndMeasurements()
    {
        Accept(Heartbeat(Session, 1, SensorState.Ready));
        Accept(Image(Session, 3));

        // A delayed heartbeat with sequence 2 is older than the measurement with sequence 3.
        Assert.Equal(PacketDisposition.StaleSequence, Accept(Heartbeat(Session, 2, SensorState.Setup)));
        Assert.Equal(SensorState.Ready, _camera.State);
    }

    [Fact]
    public void Accept_GapInSequence_IsAccepted()
    {
        Accept(Heartbeat(Session, 1));

        Assert.Equal(PacketDisposition.Accepted, Accept(Heartbeat(Session, 7, SensorState.Ready)));
        Assert.Equal(SensorState.Ready, _camera.State);
    }

    // ---- Source / type checks ----

    [Fact]
    public void Accept_FromOtherEndpoint_IsWrongSourceAndChangesNothing()
    {
        var result = _camera.Accept(Heartbeat(Session, 1), new IPEndPoint(IPAddress.Loopback, 9999));

        Assert.Equal(PacketDisposition.WrongSource, result);
        Assert.False(_camera.HasBeenSeen);
    }

    [Fact]
    public void Accept_OtherSensorsId_IsWrongSensor()
    {
        var pointerHeartbeat = Decode(SensorPacketEncoder.CreateHeartbeat(SensorId.Pointer, Session, 1, SensorState.Setup, 0));

        Assert.Equal(PacketDisposition.WrongSensor, Accept(pointerHeartbeat));
        Assert.False(_camera.HasBeenSeen);
    }

    [Fact]
    public void Accept_CommandPacket_IsUnexpectedMessageType()
    {
        var command = Decode(SensorPacketEncoder.CreateCommand(SensorId.Camera, Session, 1, SensorCommand.Start));

        Assert.Equal(PacketDisposition.UnexpectedMessageType, Accept(command));
        Assert.False(_camera.HasBeenSeen);
    }

    // ---- Freshness and measurements ----

    [Fact]
    public void Accept_Measurement_RefreshesFreshnessButNotHeartbeatTime()
    {
        Accept(Heartbeat(Session, 1, SensorState.Ready));
        var heartbeatTime = _camera.LastHeartbeatTimestamp;
        _time.Advance(TimeSpan.FromMilliseconds(20));

        Accept(Image(Session, 2));

        Assert.Equal(_time.GetTimestamp(), _camera.LastValidTrafficTimestamp);
        Assert.Equal(heartbeatTime, _camera.LastHeartbeatTimestamp);
        Assert.Equal(_time.GetTimestamp(), _camera.LatestMeasurement!.Value.ReceivedTimestamp);
    }

    [Fact]
    public void Accept_HeartbeatWithSameState_KeepsStateChangedTimestamp()
    {
        Accept(Heartbeat(Session, 1, SensorState.Setup));
        var changedAt = _camera.StateChangedTimestamp;
        _time.Advance(TimeSpan.FromMilliseconds(250));

        Accept(Heartbeat(Session, 2, SensorState.Setup));
        Assert.Equal(changedAt, _camera.StateChangedTimestamp);

        _time.Advance(TimeSpan.FromMilliseconds(250));
        Accept(Heartbeat(Session, 3, SensorState.Ready));
        Assert.Equal(_time.GetTimestamp(), _camera.StateChangedTimestamp);
    }

    [Fact]
    public void GetSilenceDuration_ReflectsTimeSinceLastValidTraffic()
    {
        Assert.Null(_camera.GetSilenceDuration());

        Accept(Heartbeat(Session, 1));
        _time.Advance(TimeSpan.FromMilliseconds(750));

        Assert.Equal(TimeSpan.FromMilliseconds(750), _camera.GetSilenceDuration());
    }

    [Fact]
    public void ClearLatestMeasurement_RemovesCachedSample()
    {
        Accept(Image(Session, 1));

        _camera.ClearLatestMeasurement();

        Assert.Null(_camera.LatestMeasurement);
    }

    // ---- Commands ----

    [Fact]
    public async Task SendCommandAsync_BeforeAnyTraffic_DoesNotSend()
    {
        Assert.False(await _camera.SendCommandAsync(SensorCommand.Start, TestContext.Current.CancellationToken));
        Assert.Empty(_transport.Sent);
    }

    [Fact]
    public async Task SendCommandAsync_FirstCommand_UsesSequence1AndCurrentSession()
    {
        Accept(Heartbeat(Session, 1));

        Assert.True(await _camera.SendCommandAsync(SensorCommand.Start, TestContext.Current.CancellationToken));

        var (datagram, destination) = Assert.Single(_transport.Sent);
        Assert.Equal(CameraEndpoint, destination);
        Assert.Equal(SensorPacketEncoder.CreateCommand(SensorId.Camera, Session, 1, SensorCommand.Start), datagram);
        Assert.Equal(SensorCommand.Start, _camera.LastSentCommand);
        Assert.Equal(_time.GetTimestamp(), _camera.LastSentCommandTimestamp);
    }

    [Fact]
    public async Task SendCommandAsync_Retry_UsesHigherSequence()
    {
        Accept(Heartbeat(Session, 1));

        await _camera.SendCommandAsync(SensorCommand.Start, TestContext.Current.CancellationToken);
        await _camera.SendCommandAsync(SensorCommand.Start, TestContext.Current.CancellationToken);

        Assert.Equal(2, _transport.Sent.Count);
        Assert.Equal(SensorPacketEncoder.CreateCommand(SensorId.Camera, Session, 2, SensorCommand.Start), _transport.Sent[1].Datagram);
    }

    [Fact]
    public async Task SendCommandAsync_SensorAlreadyAcceptedHigherSequence_ContinuesAboveIt()
    {
        // e.g. the backend restarted: its counter is 0 but the sensor already accepted command 7.
        Accept(Heartbeat(Session, 1, SensorState.Ready, lastCommand: 7));

        await _camera.SendCommandAsync(SensorCommand.Stop, TestContext.Current.CancellationToken);

        Assert.Equal(SensorPacketEncoder.CreateCommand(SensorId.Camera, Session, 8, SensorCommand.Stop), _transport.Sent[0].Datagram);
    }

    [Fact]
    public async Task SendCommandAsync_AfterNewSession_RestartsAt1()
    {
        Accept(Heartbeat(Session, 1));
        await _camera.SendCommandAsync(SensorCommand.Start, TestContext.Current.CancellationToken);
        await _camera.SendCommandAsync(SensorCommand.Start, TestContext.Current.CancellationToken);

        Accept(Heartbeat(OtherSession, 1));
        await _camera.SendCommandAsync(SensorCommand.Start, TestContext.Current.CancellationToken);

        Assert.Equal(SensorPacketEncoder.CreateCommand(SensorId.Camera, OtherSession, 1, SensorCommand.Start), _transport.Sent[2].Datagram);
    }

    [Fact]
    public async Task SendCommandAsync_SendFails_ReturnsFalseAndStillConsumesSequence()
    {
        Accept(Heartbeat(Session, 1));
        _transport.FailSends = true;

        Assert.False(await _camera.SendCommandAsync(SensorCommand.Start, TestContext.Current.CancellationToken));
        Assert.Null(_camera.LastSentCommand);

        _transport.FailSends = false;
        await _camera.SendCommandAsync(SensorCommand.Start, TestContext.Current.CancellationToken);

        Assert.Equal(SensorPacketEncoder.CreateCommand(SensorId.Camera, Session, 2, SensorCommand.Start), Assert.Single(_transport.Sent).Datagram);
    }
}
