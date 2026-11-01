using System.Diagnostics;
using System.Runtime.InteropServices;
using Rodaje.Core.Raw;
using Rodaje.Media.Gpu;
using Rodaje.Render.Raw;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Render.Tests;

/// <summary>
/// Fase D del HDR (docs/hdr.md): el revelado de Bayer en la GPU contra la referencia de la CPU (<see cref="MosaicDeveloper"/>),
/// en WARP para que corra en cualquier máquina y, con GPU de verdad, con el tiempo por cuadro.
/// </summary>
public sealed class RawGpuDeveloperTests(ITestOutputHelper output)
{
    /// <summary>Un código rgba16f de 12 bits en [0, 1] vale 1/4096 y media precisión tiene 11 bits de mantisa: ±1/2048 cubre los dos.</summary>
    private const double Tolerance = 1.0 / 2048;

    private static Half[] Gpu(GpuDevice gpu, SyntheticDng dng, RawDevelopParameters parameters)
    {
        using var developer = new GpuRawDeveloper(gpu, dng.Width, dng.Height);
        var bytes = new byte[dng.Width * dng.Height * 8];
        developer.Develop(dng.Mosaic(), parameters, bytes, dng.Width * 8);
        Assert.Equal(1, developer.Frames);
        return MemoryMarshal.Cast<byte, Half>(bytes).ToArray();
    }

    private static Half[] Cpu(SyntheticDng dng, RawDevelopParameters parameters)
    {
        var bytes = new byte[dng.Width * dng.Height * 8];
        MosaicDeveloper.DevelopRgba16F(dng.Mosaic(), dng.Width, dng.Height, parameters, bytes, dng.Width * 8);
        return MemoryMarshal.Cast<byte, Half>(bytes).ToArray();
    }

    private static double Compare(Half[] gpu, Half[] cpu)
    {
        Assert.Equal(cpu.Length, gpu.Length);
        double worst = 0;
        for (int i = 0; i < cpu.Length; i++)
        {
            worst = Math.Max(worst, Math.Abs((double)gpu[i] - (double)cpu[i]));
        }

        return worst;
    }

    /// <summary>Ruido por píxel: todos los filtros del interpolado y los bordes reflejados, en las cuatro fases del patrón.</summary>
    private static SyntheticDng Noise(int width, int height, int seed)
    {
        var random = new Random(seed);
        var scene = new (double R, double G, double B)[width * height];
        for (int i = 0; i < scene.Length; i++)
        {
            scene[i] = (random.NextDouble(), random.NextDouble(), random.NextDouble());
        }

        return new SyntheticDng { Width = width, Height = height, Scene = (x, y) => scene[(y * width) + x] };
    }

    [Theory]
    [InlineData(RawOutputCurve.Linear, 0)]
    [InlineData(RawOutputCurve.Rec709, 0)]
    [InlineData(RawOutputCurve.Srgb, 1.5)]
    [InlineData(RawOutputCurve.Linear, -2)]
    public void Warp_matches_the_cpu_reference_on_the_chart(RawOutputCurve curve, double exposure)
    {
        using GpuDevice gpu = GpuDevice.CreateWarp();
        SyntheticDng dng = SyntheticDng.Chart(96, 48);
        RawDevelopParameters parameters = dng.DevelopParameters(exposure, curve);
        double worst = Compare(Gpu(gpu, dng, parameters), Cpu(dng, parameters));
        output.WriteLine($"{curve} {exposure:+0.0;-0.0;0} EV: diferencia máxima GPU − CPU {worst:0.00000}");
        Assert.True(worst <= Tolerance, $"Diferencia máxima {worst:0.00000}.");
    }

    [Fact]
    public void Warp_matches_the_cpu_reference_on_noise_with_a_colour_matrix_and_every_pattern()
    {
        using GpuDevice gpu = GpuDevice.CreateWarp();
        SyntheticDng dng = Noise(70, 38, 7);
        RawDevelopParameters chart = dng.DevelopParameters(0.5, RawOutputCurve.Rec709) with
        {
            Matrix = [1.6f, -0.5f, -0.1f, -0.2f, 1.4f, -0.2f, 0.05f, -0.45f, 1.4f],
            Multipliers = [2.1f, 1, 1.6f],
        };
        foreach (string text in (string[])["RGGB", "BGGR", "GRBG", "GBRG"])
        {
            Assert.True(BayerPattern.TryParse(text, out BayerPattern pattern));
            RawDevelopParameters parameters = chart with { Pattern = pattern };
            double worst = Compare(Gpu(gpu, dng, parameters), Cpu(dng, parameters));
            output.WriteLine($"{text}: diferencia máxima GPU − CPU {worst:0.00000}");
            Assert.True(worst <= Tolerance, $"{text}: diferencia máxima {worst:0.00000}.");
        }
    }

