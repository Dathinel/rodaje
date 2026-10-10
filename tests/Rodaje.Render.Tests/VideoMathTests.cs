using Rodaje.Media.Video;

namespace Rodaje.Render.Tests;

public sealed class VideoMathTests
{
    [Theory]
    [InlineData(1920, 1080, 1920, 1080, 0, 0, 1920, 1080)]
    [InlineData(1000, 1000, 1920, 1080, 0, 218, 1000, 563)]
    [InlineData(1920, 1080, 1080, 1920, 656, 0, 608, 1080)]
    [InlineData(800, 600, 720, 576, 25, 0, 750, 600)]
    public void Fit_letterboxes_and_pillarboxes(int tw, int th, int dw, int dh, int x, int y, int w, int h) =>
        Assert.Equal(new PixelRect(x, y, w, h), VideoLayout.Fit(tw, th, dw, dh));

    [Theory]
    [InlineData(0, 100, 100)]
    [InlineData(100, 0, 100)]
    [InlineData(100, 100, 0)]
    public void Fit_with_nothing_to_show_is_empty(int tw, int th, int dw) =>
        Assert.True(VideoLayout.Fit(tw, th, dw, 100).IsEmpty);

    [Fact]
    public void Corners_without_rotation_crop_the_decoder_padding()
    {
        QuadTexCoords q = VideoLayout.Corners(0, 0.5f, 0.9375f);
        Assert.Equal(new TexCoord(0, 0), q.TopLeft);
        Assert.Equal(new TexCoord(0.5f, 0), q.TopRight);
        Assert.Equal(new TexCoord(0, 0.9375f), q.BottomLeft);
        Assert.Equal(new TexCoord(0.5f, 0.9375f), q.BottomRight);
    }

    [Fact]
    public void Rotating_90_clockwise_brings_the_bottom_left_to_the_top_left()
    {
        QuadTexCoords q = VideoLayout.Corners(90, 1, 1);
        Assert.Equal(new TexCoord(0, 1), q.TopLeft);
        Assert.Equal(new TexCoord(0, 0), q.TopRight);
        Assert.Equal(new TexCoord(1, 1), q.BottomLeft);
        Assert.Equal(new TexCoord(1, 0), q.BottomRight);
    }

    [Fact]
    public void Rotations_compose_like_quarter_turns()
    {
        QuadTexCoords r180 = VideoLayout.Corners(180, 1, 1);
        Assert.Equal(new TexCoord(1, 1), r180.TopLeft);
        Assert.Equal(new TexCoord(0, 0), r180.BottomRight);

        QuadTexCoords r270 = VideoLayout.Corners(270, 1, 1);
        Assert.Equal(new TexCoord(1, 0), r270.TopLeft);
        Assert.Equal(new TexCoord(0, 1), r270.BottomRight);
    }

    [Theory]
    [InlineData(-90, 270)]
    [InlineData(450, 90)]
    [InlineData(359, 0)]
    [InlineData(180, 180)]
    public void Rotation_is_normalized_to_a_quarter_turn(int input, int expected) =>
        Assert.Equal(expected, VideoLayout.NormalizeRotation(input));

    [Theory]
    [InlineData(false, 16, 235, 128)]
    [InlineData(true, 64, 940, 512)]
    public void Limited_range_maps_black_white_and_neutral_chroma(bool tenBit, int black, int white, int neutral)
    {
        YuvRange range = YuvRange.For(tenBit, fullRange: false);
        // Lo que devuelve el muestreo UNORM para un código dado (P010 lo guarda en los 10 bits altos).
        float Sample(int code) => tenBit ? code * 64 / 65535f : code / 255f;

        Assert.Equal(0, (Sample(black) * range.LumaScale) + range.LumaBias, 4);
        Assert.Equal(1, (Sample(white) * range.LumaScale) + range.LumaBias, 4);
        Assert.Equal(0, (Sample(neutral) * range.ChromaScale) + range.ChromaBias, 4);
    }

    [Theory]
    [InlineData(false, 0, 255, 128)]
    [InlineData(true, 0, 1023, 512)]
    public void Full_range_maps_black_white_and_neutral_chroma(bool tenBit, int black, int white, int neutral)
    {
        YuvRange range = YuvRange.For(tenBit, fullRange: true);
        float Sample(int code) => tenBit ? code * 64 / 65535f : code / 255f;

        Assert.Equal(0, (Sample(black) * range.LumaScale) + range.LumaBias, 4);
        Assert.Equal(1, (Sample(white) * range.LumaScale) + range.LumaBias, 4);
        Assert.Equal(0, (Sample(neutral) * range.ChromaScale) + range.ChromaBias, 4);
    }

