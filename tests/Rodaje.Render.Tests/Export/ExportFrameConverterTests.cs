using Rodaje.Core.Timing;
using Rodaje.Media.Export;
using Rodaje.Render.Export;
using Xunit.Abstractions;

namespace Rodaje.Render.Tests.Export;

/// <summary>
/// Fidelidad del conversor en WARP (corre también en la CI, sin GPU) contra <see cref="ExportReference"/>: cada formato
/// de píxel, matrices y rangos, escalas, formas, mezcla, anillo y miniaturas.
/// </summary>
public sealed class ExportFrameConverterTests(ITestOutputHelper output)
{
    private static readonly Rational Rate = new(30, 1);

    public static TheoryData<ExportPixelFormat> AllFormats()
    {
        var data = new TheoryData<ExportPixelFormat>();
        foreach (ExportPixelFormat format in Enum.GetValues<ExportPixelFormat>())
        {
            data.Add(format);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllFormats))]
    public void Every_pixel_format_matches_the_reference(ExportPixelFormat pixels)
    {
        using ExportGpu gpu = ExportGpu.Warp();
        var format = new ExportVideoFormat(62, 36, Rate, pixels, ExportColor.For(1920, 1080, pixels));
        Picture source = Picture.Test(62, 36, alpha: pixels.HasAlpha());
        ExportCheck.Compare(output, gpu, source, format, tolerance: 1);
    }

    [Theory]
    [InlineData(ExportPixelFormat.Nv12)]
    [InlineData(ExportPixelFormat.Rgb24)]
    [InlineData(ExportPixelFormat.Yuv444P)]
    [InlineData(ExportPixelFormat.Rgba)]
    public void Odd_sizes_pad_rows_without_touching_the_samples(ExportPixelFormat pixels)
    {
        using ExportGpu gpu = ExportGpu.Warp();
        (int width, int height) = pixels.NeedsEvenWidth() ? (58, 34) : (61, 35);
        var format = new ExportVideoFormat(width, height, Rate, pixels, ExportColor.For(1920, 1080, pixels));
        ExportCheck.Compare(output, gpu, Picture.Test(width, height, pixels.HasAlpha()), format, tolerance: 1);
    }

    [Theory]
    [InlineData(ExportMatrix.Bt601, false, ExportPixelFormat.Nv12)]
    [InlineData(ExportMatrix.Bt709, true, ExportPixelFormat.Nv12)]
    [InlineData(ExportMatrix.Bt601, true, ExportPixelFormat.Yuv444P10)]
    [InlineData(ExportMatrix.Bt709, true, ExportPixelFormat.P010)]
    public void Matrix_and_range_follow_the_format(ExportMatrix matrix, bool fullRange, ExportPixelFormat pixels)
    {
        using ExportGpu gpu = ExportGpu.Warp();
        var format = new ExportVideoFormat(64, 36, Rate, pixels, new ExportColor(matrix, fullRange));
        ExportCheck.Compare(output, gpu, Picture.Test(64, 36), format, tolerance: 1);
    }

    [Fact]
    public void Without_dither_flat_colors_have_one_code()
    {
        using ExportGpu gpu = ExportGpu.Warp();
        var format = new ExportVideoFormat(32, 16, Rate, ExportPixelFormat.Nv12, ExportColor.Bt709Limited);
        Picture source = new Picture(32, 16).Fill(0, 0, 32, 16, 0.3, 0.6, 0.9);
        int[][] planes = ExportCheck.Convert(gpu, source, format, dither: false);
        Assert.Single(planes[0].Distinct());
        Assert.Equal(0, ExportCheck.Worst(planes, ExportReference.Convert(source, format, dither: false)));
    }

    [Fact]
    public void Dither_spreads_a_fractional_code_and_keeps_its_mean()
    {
        using ExportGpu gpu = ExportGpu.Warp();
        var format = new ExportVideoFormat(64, 64, Rate, ExportPixelFormat.Yuv444P, ExportColor.Bt709Limited);
        // Gris con luma 16 + 219·0,5 = 125,5: sin tramado, todo 126; con tramado, mitad 125 y mitad 126.
        Picture source = new Picture(64, 64).Fill(0, 0, 64, 64, 0.5, 0.5, 0.5);
        int[] luma = ExportCheck.Convert(gpu, source, format, dither: true)[0];
        Assert.Equal(new[] { 125, 126 }, luma.Distinct().Order().ToArray());
        Assert.Equal(125.5, luma.Average(), 2);
    }

    /// <summary>Barras BT.709 al 100 % y al 75 %: los códigos publicados ±1 con tramado, e ida y vuelta a R'G'B' ±1 sin él.</summary>
    [Fact]
    public void Bt709_bars_land_on_the_published_codes_and_round_trip()
    {
        using ExportGpu gpu = ExportGpu.Warp();
        (double R, double G, double B)[] bars =
        [
            (1, 1, 1), (1, 1, 0), (0, 1, 1), (0, 1, 0), (1, 0, 1), (1, 0, 0), (0, 0, 1), (0, 0, 0),
        ];
        // Y, Cb, Cr de 8 bits de las barras al 100 % (ITU-R BT.709, rango limitado).
        (int Y, int Cb, int Cr)[] published =
        [
            (235, 128, 128), (219, 16, 138), (188, 154, 16), (173, 42, 26), (78, 214, 230), (63, 102, 240), (32, 240, 118), (16, 128, 128),
        ];
        const int Bar = 16;
        var format = new ExportVideoFormat(Bar * bars.Length, 32, Rate, ExportPixelFormat.Nv12, ExportColor.Bt709Limited);
        // Al 75 % el máximo es el código 191 de R'G'B' de 8 bits.
        foreach (double level in new[] { 1.0, 191 / 255.0 })
        {
            var source = new Picture(format.Width, 32);
            for (int i = 0; i < bars.Length; i++)
            {
                source.Fill(i * Bar, 0, Bar, 32, bars[i].R * level, bars[i].G * level, bars[i].B * level);
            }

            int[][] dithered = ExportCheck.Convert(gpu, source, format, dither: true);
            int[][] exact = ExportCheck.Convert(gpu, source, format, dither: false);
            for (int i = 0; i < bars.Length; i++)
            {
                // Lejos del borde de la barra, donde el filtro del croma no mezcla con la vecina.
                int x = (i * Bar) + 6;
                int cx = x / 2;
                int y = 10;
                if (level == 1)
                {
                    Assert.InRange(dithered[0][(y * format.Width) + x], published[i].Y - 1, published[i].Y + 1);
                    Assert.InRange(dithered[1][(5 * format.Width) + (2 * cx)], published[i].Cb - 1, published[i].Cb + 1);
                    Assert.InRange(dithered[1][(5 * format.Width) + (2 * cx) + 1], published[i].Cr - 1, published[i].Cr + 1);
                }

                Rgb back = Decode(exact[0][(y * format.Width) + x], exact[1][(5 * format.Width) + (2 * cx)], exact[1][(5 * format.Width) + (2 * cx) + 1]);
                Assert.InRange(back.R, (bars[i].R * level * 255) - 1, (bars[i].R * level * 255) + 1);
                Assert.InRange(back.G, (bars[i].G * level * 255) - 1, (bars[i].G * level * 255) + 1);
                Assert.InRange(back.B, (bars[i].B * level * 255) - 1, (bars[i].B * level * 255) + 1);
            }
        }

        static Rgb Decode(int y, int cb, int cr)
        {
            Support.Rgb c = Support.ReferenceColor.Decode(new Support.YuvCode(y, cb, cr), Rodaje.Media.Video.VideoColor.Bt709Limited, tenBit: false);
            return new Rgb(Math.Round(c.R), Math.Round(c.G), Math.Round(c.B));
        }
    }

    [Fact]
    public void Exact_half_reads_mip_one()
    {
        using ExportGpu gpu = ExportGpu.Warp();
        var format = new ExportVideoFormat(64, 36, Rate, ExportPixelFormat.P010, ExportColor.Bt709Limited);
        using ExportFrameConverter converter = gpu.Converter(128, 72, format);
        Assert.Equal((1, true), (converter.Plan.Level, converter.Plan.PassThrough));
        ExportCheck.Compare(output, gpu, Picture.Test(128, 72), format, tolerance: 1);
    }

    [Theory]
    [InlineData(96, 54, ExportScaleFilter.Auto)]
    [InlineData(40, 24, ExportScaleFilter.Auto)]
    [InlineData(200, 112, ExportScaleFilter.Auto)]
    [InlineData(96, 54, ExportScaleFilter.Lanczos)]
    [InlineData(128, 72, ExportScaleFilter.CatmullRom)]
    [InlineData(96, 54, ExportScaleFilter.Bilinear)]
    public void Arbitrary_scales_match_the_reference(int sourceWidth, int sourceHeight, ExportScaleFilter filter)
    {
        using ExportGpu gpu = ExportGpu.Warp();
        var format = new ExportVideoFormat(64, 36, Rate, ExportPixelFormat.Yuv420P10, ExportColor.Bt709Limited);
        // La bilineal del hardware pondera con 8 bits de fracción: ±2 códigos de 10 bits.
        ExportCheck.Compare(output, gpu, Picture.Test(sourceWidth, sourceHeight), format, filter: filter, tolerance: filter == ExportScaleFilter.Bilinear ? 3 : 1);
    }

    [Theory]
    [InlineData(ExportFit.Bars)]
    [InlineData(ExportFit.Crop)]
    [InlineData(ExportFit.Stretch)]
    public void Landscape_into_portrait_follows_the_fit(ExportFit fit)
    {
        using ExportGpu gpu = ExportGpu.Warp();
        var format = new ExportVideoFormat(36, 64, Rate, ExportPixelFormat.Nv12, ExportColor.Bt709Limited);
        Picture source = Picture.Test(64, 36);
        int[][] planes = ExportCheck.Compare(output, gpu, source, format, fit: fit, tolerance: 1);
        if (fit == ExportFit.Bars)
        {
            // Arriba y abajo, negro de rango limitado.
            Assert.All(planes[0][..(36 * 10)], v => Assert.Equal(16, v));
            Assert.All(planes[0][^(36 * 10)..], v => Assert.Equal(16, v));
            Assert.All(planes[1][..(36 * 5)], v => Assert.Equal(128, v));
        }
        else
        {
            Assert.DoesNotContain(planes[0][..36], v => v == 16);
        }
    }

    [Fact]
    public void Bars_are_transparent_with_alpha()
    {
        using ExportGpu gpu = ExportGpu.Warp();
        var format = new ExportVideoFormat(36, 64, Rate, ExportPixelFormat.Yuva420P, ExportColor.Bt709Limited);
        int[][] planes = ExportCheck.Compare(output, gpu, Picture.Test(64, 36, alpha: true), format, tolerance: 1);
        Assert.All(planes[3][..(36 * 10)], v => Assert.Equal(0, v));
        Assert.Contains(planes[3][(36 * 32)..(36 * 33)], v => v == 255);
    }

    [Fact]
    public void Straight_alpha_recovers_the_color_under_a_half_transparent_layer()
    {
        using ExportGpu gpu = ExportGpu.Warp();
        var format = new ExportVideoFormat(16, 16, Rate, ExportPixelFormat.Rgba, ExportColor.Srgb);
        Picture source = new Picture(16, 16).Fill(0, 0, 16, 16, 0.8, 0.4, 0.2, 0.5);
        int[] rgba = ExportCheck.Convert(gpu, source, format, dither: false)[0];
        Assert.Equal(new[] { 204, 102, 51, 128 }, rgba[..4]);
    }

    [Fact]
    public void Blend_mixes_two_composites_before_converting()
    {
        using ExportGpu gpu = ExportGpu.Warp();
        var format = new ExportVideoFormat(64, 36, Rate, ExportPixelFormat.Yuv444P10, ExportColor.Bt709Limited);
        Picture a = Picture.Test(64, 36, seed: 1);
        Picture b = Picture.Test(64, 36, seed: 2).Fill(0, 0, 20, 36, 1, 0, 0);
        using ExportFrameConverter converter = gpu.Converter(64, 36, format);
        nint first = gpu.Upload(a);
        nint second = gpu.Upload(b);
        try
        {
            converter.SubmitBlend(first, second, 0.25f, 3);
            int[][] got = ExportCheck.ReadOne(converter, expectedIndex: 3);
            Picture blended = ExportReference.Scale(a, converter.Plan, 64, 36, false, b, 0.25);
            Assert.True(ExportCheck.Worst(got, ExportReference.Convert(blended, format, dither: true)) <= 1);
        }
        finally
        {
            ExportGpu.Release(first);
            ExportGpu.Release(second);
        }
    }

    [Fact]
    public void Ring_keeps_order_and_refuses_to_overflow()
    {
        using ExportGpu gpu = ExportGpu.Warp();
        var format = new ExportVideoFormat(32, 18, Rate, ExportPixelFormat.Nv12, ExportColor.Bt709Limited);
        using ExportFrameConverter converter = gpu.Converter(32, 18, format);
        var pictures = new[] { Picture.Test(32, 18, seed: 1), Picture.Test(32, 18, seed: 2), Picture.Test(32, 18, seed: 3), Picture.Test(32, 18, seed: 4) };
        nint[] textures = pictures.Select(gpu.Upload).ToArray();
        try
        {
            Assert.False(converter.TryRead((in ExportVideoFrame _) => throw new InvalidOperationException()));
            for (int i = 0; i < 3; i++)
            {
                converter.Submit(textures[i], 10 + i);
            }

            Assert.Equal(3, converter.Pending);
            Assert.Throws<InvalidOperationException>(() => converter.Submit(textures[3], 13));
            for (int n = 0; n < 4; n++)
            {
                int i = n < 3 ? n : 3;
                if (n == 3)
                {
                    converter.Submit(textures[3], 13);
                }

                int[][] got = ExportCheck.ReadOne(converter, expectedIndex: 10 + i);
                Assert.True(ExportCheck.Worst(got, ExportReference.Convert(pictures[i], format, dither: true)) <= 1, $"Cuadro {i} desordenado.");
            }

            Assert.Equal(0, converter.Pending);
        }
        finally
        {
            foreach (nint texture in textures)
            {
                ExportGpu.Release(texture);
            }
        }
    }

    [Fact]
    public void Composite_of_another_size_is_rejected()
    {
        using ExportGpu gpu = ExportGpu.Warp();
        var format = new ExportVideoFormat(32, 18, Rate, ExportPixelFormat.Nv12, ExportColor.Bt709Limited);
        using ExportFrameConverter converter = gpu.Converter(64, 36, format);
        nint texture = gpu.Upload(Picture.Test(32, 18));
        try
        {
            Assert.Throws<ArgumentException>(() => converter.Submit(texture, 0));
            Assert.Equal(0, converter.Pending);
        }
        finally
        {
            ExportGpu.Release(texture);
        }
    }

    [Fact]
    public void Thumbnail_every_half_second()
    {
        using ExportGpu gpu = ExportGpu.Warp();
        var time = new ManualTime();
        var format = new ExportVideoFormat(640, 360, Rate, ExportPixelFormat.Nv12, ExportColor.Bt709Limited);
        using ExportFrameConverter converter = gpu.Converter(640, 360, format, time: time);
        nint texture = gpu.Upload(new Picture(640, 360).Fill(0, 0, 640, 360, 1, 0.5, 0));
        try
        {
            Assert.Null(converter.LatestThumbnail);
            long[] thumbnails = new long[6];
            for (int i = 0; i < 6; i++)
            {
                converter.Submit(texture, i);
                ExportCheck.ReadOne(converter, i);
                thumbnails[i] = converter.LatestThumbnail!.Index;
                time.Advance(TimeSpan.FromMilliseconds(200));
            }

            // 0 ms, 200, 400, 600 (nueva), 800, 1000 → miniaturas de los cuadros 0 y 3; la 6.ª sería a los 1100 ms.
            Assert.Equal(new long[] { 0, 0, 0, 3, 3, 3 }, thumbnails);
            ExportThumbnail thumbnail = converter.LatestThumbnail!;
            Assert.Equal((320, 180), (thumbnail.Width, thumbnail.Height));
            Assert.Equal(new byte[] { 0, 128, 255, 255 }, thumbnail.Bgra[..4]);
        }
        finally
        {
            ExportGpu.Release(texture);
        }
    }

    private readonly record struct Rgb(double R, double G, double B);

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks = 1_000_000;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }
}
