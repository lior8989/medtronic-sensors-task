using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Sensor.Communication.Protocol;
using Sensor.Communication.Transport;
using Sensor.Control.Status;

namespace Sensor.Control.Supervision;

/// <summary>
/// The single event loop that drives the backend: a receive loop (datagram → decoded packet) and a
/// periodic tick both post events into one bounded <see cref="Channel{T}"/>; one consumer applies them
/// to the <see cref="SystemSupervisor"/> strictly in order, so no supervisor/sensor state needs locks.
/// </summary>
public sealed partial class SupervisorEventLoop
{
    /// <summary>
    /// Receive buffer size: larger than any UDP datagram over IPv4, so oversized datagrams are received
    /// whole and rejected by length instead of failing the receive. Allocated once and reused.
    /// </summary>
    internal const int ReceiveBufferSize = 64 * 1024;

    /// <summary>
    /// Event queue bound. Normal load is ~70 events/s and each is handled in microseconds, so this is
    /// only reached if the consumer stalls; then the receive loop waits and the OS socket buffer absorbs
    /// or drops datagrams (UDP is best-effort anyway), and surplus ticks are skipped.
    /// </summary>
    internal const int QueueCapacity = 1024;

    private readonly IDatagramTransport _transport;
    private readonly SystemSupervisor _supervisor;
    private readonly ISupervisorObserver _observer;
    private readonly SupervisorOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SupervisorEventLoop> _logger;

    /// <summary>Creates the event loop.</summary>
    /// <param name="transport">The backend's single bound socket.</param>
    /// <param name="supervisor">The state machine to drive.</param>
    /// <param name="observer">Notified after each event (e.g. to publish a status snapshot).</param>
    /// <param name="options">Timing settings (tick period).</param>
    /// <param name="timeProvider">Time source for the periodic tick (fake in tests).</param>
    /// <param name="logger">Logger.</param>
    public SupervisorEventLoop(
        IDatagramTransport transport,
        SystemSupervisor supervisor,
        ISupervisorObserver observer,
        SupervisorOptions options,
        TimeProvider timeProvider,
        ILogger<SupervisorEventLoop> logger)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(observer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _transport = transport;
        _supervisor = supervisor;
        _observer = observer;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Runs the receive loop, the tick loop and the consumer until cancelled.</summary>
    /// <param name="cancellationToken">Stops all three loops (e.g. the host's stopping token).</param>
    /// <returns>A task that completes when all loops have stopped.</returns>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var channel = Channel.CreateBounded<LoopEvent>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false, // Receive loop and tick loop both write.
            FullMode = BoundedChannelFullMode.Wait,
        });

        // If any loop stops unexpectedly, stop the others too instead of running half a system.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = linked.Token;

        Task[] loops =
        [
            RunAndCancelOthersAsync(ReceiveLoopAsync(channel.Writer, token), linked),
            RunAndCancelOthersAsync(TickLoopAsync(channel.Writer, token), linked),
            RunAndCancelOthersAsync(ConsumeLoopAsync(channel.Reader, token), linked),
        ];

        try
        {
            await Task.WhenAll(loops).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private static async Task RunAndCancelOthersAsync(Task loop, CancellationTokenSource linked)
    {
        try
        {
            await loop.ConfigureAwait(false);
        }
        finally
        {
            await linked.CancelAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Receives datagrams into one reused buffer, decodes them and queues valid packets.</summary>
    private async Task ReceiveLoopAsync(ChannelWriter<LoopEvent> writer, CancellationToken cancellationToken)
    {
        var buffer = new byte[ReceiveBufferSize];
        while (!cancellationToken.IsCancellationRequested)
        {
            DatagramReceiveResult received;
            try
            {
                received = await _transport.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                LogReceiveFailed(ex); // Transient for UDP; keep serving.
                continue;
            }

            // Decoding copies what must outlive the buffer (Camera image), so the buffer is reusable at once.
            if (!SensorPacketDecoder.TryDecode(buffer.AsSpan(0, received.BytesReceived), out var packet, out var error))
            {
                LogDecodeRejected(error, received.RemoteEndPoint, received.BytesReceived);
                continue;
            }

            await writer.WriteAsync(new LoopEvent(packet, received.RemoteEndPoint), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Posts a tick every <see cref="SupervisorOptions.TickPeriod"/>; a tick is skipped if the queue is full.</summary>
    private async Task TickLoopAsync(ChannelWriter<LoopEvent> writer, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.TickPeriod, _timeProvider);
        while (!cancellationToken.IsCancellationRequested
            && await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            writer.TryWrite(LoopEvent.Tick);
        }
    }

    /// <summary>The single consumer: the only code that touches supervisor and sensor state.</summary>
    private async Task ConsumeLoopAsync(ChannelReader<LoopEvent> reader, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var next = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (next.Packet is { } packet)
                {
                    await _supervisor.HandlePacketAsync(packet, next.Source!, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _supervisor.HandleTickAsync(cancellationToken).ConfigureAwait(false);
                }

                _observer.OnEventProcessed(_supervisor);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One faulty event must not stop supervision of the whole system.
                LogEventFailed(ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Rejected datagram from {Source} ({Length} bytes): {Reason}")]
    private partial void LogDecodeRejected(DecodeError reason, IPEndPoint source, int length);

    [LoggerMessage(Level = LogLevel.Warning, Message = "UDP receive failed")]
    private partial void LogReceiveFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected error while handling an event")]
    private partial void LogEventFailed(Exception exception);

    /// <summary>A queued event: a decoded packet with its source, or a tick (both fields null). A struct, so queuing allocates nothing.</summary>
    private readonly record struct LoopEvent(SensorPacket? Packet, IPEndPoint? Source)
    {
        public static LoopEvent Tick => default;
    }
}
