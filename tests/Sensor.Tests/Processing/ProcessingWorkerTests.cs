using Microsoft.Extensions.Logging.Abstractions;
using Sensor.Communication.Protocol;
using Sensor.Control.Processing;

namespace Sensor.Tests.Processing;

public sealed class ProcessingWorkerTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);

    private static MeasurementPair Pair(byte x)
    {
        var pixels = new byte[400];
        for (var i = 0; i < 100; i++)
        {
            pixels[(4 * i) + 0] = (byte)(i % 10); // R = column, so the result identifies the pair.
            pixels[(4 * i) + 3] = 255;
        }

        return new MeasurementPair(CameraImage.FromRgba(pixels), new PixelPosition(x, 0), 1, x, 0, 0);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(WaitLimit);
        while (!condition())
        {
            await Task.Delay(5, cts.Token);
        }
    }

    [Fact]
    public async Task PixelLookupProcessor_ReturnsPixelAtPosition()
    {
        var pair = Pair(6);

        var color = await new PixelLookupProcessor().ProcessAsync(pair.Image, pair.Position, TestContext.Current.CancellationToken);

        Assert.Equal(new RgbaColor(6, 0, 0, 255), color);
    }

    [Fact]
    public async Task Submit_PublishesResult()
    {
        await using var run = new WorkerRun(new PixelLookupProcessor());
        var results = new List<ProcessingResult>();
        run.Worker.ResultProduced += results.Add;

        run.Worker.Submit(Pair(4));

        await WaitUntil(() => run.Worker.LatestResult is not null);
        Assert.Equal(new RgbaColor(4, 0, 0, 255), run.Worker.LatestResult!.Color);
        Assert.Equal(4UL, run.Worker.LatestResult.Pair.PointerSequence);
        Assert.Single(results);
    }

    [Fact]
    public async Task SlowProcessor_LatestWins_IntermediatePairDropped()
    {
        var processor = new GatedProcessor();
        await using var run = new WorkerRun(processor);

        run.Worker.Submit(Pair(1));
        await processor.WaitForStartAsync(); // Pair 1 in progress.
        run.Worker.Submit(Pair(2));          // Waits...
        run.Worker.Submit(Pair(3));          // ...and is replaced by 3.
        processor.ReleaseAll();

        await WaitUntil(() => run.Worker.ProcessedPairs == 2);
        Assert.Equal(new RgbaColor(3, 0, 0, 255), run.Worker.LatestResult!.Color);
        Assert.Equal(1, run.Worker.DroppedPairs);
        Assert.Equal([1, 3], processor.Started);
    }

    [Fact]
    public async Task Reset_CancelsInFlightAndDropsWaiting()
    {
        var processor = new GatedProcessor();
        await using var run = new WorkerRun(processor);
        run.Worker.Submit(Pair(1));
        await processor.WaitForStartAsync();
        run.Worker.Submit(Pair(2));

        run.Worker.Reset();

        await WaitUntil(() => processor.CancelledCount == 1);
        run.Worker.Submit(Pair(5)); // Next run works normally.
        processor.ReleaseAll();
        await WaitUntil(() => run.Worker.LatestResult is not null);
        Assert.Equal(new RgbaColor(5, 0, 0, 255), run.Worker.LatestResult!.Color);
        Assert.Equal([1, 5], processor.Started);
    }

    [Fact]
    public async Task Reset_ResultFinishingAfterReset_IsDiscarded()
    {
        var processor = new GatedProcessor { IgnoreCancellation = true };
        await using var run = new WorkerRun(processor);
        run.Worker.Submit(Pair(1));
        await processor.WaitForStartAsync();

        run.Worker.Reset();
        processor.ReleaseAll(); // Completes anyway, but belongs to the stopped run.

        await WaitUntil(() => processor.CompletedCount == 1);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Null(run.Worker.LatestResult);
        Assert.Equal(0, run.Worker.ProcessedPairs);
    }

    [Fact]
    public async Task ProcessorThrows_WorkerKeepsRunning()
    {
        var processor = new ThrowOnceProcessor();
        await using var run = new WorkerRun(processor);

        run.Worker.Submit(Pair(1));
        await WaitUntil(() => processor.Calls == 1);
        run.Worker.Submit(Pair(2));

        await WaitUntil(() => run.Worker.LatestResult is not null);
        Assert.Equal(new RgbaColor(2, 0, 0, 255), run.Worker.LatestResult!.Color);
    }

    /// <summary>Runs a worker in the background for one test and stops it on dispose.</summary>
    private sealed class WorkerRun : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _task;

        public WorkerRun(IMeasurementProcessor processor)
        {
            Worker = new ProcessingWorker(processor, TimeProvider.System, NullLogger<ProcessingWorker>.Instance);
            _task = Worker.RunAsync(_stop.Token);
        }

        public ProcessingWorker Worker { get; }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _task.WaitAsync(WaitLimit);
            _stop.Dispose();
        }
    }

    /// <summary>A "slow" processor: each computation waits until the test releases it.</summary>
    private sealed class GatedProcessor : IMeasurementProcessor
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim _started = new(0);
        private int _cancelled;
        private int _completed;

        public bool IgnoreCancellation { get; init; }

        public List<int> Started { get; } = [];

        public int CancelledCount => Volatile.Read(ref _cancelled);

        public int CompletedCount => Volatile.Read(ref _completed);

        public Task WaitForStartAsync() => _started.WaitAsync(WaitLimit);

        public void ReleaseAll() => _release.TrySetResult();

        public async ValueTask<RgbaColor> ProcessAsync(CameraImage image, PixelPosition position, CancellationToken cancellationToken)
        {
            lock (Started)
            {
                Started.Add(position.X);
            }

            _started.Release();
            try
            {
                await _release.Task.WaitAsync(IgnoreCancellation ? CancellationToken.None : cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _cancelled);
                throw;
            }

            Interlocked.Increment(ref _completed);
            return image.GetPixel(position);
        }
    }

    private sealed class ThrowOnceProcessor : IMeasurementProcessor
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask<RgbaColor> ProcessAsync(CameraImage image, PixelPosition position, CancellationToken cancellationToken) =>
            Interlocked.Increment(ref _calls) == 1
                ? throw new InvalidOperationException("Simulated algorithm failure.")
                : ValueTask.FromResult(image.GetPixel(position));
    }
}
