using System.Runtime.InteropServices;
using System.Text.Json;
using Rodaje.Core.Raw;
using Rodaje.Modules.Protocol;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Module.Raw.Tests;

/// <summary>
/// «mosaico» (docs/hdr.md, fase D): LibRaw entrega el mosaico de Bayer con el negro restado y lo que hace falta para
/// revelarlo fuera; revelado con la referencia de la CPU del núcleo tiene que dar la misma escena que «revelar» con LibRaw.
/// </summary>
public sealed class MosaicTests(ITestOutputHelper output)
{
    private static (MosaicInfo Info, ushort[] Mosaic) Unpack(byte[] dng, int width, int height, DevelopSettings? settings = null)
    {
        var bytes = new byte[width * height * 2];
        MosaicInfo info = RawDeveloper.Unpack(dng, settings ?? new DevelopSettings(), bytes, width, height);
        return (info, MemoryMarshal.Cast<byte, ushort>(bytes).ToArray());
    }

    private static RawDevelopParameters Parameters(MosaicInfo info, double exposure = 0, RawOutputCurve curve = RawOutputCurve.Linear)
    {
        Assert.True(BayerPattern.TryParse(info.Pattern, out BayerPattern pattern));
        return new RawDevelopParameters
        {
            Pattern = pattern,
            White = info.White,
            Multipliers = info.Multipliers,
            Matrix = info.Matrix,
            Exposure = exposure,
            Curve = curve,
        };
    }

    [Fact]
    public void Info_says_whether_the_frame_is_a_bayer_mosaic()
    {
        if (!DevelopTests.HasLibRaw)
        {
            output.WriteLine(DevelopTests.Missing);
            return;
        }

        RawInfo info = RawDeveloper.Inspect(SyntheticDng.Chart(320, 180).Write());
        Assert.Equal("RGGB", info.BayerPattern);
    }

    [Fact]
    public void The_mosaic_is_the_file_without_the_black_and_comes_with_the_white_the_balance_and_the_matrix()
    {
        if (!DevelopTests.HasLibRaw)
        {
            output.WriteLine(DevelopTests.Missing);
            return;
        }

        var dng = SyntheticDng.Chart(320, 180);
        (MosaicInfo info, ushort[] mosaic) = Unpack(dng.Write(), dng.Width, dng.Height);
        output.WriteLine($"{info}");
        Assert.Equal("RGGB", info.Pattern);
        Assert.Equal(dng.WhiteLevel - dng.BlackLevel, info.White);
        Assert.Equal(dng.Mosaic(), mosaic);
        Assert.Equal(1 / 0.55, info.Multipliers[0], 0.01);
        Assert.Equal(1, info.Multipliers[1], 0.001);
        Assert.Equal(1 / 0.72, info.Multipliers[2], 0.01);
        // La «cámara» es sRGB lineal: la matriz de LibRaw es la identidad (± el redondeo de los racionales del DNG).
        for (int i = 0; i < 9; i++)
        {
            Assert.Equal(i % 4 == 0 ? 1 : 0, info.Matrix[i], 0.01);
        }

        (MosaicInfo daylight, _) = Unpack(dng.Write(), dng.Width, dng.Height, new DevelopSettings { WhiteBalance = WhiteBalanceMode.Daylight });
        Assert.Equal(1, daylight.Multipliers[0], 0.02);
        Assert.Equal(1, daylight.Multipliers[2], 0.02);
        (MosaicInfo custom, _) = Unpack(dng.Write(), dng.Width, dng.Height, new DevelopSettings { WhiteBalance = WhiteBalanceMode.Custom, Multipliers = [2, 1, 3] });
        Assert.Equal([2, 1, 3], custom.Multipliers);
    }

    [Fact]
    public void The_cpu_reference_on_the_mosaic_matches_libraw_on_the_chart()
    {
        if (!DevelopTests.HasLibRaw)
        {
            output.WriteLine(DevelopTests.Missing);
            return;
        }

        const int Width = 480, Height = 240;
        byte[] file = SyntheticDng.Chart(Width, Height).Write();
        foreach ((double exposure, OutputCurve curve, RawOutputCurve reference) in ((double, OutputCurve, RawOutputCurve)[])
            [(0, OutputCurve.Linear, RawOutputCurve.Linear), (1, OutputCurve.Rec709, RawOutputCurve.Rec709), (-2, OutputCurve.Srgb, RawOutputCurve.Srgb)])
        {
            var settings = new DevelopSettings { Curve = curve, Exposure = exposure, HighQuality = false };
            Half[] libraw = DevelopTests.Develop(file, settings, Width, Height);
            (MosaicInfo info, ushort[] mosaic) = Unpack(file, Width, Height, settings);
            var bytes = new byte[Width * Height * 8];
            MosaicDeveloper.DevelopRgba16F(mosaic, Width, Height, Parameters(info, exposure, reference), bytes, Width * 8);
            Half[] ours = MemoryMarshal.Cast<byte, Half>(bytes).ToArray();
            double worst = 0;
            for (int i = 0; i < SyntheticDng.ChartPatches.Count; i++)
            {
                (int x, int y) = SyntheticDng.PatchCenter(i, Width, Height);
                (double r, double g, double b) a = DevelopTests.Mean(libraw, Width, x, y);
                (double r, double g, double b) o = DevelopTests.Mean(ours, Width, x, y);
                worst = Math.Max(worst, Math.Max(Math.Abs(a.r - o.r), Math.Max(Math.Abs(a.g - o.g), Math.Abs(a.b - o.b))));
            }

            output.WriteLine($"{curve} {exposure:+0;-0;0} EV: diferencia máxima en los parches {worst:0.0000}");
            Assert.True(worst < 0.006, $"{curve}: diferencia máxima {worst:0.0000} entre LibRaw y la referencia.");
        }
    }

