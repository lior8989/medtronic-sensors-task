using Microsoft.Extensions.Time.Testing;
using Sensor.Communication.Protocol;
using Sensor.Control.Processing;
using Sensor.Control.Sensors;
using Sensor.Tests.Fakes;

namespace Sensor.Tests.Processing;

public sealed class MeasurementPairerTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly RecordingProcessingQueue _queue = new();
    private readonly MeasurementPairer _pairer;
    private ulong _sequence;

    public MeasurementPairerTests() => _pairer = new MeasurementPairer(_queue, new PairingOptions(), _time);

    private void Image(byte red)
    {
        byte[] pixels = [.. Enumerable.Repeat<byte[]>([red, 0, 0, 255], 100).SelectMany(p => p)];
        Assert.True(SensorPacketDecoder.TryDecode(SensorPacketEncoder.CreateCameraMeasurement(1, ++_sequence, pixels), out var packet, out _));
        _pairer.OnRunningMeasurement(SensorId.Camera, new ReceivedMeasurement((MeasurementPacket)packet, _time.GetTimestamp()));
    }

    private void Pointer(byte x = 3, byte y = 7)
    {
        Assert.True(SensorPacketDecoder.TryDecode(SensorPacketEncoder.CreatePointerMeasurement(2, ++_sequence, x, y), out var packet, out _));
        _pairer.OnRunningMeasurement(SensorId.Pointer, new ReceivedMeasurement((MeasurementPacket)packet, _time.GetTimestamp()));
    }

    private void Elapse(int milliseconds) => _time.Advance(TimeSpan.FromMilliseconds(milliseconds));

    [Fact]
    public void Pointer_WithRecentImage_SubmitsPair()
    {
        Image(10);
        var imageTime = _time.GetTimestamp();
        Elapse(15);

        Pointer(4, 2);

        var pair = Assert.Single(_queue.Submitted);
        Assert.Equal(new PixelPosition(4, 2), pair.Position);
        Assert.Equal(10, pair.Image.GetPixel(pair.Position).R);
        Assert.Equal(imageTime, pair.CameraReceivedTimestamp);
        Assert.Equal(_time.GetTimestamp(), pair.PointerReceivedTimestamp);
        Assert.Equal(1, _pairer.PairsSubmitted);
    }

    [Fact]
    public void Pointer_UsesLatestImage()
    {
        Image(10);
        Elapse(20);
        Image(20);
        Elapse(5);

        Pointer();

        Assert.Equal(20, Assert.Single(_queue.Submitted).Image.Pixels[0]);
    }

    [Fact]
    public void Pointer_ImageExactlyAtMaxAge_IsPaired()
    {
        Image(10);
        Elapse(50);

        Pointer();

        Assert.Single(_queue.Submitted);
    }

    [Fact]
    public void Pointer_ImageOlderThanMaxAge_IsSkipped()
    {
        Image(10);
        Elapse(51);

        Pointer();

        Assert.Empty(_queue.Submitted);
        Assert.Equal(1, _pairer.SkippedStaleImage);
    }

    [Fact]
    public void Pointer_BeforeAnyImage_IsSkipped()
    {
        Pointer();

        Assert.Empty(_queue.Submitted);
        Assert.Equal(1, _pairer.SkippedNoImage);
    }

    [Fact]
    public void Images_AloneNeverTriggerPairs()
    {
        for (var i = 0; i < 10; i++)
        {
            Image((byte)i);
            Elapse(20);
        }

        Assert.Empty(_queue.Submitted);
    }

    [Fact]
    public void ImageAfterPointer_IsNotUsedForThatPointer()
    {
        Pointer();
        Elapse(5);
        Image(10);

        Assert.Empty(_queue.Submitted);
    }

    [Fact]
    public void OnProcessingStopped_ClearsCachedImageAndResetsQueue()
    {
        Image(10);

        _pairer.OnProcessingStopped();
        Pointer();

        Assert.Empty(_queue.Submitted);
        Assert.Equal(1, _queue.ResetCount);
        Assert.Equal(1, _pairer.SkippedNoImage);
    }
}
