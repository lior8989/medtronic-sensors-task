using System.Net;
using System.Security.Cryptography;
using Sensor.Communication.Protocol;
using Sensor.Communication.Transport;

namespace Sensor.Tests.Integration;

/// <summary>
/// An in-process sensor that follows the supplied simulators' behavior over real UDP: heartbeats every
/// 250 ms in every state, measurements while Ready (Camera 50 Hz, Pointer 10 Hz), Start/Stop with
/// session and command-sequence validation, an immediate heartbeat after each accepted command, and
/// operator actions (error, clear), plus test controls (pause = silence, restart = new session).
/// </summary>
internal sealed class SimulatedSensor : IAsyncDisposable
{
    private static readonly TimeSpan HeartbeatPeriod = TimeSpan.FromMilliseconds(250);

    private readonly Lock _gate = new();
    private readonly UdpDatagramTransport _transport = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource _stop = new();
    private readonly List<(SensorCommand Command, ulong Sequence)> _acceptedCommands = [];
    private readonly byte[] _image = new byte[ProtocolConstants.CameraPayloadSize];
    private Task[] _loops = [];

    private ulong _session = NewSessionId();
    private ulong _sequence;
    private ulong _lastAcceptedCommand;
    private SensorState _state = SensorState.Setup;
    private bool _paused;
    private IPEndPoint? _backend;

    public SimulatedSensor(SensorId id) => Id = id;

    public SensorId Id { get; }

    /// <summary>The sensor's endpoint (configure the backend with it).</summary>
    public IPEndPoint Endpoint => _transport.LocalEndPoint;

    public SensorState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Commands accepted by the sensor, in order (current and previous sessions).</summary>
    public IReadOnlyList<(SensorCommand Command, ulong Sequence)> AcceptedCommands
    {
        get
        {
            lock (_gate)
            {
                return [.. _acceptedCommands];
            }
        }
    }

    /// <summary>Starts sending to <paramref name="backend"/> and listening for commands.</summary>
    public void Start(IPEndPoint backend)
    {
        _backend = backend;
        var token = _stop.Token;
        _loops =
        [
            Task.Run(() => HeartbeatLoopAsync(backend, token), token),
            Task.Run(() => MeasurementLoopAsync(backend, token), token),
            Task.Run(() => CommandLoopAsync(backend, token), token),
        ];
    }

    /// <summary>CLI <c>error</c>: enter Error, stop measurements.</summary>
    public void SetError() => SetStateAndAnnounce(_ => SensorState.Error);

    /// <summary>CLI <c>clear</c>: Error → Setup; otherwise unchanged.</summary>
    public void Clear() => SetStateAndAnnounce(s => s == SensorState.Error ? SensorState.Setup : s);

    /// <summary>Stops all sending (simulates lost connectivity) without changing state.</summary>
    public void Pause()
    {
        lock (_gate)
        {
            _paused = true;
        }
    }

    /// <summary>Resumes sending after <see cref="Pause"/>.</summary>
    public void Resume()
    {
        lock (_gate)
        {
            _paused = false;
        }
    }

    /// <summary>Simulates a process restart: new session, Setup, counters reset.</summary>
    public void Restart()
    {
        lock (_gate)
        {
            _session = NewSessionId();
            _sequence = 0;
            _lastAcceptedCommand = 0;
            _state = SensorState.Setup;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await Task.WhenAll(_loops).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        _transport.Dispose();
        _stop.Dispose();
    }

    private static ulong NewSessionId()
    {
        ulong id;
        do
        {
            id = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8));
        }
        while (id == 0);
        return id;
    }

    private void SetStateAndAnnounce(Func<SensorState, SensorState> change)
    {
        lock (_gate)
        {
            _state = change(_state);
        }

        _ = SendHeartbeatAsync(null, CancellationToken.None);
    }

    private async Task HeartbeatLoopAsync(IPEndPoint backend, CancellationToken token)
    {
        using var timer = new PeriodicTimer(HeartbeatPeriod);
        await SendHeartbeatAsync(backend, token);
        while (!token.IsCancellationRequested && await timer.WaitForNextTickAsync(token))
        {
            await SendHeartbeatAsync(backend, token);
        }
    }

    private async Task MeasurementLoopAsync(IPEndPoint backend, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Id == SensorId.Camera ? 20 : 100));
        while (!token.IsCancellationRequested && await timer.WaitForNextTickAsync(token))
        {
            byte[]? packet;
            lock (_gate)
            {
                packet = _state == SensorState.Ready && !_paused ? NextMeasurement() : null;
            }

            if (packet is not null)
            {
                await _transport.SendAsync(packet, backend, token);
            }
        }
    }

    private async Task CommandLoopAsync(IPEndPoint backend, CancellationToken token)
    {
        var buffer = new byte[2048];
        while (!token.IsCancellationRequested)
        {
            var received = await _transport.ReceiveAsync(buffer, token);
            if (!received.RemoteEndPoint.Equals(backend)
                || !SensorPacketDecoder.TryDecode(buffer.AsSpan(0, received.BytesReceived), out var packet, out _)
                || packet is not CommandPacket command)
            {
                continue;
            }

            bool accepted;
            lock (_gate)
            {
                // Same validation as the simulators: target sensor, current session, increasing sequence.
                accepted = command.Header.SensorId == Id
                    && command.Header.SessionId == _session
                    && command.Header.Sequence > _lastAcceptedCommand;
                if (accepted)
                {
                    _lastAcceptedCommand = command.Header.Sequence;
                    _acceptedCommands.Add((command.Command, command.Header.Sequence));
                    _state = (command.Command, _state) switch
                    {
                        (_, SensorState.Error) => SensorState.Error, // Start/Stop never leave Error.
                        (SensorCommand.Start, _) => SensorState.Ready,
                        _ => SensorState.Setup,
                    };
                }
            }

            if (accepted)
            {
                await SendHeartbeatAsync(backend, token); // Immediate heartbeat after an accepted command.
            }
        }
    }

    private async Task SendHeartbeatAsync(IPEndPoint? backend, CancellationToken token)
    {
        backend ??= _backend;
        if (backend is null)
        {
            return;
        }

        byte[]? packet;
        lock (_gate)
        {
            packet = _paused ? null : SensorPacketEncoder.CreateHeartbeat(Id, _session, ++_sequence, _state, _lastAcceptedCommand);
        }

        if (packet is not null)
        {
            await _transport.SendAsync(packet, backend, token);
        }
    }

    /// <summary>Builds the next measurement; caller holds the lock.</summary>
    private byte[] NextMeasurement()
    {
        if (Id == SensorId.Camera)
        {
            RandomNumberGenerator.Fill(_image);
            for (var alpha = 3; alpha < _image.Length; alpha += 4)
            {
                _image[alpha] = ProtocolConstants.OpaqueAlpha;
            }

            return SensorPacketEncoder.CreateCameraMeasurement(_session, ++_sequence, _image);
        }

        return SensorPacketEncoder.CreatePointerMeasurement(
            _session, ++_sequence, (byte)RandomNumberGenerator.GetInt32(10), (byte)RandomNumberGenerator.GetInt32(10));
    }
}