    [Fact]
    public void Wrong_sizes_and_other_primaries_are_errors()
    {
        if (!DevelopTests.HasLibRaw)
        {
            output.WriteLine(DevelopTests.Missing);
            return;
        }

        byte[] file = SyntheticDng.Chart(64, 32).Write();
        Assert.Throws<RawException>(() => RawDeveloper.Unpack(file, new DevelopSettings(), new byte[64 * 32 * 2], 32, 64));
        Assert.Throws<RawException>(() => RawDeveloper.Unpack(file, new DevelopSettings { Primaries = OutputPrimaries.Rec2020 }, new byte[64 * 32 * 2], 64, 32));
        Assert.Throws<ArgumentException>(() => RawDeveloper.Unpack(file, new DevelopSettings(), new byte[100], 64, 32));
        Assert.Throws<RawException>(() => RawDeveloper.Unpack(new byte[4096], new DevelopSettings(), new byte[64 * 32 * 2], 64, 32));
    }

    [Fact]
    public void A_12_bit_ramp_arrives_whole()
    {
        if (!DevelopTests.HasLibRaw)
        {
            output.WriteLine(DevelopTests.Missing);
            return;
        }

        const int Width = 3840 * 4, Height = 32;
        (MosaicInfo info, ushort[] mosaic) = Unpack(BitDepthTests.Ramp(), Width, Height);
        Assert.Equal(3839, info.White);
        int distinct = BitDepthTests.DistinctGreen(x => mosaic[((Height / 2) * Width) + x]);
        output.WriteLine($"{distinct} códigos distintos en el mosaico.");
        Assert.Equal(3840, distinct);
    }

    /// <summary>Por el protocolo, como lo pide la app: pares [archivo, salida de bytes] con «ancho» y «alto».</summary>
    [Fact]
    public async Task Mosaic_through_the_protocol()
    {
        string exe = Environment.GetEnvironmentVariable("RODAJE_MODULO_RAW") is { Length: > 0 } e ? e : Path.Combine(AppContext.BaseDirectory, "Rodaje.Module.Raw.exe");
        if (!File.Exists(exe) || !DevelopTests.HasLibRaw)
        {
            output.WriteLine(DevelopTests.Missing);
            return;
        }

        const int Width = 320, Height = 180;
        var dng = SyntheticDng.Chart(Width, Height);
        byte[] file = dng.Write();
        await using ModuleProcess module = await ModuleProcess.StartAsync(exe);
        Assert.Contains(module.Client.Module.Functions, f => f.Id == "mosaico");
        SharedMemoryRegion region = module.Client.CreateRegion((file.Length + (Width * Height * 2)) * 2L);
        var pairs = new List<SharedBufferRef>();
        long at = 0;
        for (int i = 0; i < 2; i++)
        {
            file.CopyTo(region.GetSpan(at, file.Length));
            pairs.Add(region.Describe(at, file.Length));
            at += file.Length;
            pairs.Add(region.Describe(at, Width * Height * 2));
            at += Width * Height * 2;
        }

        using JsonDocument parameters = JsonDocument.Parse($$"""{"ancho":{{Width}},"alto":{{Height}},"balance":"camara","hilos":2}""");
        ModuleResult result = await module.Client.InvokeAsync("mosaico", parameters.RootElement, pairs);
        output.WriteLine($"mosaico: {result.Value}");
        Assert.Equal(2, result.Buffers.Count);
        JsonElement frames = result.Value!.Value.GetProperty("cuadros");
        Assert.Equal(2, frames.GetArrayLength());
        Assert.Equal("RGGB", frames[0].GetProperty("patron").GetString());
        Assert.Equal(dng.WhiteLevel - dng.BlackLevel, frames[1].GetProperty("blanco").GetInt32());
        Assert.Equal(9, frames[0].GetProperty("matriz").GetArrayLength());
        Assert.Equal(dng.Mosaic(), MemoryMarshal.Cast<byte, ushort>(region.GetSpan(result.Buffers[1])).ToArray());

        using JsonDocument missing = JsonDocument.Parse("""{"balance":"camara"}""");
        ModuleCallException error = await Assert.ThrowsAsync<ModuleCallException>(() => module.Client.InvokeAsync("mosaico", missing.RootElement, pairs));
        Assert.Equal(ErrorCodes.InvalidParameters, error.Code);
        using JsonDocument small = JsonDocument.Parse($$"""{"ancho":{{Width * 2}},"alto":{{Height}}}""");
        error = await Assert.ThrowsAsync<ModuleCallException>(() => module.Client.InvokeAsync("mosaico", small.RootElement, pairs));
        Assert.Equal(ErrorCodes.InvalidParameters, error.Code);
    }
}
