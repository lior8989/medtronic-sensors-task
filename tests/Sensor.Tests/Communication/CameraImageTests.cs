using Sensor.Communication.Protocol;

namespace Sensor.Tests.Communication;

public sealed class CameraImageTests
{
    /// <summary>Every pixel encodes its own coordinates: R = x, G = y, B = pixel index, A = 255.</summary>
    private static CameraImage CoordinateImage()
    {
        var pixels = new byte[400];
        for (var i = 0; i < 100; i++)
        {
            pixels[(4 * i) + 0] = (byte)(i % 10);
            pixels[(4 * i) + 1] = (byte)(i / 10);
            pixels[(4 * i) + 2] = (byte)i;
            pixels[(4 * i) + 3] = 255;
        }

        return CameraImage.FromRgba(pixels);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(6, 1)]
    [InlineData(7, 3)]
    [InlineData(9, 9)]
    public void GetPixel_ReadsRowMajorOffset(byte x, byte y)
    {
        var color = CoordinateImage().GetPixel(new PixelPosition(x, y));

        Assert.Equal(new RgbaColor(x, y, (byte)((10 * y) + x), 255), color);
    }

    [Theory]
    [InlineData(10, 0)]
    [InlineData(0, 10)]
    public void GetPixel_OutsideImage_Throws(byte x, byte y) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => CoordinateImage().GetPixel(new PixelPosition(x, y)));

    [Fact]
    public void FromRgba_CopiesInput()
    {
        var source = new byte[400];
        var image = CameraImage.FromRgba(source);

        source[0] = 99;

        Assert.Equal(0, image.Pixels[0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(401)]
    public void FromRgba_WrongLength_Throws(int length) =>
        Assert.Throws<ArgumentException>(() => CameraImage.FromRgba(new byte[length]));
}
