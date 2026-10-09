namespace Rodaje.TestMedia.Tests;

public class FrameCodeTests
{
    private static byte[] RenderLuma(int width, int height, int index)
    {
        var video = new SyntheticVideo(width, height);
        var frame = new byte[video.FrameSize];
        video.Render(frame, index);
        return frame[..(width * height)];
    }

    [Theory]
    [InlineData(1920, 1080, 64)]
    [InlineData(1280, 720, 40)]
    [InlineData(640, 360, 16)]
    [InlineData(80, 48, 8)]
    [InlineData(1080, 1920, 64)]
    public void Layout_ScalesWithFrameAndStaysAlignedTo8(int width, int height, int expectedCell)
    {
        FrameCode.Layout layout = FrameCode.GetLayout(width, height);

        Assert.Equal(expectedCell, layout.Cell);
        Assert.Equal(0, layout.X % 8);
        Assert.Equal(0, layout.Y % 8);
        (int px, int py, int pw, int ph) = layout.Panel;
        Assert.True(px >= 0 && py >= 0 && px + pw <= width && py + ph <= height);
    }

    [Theory]
    [InlineData(78, 48)]
    [InlineData(80, 46)]
    [InlineData(0, 100)]
    public void Layout_RejectsFramesTooSmall(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameCode.GetLayout(width, height));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(599)]
    [InlineData(17_981)]
    [InlineData(0x55555)]
    [InlineData(FrameCode.MaxIndex)]
    public void RoundTrip_OnRenderedFrame(int index)
    {
        byte[] luma = RenderLuma(640, 360, index);

        Assert.Equal(index, FrameCode.Read(luma, 640, 360, 640));
    }

    [Fact]
    public void RoundTrip_WithStrideLargerThanWidth()
    {
        const int width = 320;
        const int height = 240;
        const int stride = 384;
        var plane = new byte[stride * height];
        plane.AsSpan().Fill(77);

        FrameCode.Draw(plane, width, height, stride, 123_456);

        Assert.Equal(123_456, FrameCode.Read(plane, width, height, stride));
        Assert.Equal(77, plane[width]);
    }

    [Fact]
    public void Draw_RejectsIndexOutOfRange()
    {
        var plane = new byte[320 * 240];

        Assert.Throws<ArgumentOutOfRangeException>(() => FrameCode.Draw(plane, 320, 240, 320, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => FrameCode.Draw(plane, 320, 240, 320, FrameCode.MaxIndex + 1));
    }

    [Fact]
    public void Read_RejectsShortPlane()
    {
        Assert.Throws<ArgumentException>(() => FrameCode.Read(new byte[100], 320, 240, 320));
    }

    [Fact]
    public void RoundTrip_ManyIndices_WithNoiseBlurAndQuantization()
    {
        const int width = 640;
        const int height = 360;
        var random = new Random(20261009);
        for (int n = 0; n < 40; n++)
        {
            int index = random.Next(FrameCode.MaxIndex + 1);
            byte[] luma = RenderLuma(width, height, index);

            Degrade.AddNoise(luma, amplitude: 25, seed: n);
            luma = Degrade.BoxBlur(luma, width, height, radius: 2);
            Degrade.JpegLikeQuantize(luma, width, height, scale: 4);

            Assert.Equal(index, FrameCode.Read(luma, width, height, width));
        }
    }

    [Fact]
    public void RoundTrip_FullRangeGray()
    {
        // ffmpeg convierte YUV de rango limitado a gris de rango completo: 16 → 0 y 235 → 255.
        byte[] luma = RenderLuma(640, 360, 4242);
        for (int i = 0; i < luma.Length; i++)
        {
            luma[i] = (byte)Math.Clamp((luma[i] - 16) * 255 / 219, 0, 255);
        }

        Assert.Equal(4242, FrameCode.Read(luma, 640, 360, 640));
    }

    [Fact]
    public void Read_ReturnsNull_WhenOneDataCellFlips()
    {
        const int width = 640;
        const int height = 360;
        byte[] luma = RenderLuma(width, height, 1000);
        FrameCode.Layout layout = FrameCode.GetLayout(width, height);
        int cellX = layout.X + 3 * layout.Cell;
        int cellY = layout.Y + 2 * layout.Cell;
        byte flipped = luma[(cellY + layout.Cell / 2) * width + cellX + layout.Cell / 2] == FrameCode.White ? FrameCode.Black : FrameCode.White;
        for (int y = cellY; y < cellY + layout.Cell; y++)
        {
            luma.AsSpan(y * width + cellX, layout.Cell).Fill(flipped);
        }

        Assert.Null(FrameCode.Read(luma, width, height, width));
    }

    [Fact]
    public void Read_ReturnsNull_WithoutCode()
    {
        var gray = new byte[640 * 360];
        gray.AsSpan().Fill(128);
        var noise = new byte[640 * 360];
        new Random(1).NextBytes(noise);

        Assert.Null(FrameCode.Read(gray, 640, 360, 640));
        Assert.Null(FrameCode.Read(noise, 640, 360, 640));
    }

    [Fact]
    public void Read_ReturnsNull_WhenCellsAreAmbiguous()
    {
        byte[] luma = RenderLuma(640, 360, 77);
        // Mezcla con gris medio hasta que blanco y negro casi se tocan.
        for (int i = 0; i < luma.Length; i++)
        {
            luma[i] = (byte)(128 + (luma[i] - 128) / 8);
        }

        Assert.Null(FrameCode.Read(luma, 640, 360, 640));
    }

    [Fact]
    public void Crc_DetectsEverySingleBitError()
    {
        for (int index = 0; index < 4096; index += 37)
        {
            for (int bit = 0; bit < FrameCode.IndexBits; bit++)
            {
                Assert.NotEqual(FrameCode.Crc8(index), FrameCode.Crc8(index ^ (1 << bit)));
            }
        }
    }

    [Fact]
    public void DrawI420_NeutralizesChromaUnderPanel()
    {
        var video = new SyntheticVideo(640, 360);
        var frame = new byte[video.FrameSize];
        video.Render(frame, 5);
        (int px, int py, int pw, int ph) = FrameCode.GetLayout(640, 360).Panel;
        int lumaSize = 640 * 360;
        int chromaSize = lumaSize / 4;

        for (int y = py / 2; y < (py + ph) / 2; y++)
        {
            for (int x = px / 2; x < (px + pw) / 2; x++)
            {
                Assert.Equal(128, frame[lumaSize + y * 320 + x]);
                Assert.Equal(128, frame[lumaSize + chromaSize + y * 320 + x]);
            }
        }
    }
}
