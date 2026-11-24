using System.Runtime.InteropServices;
using Rodaje.Core.Raw;

namespace Rodaje.Core.Tests.Raw;

/// <summary>
/// La referencia en la CPU del revelado de Bayer (docs/hdr.md, fase D): una «cámara» sintética RGGB que ve sRGB lineal por
/// el neutro de la toma, como <c>SyntheticDng</c> de las pruebas con archivo, pero sin archivo ni LibRaw.
/// </summary>
public sealed class MosaicDeveloperTests
{
    private const int Black = 256;
    private const int WhiteLevel = 4095;
    private static readonly double[] Neutral = [0.55, 1.0, 0.72];

    private static readonly (double R, double G, double B)[] Patches =
    [
        (0.18, 0.18, 0.18), (0.40, 0.10, 0.05), (0.05, 0.30, 0.08), (0.06, 0.08, 0.45), (0.80, 0.80, 0.80), (0.60, 0.45, 0.10),
    ];

    /// <summary>Mosaico RGGB con el negro ya restado, como lo devuelve «mosaico» del módulo.</summary>
    internal static ushort[] Mosaic(int width, int height, Func<int, int, (double R, double G, double B)> scene, IReadOnlyList<double>? neutral = null)
    {
        neutral ??= Neutral;
        var mosaic = new ushort[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                (double r, double g, double b) = scene(x, y);
                (double value, double n) = (y % 2, x % 2) switch
                {
                    (0, 0) => (r, neutral[0]),
                    (1, 1) => (b, neutral[2]),
                    _ => (g, neutral[1]),
                };
                mosaic[(y * width) + x] = (ushort)Math.Round(Math.Clamp(value, 0, 1) * n * (WhiteLevel - Black));
            }
        }

