using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Sensor.Communication.Protocol;
using Sensor.Communication.Transport;

namespace Sensor.Control.Sensors;

/// <summary>
/// The backend's controller for one remote sensor (a Proxy for it): tracks its session, output
/// sequence, reported state, freshness and latest measurement, and sends it Start/Stop commands.
/// </summary>
/// <remarks>
/// <para>
/// Not thread-safe by design. All calls come from the single event-loop consumer, which processes
/// packets and timer ticks strictly in order, so no locking is needed.
/// </para>
/// <para>
/// Responsibilities are the stateful protocol rules only (PROTOCOL.md: sessions, sequences, command
/// sequencing). Byte-level validation is done earlier by <see cref="SensorPacketDecoder"/>; system
/// decisions (when to Start/Stop, faults) belong to the state machine.
/// </para>
/// </remarks>
public sealed partial class SensorControl
{
    /// <summary>
    /// How many retired session IDs are remembered. Restarts are rare (operator action), so this
    /// bounds memory while still ignoring late packets from any recent previous session.
    /// </summary>
    internal const int RetiredSessionCapacity = 32;

    private readonly IDatagramTransport _transport;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SensorControl> _logger;

    // Retired sessions: set for O(1) lookup, queue to evict the oldest when the capacity is reached.
    private readonly HashSet<ulong> _retiredSessions = [];
    private readonly Queue<ulong> _retiredOrder = new();

    // Reused command buffer; safe because the class is single-threaded.
    private readonly byte[] _commandBuffer = new byte[SensorPacketEncoder.CommandPacketSize];

    private ulong _lastSequence;
    private ulong _lastSentCommandSequence;

    /// <summary>Creates the controller for one sensor.</summary>
    /// <param name="sensorId">Which sensor this controls.</param>
    /// <param name="endpoint">The sensor's command endpoint; also the only accepted source of its packets.</param>
    /// <param name="transport">The backend's single bound socket (shared by both sensors).</param>
    /// <param name="timeProvider">Monotonic time source (fake in tests).</param>
    /// <param name="logger">Logger.</param>
    public SensorControl(
        SensorId sensorId,
        IPEndPoint endpoint,
        IDatagramTransport transport,
        TimeProvider timeProvider,
        ILogger<SensorControl> logger)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        SensorId = sensorId;
        Endpoint = endpoint;
        _transport = transport;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Which sensor this controls.</summary>
    public SensorId SensorId { get; }

    /// <summary>The sensor's endpoint: accepted packet source and command destination.</summary>
    public IPEndPoint Endpoint { get; }

    /// <summary>The current session ID, or <see langword="null"/> before any valid traffic.</summary>
    public ulong? SessionId { get; private set; }

    /// <summary>True once any valid traffic has been accepted from this sensor.</summary>
    public bool HasBeenSeen => SessionId is not null;

    /// <summary>
    /// Latest state reported by a heartbeat of the current session, or <see langword="null"/> when the
    /// current session has not sent a heartbeat yet.
    /// </summary>
    public SensorState? State { get; private set; }

    /// <summary>
    /// Timestamp of the heartbeat that first reported the current <see cref="State"/> (i.e. when the
    /// reported state last changed), or <see langword="null"/> if no state is known.
    /// </summary>
    public long? StateChangedTimestamp { get; private set; }

    /// <summary>Last accepted command sequence reported by the latest heartbeat (0 if none).</summary>
    public ulong LastAcceptedCommandSequence { get; private set; }

    /// <summary>Timestamp of the latest valid traffic (heartbeat or measurement) of the current session.</summary>
    public long? LastValidTrafficTimestamp { get; private set; }

    /// <summary>Timestamp of the latest accepted heartbeat of the current session.</summary>
    public long? LastHeartbeatTimestamp { get; private set; }

    /// <summary>Latest accepted measurement of the current session, or <see langword="null"/>.</summary>
    public ReceivedMeasurement? LatestMeasurement { get; private set; }

    /// <summary>The most recent command handed to the OS, or <see langword="null"/>.</summary>
    public SensorCommand? LastSentCommand { get; private set; }

    /// <summary>Timestamp of <see cref="LastSentCommand"/> (used by retry policies).</summary>
    public long? LastSentCommandTimestamp { get; private set; }

    /// <summary>Time elapsed since the latest valid traffic, or <see langword="null"/> if never seen.</summary>
    /// <returns>The elapsed time on the monotonic clock.</returns>
    public TimeSpan? GetSilenceDuration() =>
        LastValidTrafficTimestamp is { } last ? _timeProvider.GetElapsedTime(last) : null;

    /// <summary>
    /// Applies one decoded packet if it is valid traffic for this sensor (source endpoint, sensor ID,
    /// session and sequence rules). Rejected packets change nothing, including freshness.
    /// </summary>
    /// <param name="packet">A packet that already passed byte-level validation.</param>
    /// <param name="source">The datagram's source endpoint.</param>
    /// <returns>Whether and how the packet was applied.</returns>
    public PacketDisposition Accept(SensorPacket packet, IPEndPoint source)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(source);

        var disposition = packet is CommandPacket
            ? PacketDisposition.UnexpectedMessageType
            : Classify(packet.Header, source);

        if (disposition is not (PacketDisposition.Accepted or PacketDisposition.AcceptedNewSession))
        {
            LogRejected(SensorId, disposition, packet.Header.SessionId, packet.Header.Sequence);
            return disposition;
        }