    [Fact]
    public void The_gpu_returns_the_scene_with_the_as_shot_balance()
    {
        using GpuDevice gpu = GpuDevice.CreateWarp();
        SyntheticDng dng = SyntheticDng.Chart(240, 120);
        Half[] rgba = Gpu(gpu, dng, dng.DevelopParameters());
        for (int i = 0; i < SyntheticDng.ChartPatches.Count; i++)
        {
            (int x, int y) = SyntheticDng.PatchCenter(i, dng.Width, dng.Height);
            (double r, double g, double b) = SyntheticDng.ChartPatches[i];
            int p = ((y * dng.Width) + x) * 4;
            Assert.Equal(r, (double)rgba[p], 0.003);
            Assert.Equal(g, (double)rgba[p + 1], 0.003);
            Assert.Equal(b, (double)rgba[p + 2], 0.003);
            Assert.Equal(Half.One, rgba[p + 3]);
        }
    }

    [Fact]
    public void A_12_bit_ramp_keeps_more_than_2000_levels_in_rgba16f()
    {
        const int Levels = 3840, Block = 4;
        using GpuDevice gpu = GpuDevice.CreateWarp();
        var dng = new SyntheticDng
        {
            Width = Levels * Block,
            Height = 8,
            Neutral = [1.0, 1.0, 1.0],
            Scene = (x, _) => { double v = x / Block / (double)(Levels - 1); return (v, v, v); },
        };
        Half[] rgba = Gpu(gpu, dng, dng.DevelopParameters(0, RawOutputCurve.Rec709));
        int distinct = Enumerable.Range(0, Levels).Select(i => (double)rgba[(((4 * dng.Width) + (i * Block) + 2) * 4) + 1]).Distinct().Count();
        output.WriteLine($"Rec.709: {Levels} códigos RAW de 12 bits → {distinct} valores rgba16f distintos en la GPU.");
        Assert.True(distinct > 2000, $"Solo {distinct}.");
    }

    [Fact]
    public void Bad_sizes_and_parameters_are_rejected()
    {
        using GpuDevice gpu = GpuDevice.CreateWarp();
        using var developer = new GpuRawDeveloper(gpu, 16, 8);
        RawDevelopParameters parameters = SyntheticDng.Chart(16, 8).DevelopParameters();
        Assert.Throws<ArgumentException>(() => developer.Develop(new ushort[100], parameters, new byte[16 * 8 * 8], 16 * 8));
        Assert.Throws<ArgumentException>(() => developer.Develop(new ushort[128], parameters, new byte[100], 16 * 8));
        Assert.Throws<ArgumentException>(() => developer.Develop(new ushort[128], parameters with { White = 0 }, new byte[16 * 8 * 8], 16 * 8));
    }

    /// <summary>Tiempo por cuadro en 1080p y 4K DCI, en la GPU de la máquina y en la CPU (referencia); las cifras van a docs/hdr.md.</summary>
    [GpuFact]
    public void Time_per_frame_on_the_gpu_and_the_cpu_reference()
    {
        using GpuDevice gpu = GpuDevice.Create();
        foreach ((int width, int height) in ((int, int)[])[(1920, 1080), (4096, 2160)])
        {
            SyntheticDng dng = SyntheticDng.Chart(width, height);
            ushort[] mosaic = dng.Mosaic();
            RawDevelopParameters parameters = dng.DevelopParameters(0, RawOutputCurve.Rec709);
            var bytes = new byte[width * height * 8];
            using var developer = new GpuRawDeveloper(gpu, width, height);
            developer.Develop(mosaic, parameters, bytes, width * 8);
            var times = new List<double>();
            for (int i = 0; i < 5; i++)
            {
                long start = Stopwatch.GetTimestamp();
                developer.Develop(mosaic, parameters, bytes, width * 8);
                times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }

            long cpuStart = Stopwatch.GetTimestamp();
            MosaicDeveloper.DevelopRgba16F(mosaic, width, height, parameters, bytes, width * 8);
            double cpu = Stopwatch.GetElapsedTime(cpuStart).TotalMilliseconds;
            output.WriteLine($"{width}×{height} en {gpu.AdapterName}: mediana {times.Order().ElementAt(2):0.0} ms por cuadro (mín {times.Min():0.0}); "
                + $"referencia en la CPU (un hilo): {cpu:0} ms.");
        }
    }
}