        return mosaic;
    }

    internal static RawDevelopParameters Parameters(double exposure = 0, RawOutputCurve curve = RawOutputCurve.Linear, IReadOnlyList<double>? neutral = null)
    {
        neutral ??= Neutral;
        return new RawDevelopParameters
        {
            Pattern = BayerPattern.Rggb,
            White = WhiteLevel - Black,
            Multipliers = [(float)(1 / neutral[0]), (float)(1 / neutral[1]), (float)(1 / neutral[2])],
            Matrix = [1, 0, 0, 0, 1, 0, 0, 0, 1],
            Exposure = exposure,
            Curve = curve,
        };
    }

    private static Func<int, int, (double R, double G, double B)> Chart(int width, int height) => (x, y) =>
    {
        if (y < height / 2)
        {
            return Patches[Math.Min(x * Patches.Length / width, Patches.Length - 1)];
        }

        double v = x / (double)(width - 1);
        return (v, v, v);
    };

    private static (double R, double G, double B) Mean(float[] rgb, int width, int x, int y)
    {
        double r = 0, g = 0, b = 0;
        for (int dy = -4; dy <= 4; dy++)
        {
            for (int dx = -4; dx <= 4; dx++)
            {
                int i = (((y + dy) * width) + x + dx) * 3;
                r += rgb[i];
                g += rgb[i + 1];
                b += rgb[i + 2];
            }
        }

        return (r / 81, g / 81, b / 81);
    }

    [Theory]
    [InlineData("RGGB", 0, 1, 1, 2)]
    [InlineData("bggr", 2, 1, 1, 0)]
    [InlineData("GRBG", 1, 0, 2, 1)]
    [InlineData("GBRG", 1, 2, 0, 1)]
    public void Patterns_parse_and_print(string text, int tl, int tr, int bl, int br)
    {
        Assert.True(BayerPattern.TryParse(text, out BayerPattern pattern));
        Assert.Equal(new BayerPattern(tl, tr, bl, br), pattern);
        Assert.Equal(text.ToUpperInvariant(), pattern.ToString());
        Assert.Equal(tl, pattern.ChannelAt(0, 0));
        Assert.Equal(tr, pattern.ChannelAt(3, 2));
        Assert.Equal(bl, pattern.ChannelAt(2, 3));
        Assert.Equal(br, pattern.ChannelAt(1, 1));
    }

    [Theory]
    [InlineData("RGBG")]
    [InlineData("RRGB")]
    [InlineData("GGGG")]
    [InlineData("RGG")]
    [InlineData("RGBX")]
    [InlineData(null)]
    public void Patterns_without_one_red_one_blue_and_two_greens_are_rejected(string? text) =>
        Assert.False(BayerPattern.TryParse(text, out _));

    [Fact]
    public void Parameters_are_validated()
    {
        Assert.Null(Parameters().Validate());
        Assert.NotNull((Parameters() with { White = 0 }).Validate());
        Assert.NotNull((Parameters() with { Multipliers = [1, 0, 1] }).Validate());
        Assert.NotNull((Parameters() with { Matrix = [1, 0, 0] }).Validate());
        Assert.NotNull((Parameters() with { Exposure = 7 }).Validate());
        Assert.NotNull((Parameters() with { Pattern = new BayerPattern(0, 0, 1, 2) }).Validate());
        Assert.Throws<ArgumentException>(() => (Parameters() with { White = 70_000 }).Check());
        Assert.Equal([1 / 0.55f, 1, 1 / 0.72f], Parameters().NormalizedMultipliers(), (a, b) => Math.Abs(a - b) < 1e-5);
    }

    [Fact]
    public void Mirrored_borders_keep_the_parity()
    {
        Assert.Equal(1, MosaicDeveloper.Mirror(-1, 10));
        Assert.Equal(2, MosaicDeveloper.Mirror(-2, 10));
        Assert.Equal(8, MosaicDeveloper.Mirror(10, 10));
        Assert.Equal(7, MosaicDeveloper.Mirror(11, 10));
        Assert.Equal(5, MosaicDeveloper.Mirror(5, 10));
    }

    [Fact]
    public void As_shot_balance_returns_the_scene_in_linear_rgb()
    {
        const int Width = 240, Height = 120;
        var rgb = new float[Width * Height * 3];
        MosaicDeveloper.Develop(Mosaic(Width, Height, Chart(Width, Height)), Width, Height, Parameters(), rgb);
        double worst = 0;
        for (int i = 0; i < Patches.Length; i++)
        {
            (double r, double g, double b) expected = Patches[i];
            (double r, double g, double b) got = Mean(rgb, Width, (int)((i + 0.5) * Width / Patches.Length), Height / 4);
            worst = Math.Max(worst, Math.Max(Math.Abs(got.r - expected.r), Math.Max(Math.Abs(got.g - expected.g), Math.Abs(got.b - expected.b))));
        }

        Assert.True(worst < 0.002, $"Error máximo {worst:0.0000} en luz lineal.");
    }

    [Fact]
    public void A_flat_frame_is_flat_to_the_last_pixel_of_every_border()
    {
        const int Width = 32, Height = 20;
        var rgb = new float[Width * Height * 3];
        MosaicDeveloper.Develop(Mosaic(Width, Height, (_, _) => (0.4, 0.4, 0.4)), Width, Height, Parameters(), rgb);
        foreach (float v in rgb)
        {
            Assert.InRange(v, 0.4 - 0.002, 0.4 + 0.002);
        }
    }

    [Fact]
    public void Exposure_is_linear_and_the_curves_follow_the_formula()
    {
        const int Width = 64, Height = 32;
        ushort[] mosaic = Mosaic(Width, Height, (_, _) => (0.18, 0.18, 0.18));
        var plain = new float[Width * Height * 3];
        var brighter = new float[Width * Height * 3];
        var rec709 = new float[Width * Height * 3];
        var srgb = new float[Width * Height * 3];
        MosaicDeveloper.Develop(mosaic, Width, Height, Parameters(), plain);
        MosaicDeveloper.Develop(mosaic, Width, Height, Parameters(1), brighter);
        MosaicDeveloper.Develop(mosaic, Width, Height, Parameters(0, RawOutputCurve.Rec709), rec709);
        MosaicDeveloper.Develop(mosaic, Width, Height, Parameters(0, RawOutputCurve.Srgb), srgb);
        int i = (((Height / 2) * Width) + (Width / 2)) * 3;
        Assert.Equal(0.18, plain[i], 0.002);
        Assert.Equal(0.36, brighter[i], 0.003);
        Assert.Equal(MosaicDeveloper.Encode(0.18, RawOutputCurve.Rec709), rec709[i], 0.002);
        Assert.Equal(MosaicDeveloper.Encode(0.18, RawOutputCurve.Srgb), srgb[i], 0.002);
        Assert.Equal(0.18, MosaicDeveloper.Encode(0.18, RawOutputCurve.Linear), 1e-9);
        Assert.Equal(1.0, MosaicDeveloper.Encode(1, RawOutputCurve.Rec709), 1e-9);
        Assert.Equal(1.0, MosaicDeveloper.Encode(1, RawOutputCurve.Srgb), 1e-9);
        Assert.Equal(0, MosaicDeveloper.Encode(-0.5, RawOutputCurve.Rec709));
    }

    [Fact]
    public void Highlights_clip_at_the_white_before_the_exposure()
    {
        const int Width = 64, Height = 32;
        ushort[] mosaic = Mosaic(Width, Height, (_, _) => (1, 1, 1));
        var darker = new float[Width * Height * 3];
        MosaicDeveloper.Develop(mosaic, Width, Height, Parameters(-1), darker);
        Assert.Equal(0.5, darker[(((Height / 2) * Width) + (Width / 2)) * 3 + 1], 0.002);
    }

    [Fact]
    public void A_12_bit_ramp_keeps_more_than_3000_levels_in_float_and_more_than_2000_in_rgba16f()
    {
        const int Levels = 3840, Block = 4, Width = Levels * Block, Height = 8;
        ushort[] mosaic = Mosaic(Width, Height, (x, _) => { double v = x / Block / (double)(Levels - 1); return (v, v, v); }, [1, 1, 1]);
        var rgb = new float[Width * Height * 3];
        RawDevelopParameters parameters = Parameters(0, RawOutputCurve.Rec709, [1, 1, 1]);
        MosaicDeveloper.Develop(mosaic, Width, Height, parameters, rgb);
        int distinct = Enumerable.Range(0, Levels).Select(i => rgb[((((Height / 2) * Width) + (i * Block) + 2) * 3) + 1]).Distinct().Count();
        Assert.True(distinct > 3000, $"Solo {distinct} valores distintos en float.");

        var rgba = new byte[Width * Height * 8];
        MosaicDeveloper.DevelopRgba16F(mosaic, Width, Height, parameters, rgba, Width * 8);
        Half[] half = MemoryMarshal.Cast<byte, Half>(rgba).ToArray();
        int distinctHalf = Enumerable.Range(0, Levels).Select(i => (double)half[((((Height / 2) * Width) + (i * Block) + 2) * 4) + 1]).Distinct().Count();
        Assert.True(distinctHalf > 2000, $"Solo {distinctHalf} valores rgba16f distintos.");
        Assert.Equal(Half.One, half[3]);
    }

    [Fact]
    public void Sizes_are_checked()
    {
        var rgb = new float[8 * 8 * 3];
        Assert.Throws<ArgumentException>(() => MosaicDeveloper.Develop(new ushort[10], 8, 8, Parameters(), rgb));
        Assert.Throws<ArgumentException>(() => MosaicDeveloper.Develop(new ushort[64], 8, 8, Parameters(), new float[10]));
        Assert.Throws<ArgumentException>(() => MosaicDeveloper.DevelopRgba16F(new ushort[64], 8, 8, Parameters(), new byte[100], 64));
    }
}
