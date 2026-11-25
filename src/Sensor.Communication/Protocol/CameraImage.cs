namespace Sensor.Communication.Protocol;

/// <summary>
/// An immutable 10×10 RGBA Camera image: 400 bytes, row-major, top row first, 4 bytes per pixel
/// in R, G, B, A order.
/// </summary>
/// <remarks>
/// The bytes are copied out of the (reused) receive buffer on creation, so an image stays valid
/// after the next datagram is received and can be cached and shared across threads safely.
/// </remarks>
public sealed class CameraImage
{
    private readonly byte[] _pixels;

    private CameraImage(byte[] pixels) => _pixels = pixels;

    /// <summary>Raw RGBA bytes (400), row-major, top row first.</summary>
    public ReadOnlySpan<byte> Pixels => _pixels;

    /// <summary>Raw RGBA bytes as read-only memory (e.g. for serialization).</summary>
    public ReadOnlyMemory<byte> PixelMemory => _pixels;

    /// <summary>Creates an image by copying exactly <see cref="ProtocolConstants.CameraPayloadSize"/> RGBA bytes.</summary>
    /// <param name="rgbaPixels">400 bytes of row-major RGBA data.</param>
    /// <returns>A new image owning its own copy of the bytes.</returns>
    /// <exception cref="ArgumentException">The span is not exactly 400 bytes.</exception>
    public static CameraImage FromRgba(ReadOnlySpan<byte> rgbaPixels)
    {
        if (rgbaPixels.Length != ProtocolConstants.CameraPayloadSize)
        {
            throw new ArgumentException(
                $"A Camera image must be exactly {ProtocolConstants.CameraPayloadSize} bytes.", nameof(rgbaPixels));
        }

        return new CameraImage(rgbaPixels.ToArray());
    }

    /// <summary>Returns the color of the pixel at <paramref name="position"/>.</summary>
    /// <param name="position">A position inside the 10×10 image.</param>
    /// <returns>The pixel's RGBA color.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The position is outside the image.</exception>
    public RgbaColor GetPixel(PixelPosition position)
    {
        if (!position.IsInsideImage)
        {
            throw new ArgumentOutOfRangeException(nameof(position), position, "Position is outside the 10×10 image.");
        }

        // Pixel (x, y) starts at payload offset 4 * (10 * y + x) — PROTOCOL.md, Camera measurement.
        var offset = ProtocolConstants.BytesPerPixel * ((ProtocolConstants.ImageWidth * position.Y) + position.X);
        return new RgbaColor(_pixels[offset], _pixels[offset + 1], _pixels[offset + 2], _pixels[offset + 3]);
    }
}