    [Fact]
    public void Bt709_coefficients_are_the_standard_ones()
    {
        YuvToRgb m = YuvToRgb.For(YuvMatrix.Bt709);
        Assert.Equal(1.5748, m.RedFromCr, 4);
        Assert.Equal(0.187324, m.GreenFromCb, 4);
        Assert.Equal(0.468124, m.GreenFromCr, 4);
        Assert.Equal(1.8556, m.BlueFromCb, 4);
        Assert.Equal(1.402, YuvToRgb.For(YuvMatrix.Bt601).RedFromCr, 4);
        Assert.Equal(1.772, YuvToRgb.For(YuvMatrix.Bt601).BlueFromCb, 4);
    }

    [Theory]
    [InlineData(YuvMatrix.Bt601, 0.299, 0.114, false, false)]
    [InlineData(YuvMatrix.Bt709, 0.2126, 0.0722, false, false)]
    [InlineData(YuvMatrix.Bt709, 0.2126, 0.0722, true, false)]
    [InlineData(YuvMatrix.Bt601, 0.299, 0.114, true, true)]
    [InlineData(YuvMatrix.Bt2020, 0.2627, 0.0593, true, true)]
    public void Encoded_colors_come_back_through_range_and_matrix(YuvMatrix matrix, double kr, double kb, bool tenBit, bool full)
    {
        YuvRange range = YuvRange.For(tenBit, full);
        YuvToRgb toRgb = YuvToRgb.For(matrix);
        double tolerance = (tenBit ? 4.0 / 876 : 4.0 / 219) * (full ? 1 : 1.2);
        foreach ((double r, double g, double b) in new[] { (1.0, 0.0, 0.0), (0.0, 1.0, 0.0), (0.0, 0.0, 1.0), (0.5, 0.5, 0.5), (0.9, 0.3, 0.1), (1.0, 1.0, 1.0) })
        {
            (double R, double G, double B) decoded = Decode(Encode(r, g, b, kr, kb, tenBit, full), range, toRgb, tenBit);
            Assert.Equal(r, decoded.R, tolerance);
            Assert.Equal(g, decoded.G, tolerance);
            Assert.Equal(b, decoded.B, tolerance);
        }
    }

    [Fact]
    public void Decoding_with_the_wrong_matrix_shifts_the_color()
    {
        (int, int, int) red601 = Encode(1, 0, 0, 0.299, 0.114, tenBit: false, full: false);
        (double R, double G, double B) as709 = Decode(red601, YuvRange.LimitedNv12, YuvToRgb.Bt709, tenBit: false);
        Assert.True(Math.Abs(as709.G) > 0.05 || Math.Abs(as709.R - 1) > 0.05, $"Rojo BT.601 leído como BT.709: {as709}.");
        (double R, double G, double B) asFull = Decode(red601, YuvRange.FullNv12, YuvToRgb.Bt601, tenBit: false);
        Assert.True(Math.Abs(asFull.R - 1) > 0.05, $"Rango limitado leído como completo: {asFull}.");
    }

    // Lo que guarda el códec: R'G'B' a Y'CbCr con los pesos de la norma, cuantizado al rango y a los bits pedidos.
    private static (int Y, int Cb, int Cr) Encode(double r, double g, double b, double kr, double kb, bool tenBit, bool full)
    {
        double y = (kr * r) + ((1 - kr - kb) * g) + (kb * b);
        double cb = (b - y) / (2 * (1 - kb));
        double cr = (r - y) / (2 * (1 - kr));
        int bits = tenBit ? 10 : 8;
        double max = (1 << bits) - 1;
        double step = 1 << (bits - 8);
        double center = 1 << (bits - 1);
        return full
            ? ((int)Math.Round(y * max), (int)Math.Min(max, Math.Round(center + (cb * max))), (int)Math.Min(max, Math.Round(center + (cr * max))))
            : ((int)Math.Round((16 + (219 * y)) * step), (int)Math.Round((128 + (224 * cb)) * step), (int)Math.Round((128 + (224 * cr)) * step));
    }

    // Lo que hacen los shaders con lo que devuelve el muestreo UNORM.
    private static (double R, double G, double B) Decode((int Y, int Cb, int Cr) code, YuvRange range, YuvToRgb m, bool tenBit)
    {
        double Sample(int c) => tenBit ? c * 64 / 65535.0 : c / 255.0;
        double y = (Sample(code.Y) * range.LumaScale) + range.LumaBias;
        double cb = (Sample(code.Cb) * range.ChromaScale) + range.ChromaBias;
        double cr = (Sample(code.Cr) * range.ChromaScale) + range.ChromaBias;
        return (y + (m.RedFromCr * cr), y - (m.GreenFromCb * cb) - (m.GreenFromCr * cr), y + (m.BlueFromCb * cb));
    }
}
