using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Sensor.Communication.Protocol;
using Sensor.Control.Sensors;
using Sensor.Control.Supervision;
using Sensor.Tests.Fakes;

namespace Sensor.Tests.Control;

/// <summary>A scripted sensor: owns a session ID and an output sequence, and builds its packets.</summary>
internal sealed class ScriptedSensor(SensorId id, IPEndPoint endpoint, ulong session)
{
    private ulong _sequence;

    public SensorId Id { get; } = id;

    public IPEndPoint Endpoint { get; } = endpoint;

    public ulong Session { get; private set; } = session;

    /// <summary>Simulates a process restart: new session, sequence starts again at 1.</summary>
    public void Restart()
    {
        Session += 0x1000;
        _sequence = 0;
    }

    public byte[] Heartbeat(SensorState state) =>
        SensorPacketEncoder.CreateHeartbeat(Id, Session, ++_sequence, state, 0);

    public byte[] Measurement() => Id == SensorId.Camera
        ? SensorPacketEncoder.CreateCameraMeasurement(Session, ++_sequence, OpaqueImage)
        : SensorPacketEncoder.CreatePointerMeasurement(Session, ++_sequence, 3, 7);

    private static readonly byte[] OpaqueImage = [.. Enumerable.Repeat<byte[]>([1, 2, 3, 255], 100).SelectMany(p => p)];
}

/// <summary>
/// Drives a <see cref="SystemSupervisor"/> directly (no event loop) with fake time and transport.
/// Every injected packet first advances the clock by 1 ms, so packets are strictly ordered in time.
/// </summary>
internal sealed class SupervisorHarness
{
    public static readonly IPEndPoint CameraEndpoint = new(IPAddress.Loopback, 8100);
    public static readonly IPEndPoint PointerEndpoint = new(IPAddress.Loopback, 8200);

    public SupervisorHarness()
    {
        var camera = new SensorControl(SensorId.Camera, CameraEndpoint, Transport, Time, NullLogger<SensorControl>.Instance);
        var pointer = new SensorControl(SensorId.Pointer, PointerEndpoint, Transport, Time, NullLogger<SensorControl>.Instance);
        Supervisor = new SystemSupervisor(camera, pointer, Sink, Options, Time, NullLogger<SystemSupervisor>.Instance);
    }

    /// <summary>Records what the supervisor forwards to processing.</summary>
    public RecordingMeasurementSink Sink { get; } = new();

    public FakeTimeProvider Time { get; } = new();

    public FakeDatagramTransport Transport { get; } = new();

    public SupervisorOptions Options { get; } = new();

    public SystemSupervisor Supervisor { get; }

    public ScriptedSensor Camera { get; } = new(SensorId.Camera, CameraEndpoint, 0xCA00);

    public ScriptedSensor Pointer { get; } = new(SensorId.Pointer, PointerEndpoint, 0xB000);

    public SystemState State => Supervisor.State;

    /// <summary>Commands sent so far: (target, command, sequence, session).</summary>
    public IReadOnlyList<(SensorId Target, SensorCommand Command, ulong Sequence, ulong Session)> Commands =>
    [
        .. Transport.Sent.Select(s =>
        {
            Assert.True(SensorPacketDecoder.TryDecode(s.Datagram, out var packet, out _));
            var command = Assert.IsType<CommandPacket>(packet);
            return (command.Header.SensorId, command.Command, command.Header.Sequence, command.Header.SessionId);
        }),
    ];

    public Task Heartbeat(ScriptedSensor sensor, SensorState state) => Inject(sensor, sensor.Heartbeat(state));

    public Task Measurement(ScriptedSensor sensor) => Inject(sensor, sensor.Measurement());

    /// <summary>Both sensors send one heartbeat each.</summary>
    public async Task Heartbeats(SensorState camera, SensorState pointer)
    {
        await Heartbeat(Camera, camera);
        await Heartbeat(Pointer, pointer);
    }

    /// <summary>Advances time by <paramref name="milliseconds"/> and runs one tick.</summary>
    public async Task Elapse(int milliseconds)
    {
        Time.Advance(TimeSpan.FromMilliseconds(milliseconds));
        await Supervisor.HandleTickAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Brings the system to Running through Configuring (both start in Setup, accept Start).</summary>
    public async Task BringToRunning()
    {
        await Heartbeats(SensorState.Setup, SensorState.Setup);
        await Heartbeats(SensorState.Ready, SensorState.Ready);
        Assert.Equal(SystemState.Running, State);
    }

    private async Task Inject(ScriptedSensor sensor, byte[] datagram)
    {
        Time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(SensorPacketDecoder.TryDecode(datagram, out var packet, out _));
        await Supervisor.HandlePacketAsync(packet, sensor.Endpoint, TestContext.Current.CancellationToken);
    }
}