        if (disposition == PacketDisposition.AcceptedNewSession)
        {
            StartSession(packet.Header.SessionId);
        }

        Apply(packet);
        return disposition;
    }

    /// <summary>Discards the cached measurement (e.g. when processing stops, so in-flight data is never paired later).</summary>
    public void ClearLatestMeasurement() => LatestMeasurement = null;

    /// <summary>
    /// Sends a command to the sensor's current session with the next command sequence. Best-effort:
    /// a send failure is logged and reported, and the sequence is still consumed so a retry is a new command.
    /// </summary>
    /// <param name="command">Start or Stop.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns><see langword="true"/> if handed to the OS; <see langword="false"/> if no session is known or sending failed.</returns>
    public async ValueTask<bool> SendCommandAsync(SensorCommand command, CancellationToken cancellationToken)
    {
        if (SessionId is not { } sessionId)
        {
            return false; // A command must echo the current session ID, learned from the sensor's traffic.
        }

        // Strictly above both our own last command and the sensor's last accepted one: after a backend
        // restart our counter is 0, but the sensor's heartbeat tells us where it stands.
        var sequence = Math.Max(_lastSentCommandSequence, LastAcceptedCommandSequence) + 1;
        _lastSentCommandSequence = sequence;

        var length = SensorPacketEncoder.WriteCommand(_commandBuffer, SensorId, sessionId, sequence, command);
        try
        {
            await _transport.SendAsync(_commandBuffer.AsMemory(0, length), Endpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            LogCommandFailed(ex, SensorId, command, sequence);
            return false;
        }

        LastSentCommand = command;
        LastSentCommandTimestamp = _timeProvider.GetTimestamp();
        LogCommandSent(SensorId, command, sequence, sessionId);
        return true;
    }

    /// <summary>Decides whether a packet is valid traffic, without changing any state.</summary>
    private PacketDisposition Classify(PacketHeader header, IPEndPoint source)
    {
        if (!Endpoint.Equals(source))
        {
            return PacketDisposition.WrongSource;
        }

        if (header.SensorId != SensorId)
        {
            return PacketDisposition.WrongSensor;
        }

        if (header.SessionId == SessionId)
        {
            // One output counter is shared by heartbeats and measurements: only strictly newer packets count.
            return header.Sequence > _lastSequence ? PacketDisposition.Accepted : PacketDisposition.StaleSequence;
        }

        // A retired session must never switch tracking back to it; any other unknown ID is a new boot.
        return _retiredSessions.Contains(header.SessionId)
            ? PacketDisposition.RetiredSession
            : PacketDisposition.AcceptedNewSession;
    }

    /// <summary>Retires the current session and resets all per-session tracking.</summary>
    private void StartSession(ulong newSessionId)
    {
        if (SessionId is { } previous)
        {
            Retire(previous);
            LogSessionChanged(SensorId, previous, newSessionId);
        }
        else
        {
            LogFirstSeen(SensorId, newSessionId);
        }

        SessionId = newSessionId;
        _lastSequence = 0;
        _lastSentCommandSequence = 0;
        State = null;
        StateChangedTimestamp = null;
        LastAcceptedCommandSequence = 0;
        LastHeartbeatTimestamp = null;
        LatestMeasurement = null;
        LastSentCommand = null;
        LastSentCommandTimestamp = null;
    }

    private void Retire(ulong sessionId)
    {
        if (_retiredSessions.Count == RetiredSessionCapacity)
        {
            _retiredSessions.Remove(_retiredOrder.Dequeue());
        }

        _retiredSessions.Add(sessionId);
        _retiredOrder.Enqueue(sessionId);
    }

    /// <summary>Applies valid traffic: freshness for every packet, then type-specific state.</summary>
    private void Apply(SensorPacket packet)
    {
        var now = _timeProvider.GetTimestamp();
        _lastSequence = packet.Header.Sequence;
        LastValidTrafficTimestamp = now;

        switch (packet)
        {
            case HeartbeatPacket heartbeat:
                if (State != heartbeat.State)
                {
                    LogStateReported(SensorId, State?.ToString() ?? "unknown", heartbeat.State);
                    StateChangedTimestamp = now;
                }

                State = heartbeat.State;
                LastAcceptedCommandSequence = heartbeat.LastAcceptedCommandSequence;
                LastHeartbeatTimestamp = now;
                break;

            case MeasurementPacket measurement:
                LatestMeasurement = new ReceivedMeasurement(measurement, now);
                break;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Sensor} first seen with session {SessionId:X16}")]
    private partial void LogFirstSeen(SensorId sensor, ulong sessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Sensor} new session {NewSessionId:X16} (retired {OldSessionId:X16})")]
    private partial void LogSessionChanged(SensorId sensor, ulong oldSessionId, ulong newSessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Sensor} reports {NewState} (was {OldState})")]
    private partial void LogStateReported(SensorId sensor, string oldState, SensorState newState);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Sensor} rejected packet: {Reason} (session {SessionId:X16}, sequence {Sequence})")]
    private partial void LogRejected(SensorId sensor, PacketDisposition reason, ulong sessionId, ulong sequence);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Sensor} <- {Command} (command sequence {Sequence}, session {SessionId:X16})")]
    private partial void LogCommandSent(SensorId sensor, SensorCommand command, ulong sequence, ulong sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Sensor} <- {Command} (command sequence {Sequence}) send failed")]
    private partial void LogCommandFailed(Exception exception, SensorId sensor, SensorCommand command, ulong sequence);
}
