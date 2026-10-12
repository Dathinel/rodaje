using System.Runtime.InteropServices;
using System.Text.Json;
using Rodaje.Modules.Protocol;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Module.Raw.Tests;

/// <summary>
/// El revelado en el proceso de prueba, con LibRaw de <c>.entorno\libraw</c> y CinemaDNG sintéticos: la cámara ve sRGB
/// lineal por el neutro de la toma, así que revelar con el balance de la toma tiene que devolver la escena.
/// </summary>
public class DevelopTests(ITestOutputHelper output)
{
    internal const string Missing = "Sin libraw.dll: ejecuta build\\preparar-entorno.ps1.";

    internal static bool HasLibRaw => LibRawNative.Locate() is not null;

    internal static Half[] Develop(byte[] dng, DevelopSettings settings, int width, int height)
    {
        var pixels = new byte[width * height * 8];
        RawDeveloper.Develop(dng, settings, new ToneTable(PixelFormats.Rgba16F, settings.Exposure, settings.Curve), pixels,
            new VideoLayout(width, height, width * 8, PixelFormats.Rgba16F));
        return MemoryMarshal.Cast<byte, Half>(pixels).ToArray();
    }

    /// <summary>Media de un cuadrado de 9×9 alrededor de (x, y).</summary>
    internal static (double R, double G, double B) Mean(Half[] rgba, int width, int x, int y)
    {
        double r = 0, g = 0, b = 0;
        int n = 0;
        for (int dy = -4; dy <= 4; dy++)
        {
            for (int dx = -4; dx <= 4; dx++)
            {
                int i = (((y + dy) * width) + x + dx) * 4;
                r += (double)rgba[i];
                g += (double)rgba[i + 1];
                b += (double)rgba[i + 2];
                n++;
            }
        }

        return (r / n, g / n, b / n);
    }

