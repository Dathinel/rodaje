using System.Diagnostics;
using System.Globalization;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Playback.Composition;
using Rodaje.Render.Tests.Support;
using Xunit.Abstractions;

namespace Rodaje.Render.Tests;

public sealed class CompositorPerformanceTests(ITestOutputHelper output)
{
    private const int Warmup = 30;
    private const int Measured = 200;

    /// <summary>
    /// Mide con marcas de tiempo de la GPU el costo de componer 1, 2, 4 y 8 capas NV12 de 1080p a pantalla completa
    /// (el peor caso: cada capa cubre todo el cuadro y se mezcla) en una salida de 1080p.
    /// </summary>
    [GpuTheory]
    [InlineData(CompositeFormat.Rgba16Float)]
    [InlineData(CompositeFormat.Rgba8Unorm)]
    public void Reports_gpu_milliseconds_per_frame_for_1_to_8_layers(CompositeFormat format)
    {
        using GpuDevice gpu = GpuDevice.Create();
        // Ocho capas de un arreglo de 1920×1088 como el pool de D3D11VA, cada una con un degradado distinto.
        var slices = new YuvImage[8];
        for (int s = 0; s < slices.Length; s++)
        {
            slices[s] = new YuvImage(1920, 1088, tenBit: false);
            for (int band = 0; band < 16; band++)
            {
                slices[s].Fill(band * 120, 0, 120, 1088, new YuvCode(16 + ((band * 13) + (s * 7)) % 219, 64 + (s * 16), 192 - (band * 8)));
            }
        }

        nint texture = YuvImage.CreateTexture(gpu, slices);
        try
        {
            Measure(gpu, texture, format);
        }
        finally
        {
            Layers.Release(texture);
        }
    }

    private static double Percentile(List<double> sorted, double p) => sorted[(int)Math.Min(sorted.Count - 1, Math.Round(p * (sorted.Count - 1)))];

    private void Measure(GpuDevice gpu, nint texture, CompositeFormat format)
    {
        var color = VideoColor.Bt709Limited;
        using var compositor = new Compositor(gpu, 1920, 1080, format);
        using var timer = new GpuTimer(gpu);
        output.WriteLine($"GPU: {gpu.AdapterName}; salida 1920×1080 {format}; {Measured} cuadros por caso tras {Warmup} de calentamiento.");
        foreach (int count in new[] { 1, 2, 4, 8 })
        {
            var layers = new LayerFrame[count];
            for (int i = 0; i < count; i++)
            {
                // Opacidad < 1 en las de arriba para que la mezcla no sea trivial.
                var transform = LayerTransform.Identity with { Opacity = i == 0 ? 1 : 0.85 };
                layers[i] = Layers.FromTexture(gpu, texture, i, 1920, 1080, color, transform, i);
            }

            using var frame = new CompositionFrame(Rodaje.Core.Timing.Time.Zero, 1920, 1080, layers);
            var gpuMs = new List<double>(Measured);
            var cpuMs = new List<double>(Measured);
            for (int n = 0; n < Warmup + Measured; n++)
            {
                timer.Begin();
                long start = Stopwatch.GetTimestamp();
                compositor.Render(frame);
                double cpu = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                timer.End();
                if (timer.WaitMilliseconds(TimeSpan.FromSeconds(2)) is { } ms && n >= Warmup)
                {
                    gpuMs.Add(ms);
                    cpuMs.Add(cpu);
                }
            }

            Assert.True(gpuMs.Count >= Measured / 2, "Demasiadas mediciones disjuntas de la GPU.");
            gpuMs.Sort();
            cpuMs.Sort();
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{count} capa(s): GPU mediana {Percentile(gpuMs, 0.5):0.000} ms, p95 {Percentile(gpuMs, 0.95):0.000} ms; CPU de envío mediana {Percentile(cpuMs, 0.5):0.000} ms"));
            // Holgado a propósito: solo detecta una regresión grave (una vista previa a 60 fps tiene 16,7 ms).
            Assert.True(Percentile(gpuMs, 0.5) < 16, $"Componer {count} capas tomó {Percentile(gpuMs, 0.5):0.00} ms en la GPU.");
        }
    }
}
