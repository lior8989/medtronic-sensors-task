using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Sensor.Control.Processing;

/// <summary>
/// Runs the (potentially expensive) <see cref="IMeasurementProcessor"/> off the event loop, one pair at a
/// time, with "latest wins" queuing: at most one pair waits, and a newer pair replaces it.
/// </summary>
/// <remarks>
/// <para>
/// A slow computation therefore never builds a backlog (memory stays flat, results stay current) and
/// never delays heartbeat handling, which would cause false silence faults.
/// </para>
/// <para>
/// <see cref="Reset"/> (system left Running) bumps an epoch, drops the waiting pair and cancels the
/// computation in progress; any result belonging to an older epoch is discarded, so nothing from a
/// previous run is published late. <see cref="Submit"/>/<see cref="Reset"/> are called from the event
/// loop, <see cref="RunAsync"/> runs on its own task; shared fields are guarded accordingly.
/// </para>
/// </remarks>
public sealed partial class ProcessingWorker : IProcessingQueue
{
    private readonly IMeasurementProcessor _processor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ProcessingWorker> _logger;
    private readonly Channel<WorkItem> _channel;

    // Guards _inFlight (swapped by the worker, cancelled by Reset).
    private readonly Lock _gate = new();
    private CancellationTokenSource? _inFlight;

    private long _epoch;
    private long _droppedPairs;
    private long _processedPairs;
    private ProcessingResult? _latestResult;

    /// <summary>Creates the worker.</summary>
    /// <param name="processor">The computation strategy.</param>
    /// <param name="timeProvider">Time source for result timestamps.</param>
    /// <param name="logger">Logger.</param>
    public ProcessingWorker(IMeasurementProcessor processor, TimeProvider timeProvider, ILogger<ProcessingWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(processor);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _processor = processor;
        _timeProvider = timeProvider;
        _logger = logger;

        // Capacity 1 + DropOldest = "latest wins"; the callback counts replaced pairs.
        _channel = Channel.CreateBounded<WorkItem>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true },
            _ => Interlocked.Increment(ref _droppedPairs));
    }

    /// <summary>Raised after each published result, on the worker task.</summary>
    public event Action<ProcessingResult>? ResultProduced;

    /// <summary>The most recent published result (kept after processing stops, for display), or <see langword="null"/>.</summary>
    public ProcessingResult? LatestResult => Volatile.Read(ref _latestResult);

    /// <summary>Pairs replaced while waiting (latest wins).</summary>
    public long DroppedPairs => Interlocked.Read(ref _droppedPairs);

    /// <summary>Pairs whose result was published.</summary>
    public long ProcessedPairs => Interlocked.Read(ref _processedPairs);

    /// <inheritdoc />
    public void Submit(MeasurementPair pair)
    {
        ArgumentNullException.ThrowIfNull(pair);
        _channel.Writer.TryWrite(new WorkItem(pair, Interlocked.Read(ref _epoch))); // Never fails with DropOldest.
    }

    /// <inheritdoc />
    public void Reset()
    {
        Interlocked.Increment(ref _epoch);

        while (_channel.Reader.TryRead(out _))
        {
            // Drain the waiting pair (not counted as "dropped": it was discarded on purpose).
        }

        lock (_gate)
        {
            _inFlight?.Cancel();
        }
    }

    /// <summary>Processes pairs until cancelled.</summary>
    /// <param name="cancellationToken">Stops the worker (e.g. the host's stopping token).</param>
    /// <returns>A task that completes when the worker stops.</returns>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var item = await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                await ProcessAsync(item, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task ProcessAsync(WorkItem item, CancellationToken stoppingToken)
    {
        if (item.Epoch != Interlocked.Read(ref _epoch))
        {
            return; // Queued before the last Reset.
        }

        using var computation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        lock (_gate)
        {
            _inFlight = computation;
        }

        try
        {
            var color = await _processor.ProcessAsync(item.Pair.Image, item.Pair.Position, computation.Token).ConfigureAwait(false);

            // Re-check: a Reset during the computation means this result belongs to a finished run.
            if (item.Epoch != Interlocked.Read(ref _epoch))
            {
                return;
            }

            var result = new ProcessingResult(item.Pair, color, _timeProvider.GetTimestamp());
            Volatile.Write(ref _latestResult, result);
            Interlocked.Increment(ref _processedPairs);
            ResultProduced?.Invoke(result);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // Cancelled by Reset: discard.
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogProcessingFailed(ex); // One failed computation must not stop the worker.
        }
        finally
        {
            lock (_gate)
            {
                _inFlight = null;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Processing a measurement pair failed")]
    private partial void LogProcessingFailed(Exception exception);

    /// <summary>A queued pair tagged with the epoch it was submitted in.</summary>
    private readonly record struct WorkItem(MeasurementPair Pair, long Epoch);
}
