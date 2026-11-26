using Sensor.Communication.Protocol;

namespace Sensor.Control.Processing;

/// <summary>The exercise's processing: the RGBA color of the Camera pixel at the Pointer position.</summary>
/// <remarks>Completes synchronously, so the <see cref="ValueTask{TResult}"/> allocates nothing.</remarks>
public sealed class PixelLookupProcessor : IMeasurementProcessor
{
    /// <inheritdoc />
    public ValueTask<RgbaColor> ProcessAsync(CameraImage image, PixelPosition position, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        return ValueTask.FromResult(image.GetPixel(position));
    }
}