    [Fact]
    public void Inspect_reads_size_camera_and_as_shot_balance()
    {
        if (!HasLibRaw)
        {
            output.WriteLine(Missing);
            return;
        }

        RawInfo info = RawDeveloper.Inspect(SyntheticDng.Chart(320, 180).Write());
        output.WriteLine($"{info} LibRaw {RawDeveloper.Version}");
        Assert.Equal((320, 180), (info.Width, info.Height));
        Assert.True(info.IsDng);
        Assert.Equal("Rodaje", info.Make);
        Assert.Equal(3, info.CameraMultipliers.Count);
        Assert.Equal(1 / 0.55, info.CameraMultipliers[0], 0.01);
        Assert.Equal(1 / 0.72, info.CameraMultipliers[2], 0.01);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void As_shot_balance_returns_the_scene_in_linear_rec709(bool highQuality)
    {
        if (!HasLibRaw)
        {
            output.WriteLine(Missing);
            return;
        }

        const int Width = 480, Height = 240;
        Half[] rgba = Develop(SyntheticDng.Chart(Width, Height).Write(), new DevelopSettings { Curve = OutputCurve.Linear, HighQuality = highQuality }, Width, Height);
        double worst = 0;
        for (int i = 0; i < SyntheticDng.ChartPatches.Count; i++)
        {
            (double r, double g, double b) expected = SyntheticDng.ChartPatches[i];
            (int x, int y) = SyntheticDng.PatchCenter(i, Width, Height);
            (double r, double g, double b) got = Mean(rgba, Width, x, y);
            double error = Math.Max(Math.Abs(got.r - expected.r), Math.Max(Math.Abs(got.g - expected.g), Math.Abs(got.b - expected.b)));
            worst = Math.Max(worst, error);
            output.WriteLine($"parche {i}: esperado {expected}, revelado ({got.r:0.0000}, {got.g:0.0000}, {got.b:0.0000}), error {error:0.0000}");
        }

        Assert.True(worst < 0.006, $"Error máximo {worst:0.0000} en luz lineal.");
    }

    [Fact]
    public void Exposure_is_applied_in_linear_light_and_daylight_keeps_the_cast()
    {
        if (!HasLibRaw)
        {
            output.WriteLine(Missing);
            return;
        }

        const int Width = 480, Height = 240;
        byte[] dng = SyntheticDng.Chart(Width, Height).Write();
        (int x, int y) = SyntheticDng.PatchCenter(0, Width, Height);
        var linear = new DevelopSettings { Curve = OutputCurve.Linear };
        double gray = Mean(Develop(dng, linear, Width, Height), Width, x, y).G;
        double brighter = Mean(Develop(dng, linear with { Exposure = 1 }, Width, Height), Width, x, y).G;
        double darker = Mean(Develop(dng, linear with { Exposure = -2 }, Width, Height), Width, x, y).G;
        Assert.Equal(2 * gray, brighter, 0.003);
        Assert.Equal(gray / 4, darker, 0.002);

        (double r, double g, double b) daylight = Mean(Develop(dng, linear with { WhiteBalance = WhiteBalanceMode.Daylight }, Width, Height), Width, x, y);
        output.WriteLine($"Gris 18 % con el balance de la toma: {gray:0.0000}; con luz de día: ({daylight.r:0.000}, {daylight.g:0.000}, {daylight.b:0.000})");
        Assert.Equal(0.18 * 0.55, daylight.r, 0.004);
        Assert.Equal(0.18 * 0.72, daylight.b, 0.004);

        (double r, double g, double b) custom = Mean(Develop(dng, linear with { WhiteBalance = WhiteBalanceMode.Custom, Multipliers = [1 / 0.55f, 1, 1 / 0.72f] }, Width, Height), Width, x, y);
        Assert.Equal(0.18, custom.r, 0.004);
        Assert.Equal(0.18, custom.b, 0.004);
    }

    [Fact]
    public void Rec709_curve_and_8_bit_output_match_the_formula()
    {
        if (!HasLibRaw)
        {
            output.WriteLine(Missing);
            return;
        }

        const int Width = 480, Height = 240;
        byte[] dng = SyntheticDng.Chart(Width, Height).Write();
        var bgra = new byte[Width * Height * 4];
        var settings = new DevelopSettings();
        RawDeveloper.Develop(dng, settings, new ToneTable(PixelFormats.Bgra8, 0, OutputCurve.Rec709), bgra, new VideoLayout(Width, Height, Width * 4, PixelFormats.Bgra8));
        (int x, int y) = SyntheticDng.PatchCenter(1, Width, Height);
        int i = ((y * Width) + x) * 4;
        (double r, _, double b) = SyntheticDng.ChartPatches[1];
        Assert.Equal(ToneTable.Encode(r, OutputCurve.Rec709) * 255, bgra[i + 2], 2.0);
        Assert.Equal(ToneTable.Encode(b, OutputCurve.Rec709) * 255, bgra[i], 2.0);
        Assert.Equal(255, bgra[i + 3]);
    }

    [Fact]
    public void Garbage_and_truncated_files_fail_with_a_message()
    {
        if (!HasLibRaw)
        {
            output.WriteLine(Missing);
            return;
        }

        byte[] dng = SyntheticDng.Chart(64, 32).Write();
        Assert.Throws<RawException>(() => RawDeveloper.Inspect(new byte[1000]));
        var random = new Random(7);
        for (int k = 0; k < 50; k++)
        {
            byte[] broken = dng[..random.Next(8, dng.Length / 2)];
            random.NextBytes(broken.AsSpan(random.Next(8, broken.Length)));
            try
            {
                RawInfo info = RawDeveloper.Inspect(broken);
                Develop(broken, new DevelopSettings(), info.Width, info.Height);
            }
            catch (RawException)
            {
            }
        }
    }

    [Fact]
    public void Wrong_output_size_is_rejected()
    {
        if (!HasLibRaw)
        {
            output.WriteLine(Missing);
            return;
        }

        byte[] dng = SyntheticDng.Chart(64, 32).Write();
        Assert.Throws<RawException>(() => Develop(dng, new DevelopSettings(), 32, 16));
    }

    [Fact]
    public void Frame_rate_tag_is_read_and_junk_gives_null()
    {
        Assert.Equal((24_000, 1_001), DngTags.FrameRate(SyntheticDng.Chart(64, 32).Write()));
        Assert.Equal((25, 1), DngTags.FrameRate((SyntheticDng.Chart(64, 32) with { FrameRate = (25, 1) }).Write()));
        Assert.Null(DngTags.FrameRate((SyntheticDng.Chart(64, 32) with { FrameRate = null }).Write()));
        Assert.Null(DngTags.FrameRate(new byte[16]));
        Assert.Null(DngTags.FrameRate([(byte)'I', (byte)'I', 42, 0, 255, 255, 255, 127]));
    }

    [Theory]
    [InlineData("""{"balance":"auto"}""")]
    [InlineData("""{"balance":[1,2]}""")]
    [InlineData("""{"balance":[1,2,0]}""")]
    [InlineData("""{"exposicion":9}""")]
    [InlineData("""{"exposicion":"mucha"}""")]
    [InlineData("""{"balance":["a",1,1]}""")]
    [InlineData("""{"espacio":"adobe"}""")]
    [InlineData("""{"curva":"log"}""")]
    [InlineData("""{"calidad":3}""")]
    public void Bad_settings_are_rejected(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Throws<ArgumentException>(() => DevelopSettings.Parse(doc.RootElement));
    }

    [Fact]
    public void Settings_are_parsed()
    {
        using JsonDocument doc = JsonDocument.Parse("""{"balance":[2,1,1.5],"exposicion":-1.5,"espacio":"rec2020","curva":"srgb","calidad":"rapida"}""");
        DevelopSettings s = DevelopSettings.Parse(doc.RootElement);
        Assert.Equal(WhiteBalanceMode.Custom, s.WhiteBalance);
        Assert.Equal([2f, 1f, 1.5f], s.Multipliers);
        Assert.Equal(-1.5, s.Exposure);
        Assert.Equal(OutputPrimaries.Rec2020, s.Primaries);
        Assert.Equal(OutputCurve.Srgb, s.Curve);
        Assert.False(s.HighQuality);
        Assert.Equal(new DevelopSettings(), DevelopSettings.Parse(null));
    }
}
