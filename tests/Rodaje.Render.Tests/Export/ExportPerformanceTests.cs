using System.Diagnostics;
using System.Globalization;
using Rodaje.Core.Timing;
using Rodaje.Media.Export;
using Rodaje.Media.Gpu;
using Rodaje.Render.Export;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Render.Tests.Export;

/// <summary>
/// Conversión y bajada por cuadro en la GPU de verdad: el tiempo de GPU de <see cref="ExportFrameConverter.Submit"/>
/// (escala, Y'CbCr y copia al búfer <c>Staging</c>) con <see cref="GpuTimer"/>, y el caudal del anillo completo
/// (encolar, mapear, copiar a la CPU y empaquetar como lo pide la tubería). Los objetivos son de la RTX 3050 Laptop y
/// solo se exigen con <c>RODAJE_PRUEBAS_LARGAS=1</c>; sin ella se informan y se detecta una regresión grave.
/// </summary>
[Trait("Categoria", "Rendimiento")]
public sealed class ExportPerformanceTests(ITestOutputHelper output)
{
    private const int Warmup = 30;
    private const int Measured = 300;
    private static readonly bool Strict = Environment.GetEnvironmentVariable(Skip.LongVariable) == "1";

    /// <param name="minFps">Caudal mínimo del anillo con <c>RODAJE_PRUEBAS_LARGAS=1</c>.</param>
    [GpuTheory]
    [InlineData(1920, 1080, 1920, 1080, ExportPixelFormat.Nv12, 500)]
    [InlineData(1920, 1080, 1920, 1080, ExportPixelFormat.P010, 400)]
    [InlineData(1920, 1080, 1280, 720, ExportPixelFormat.Nv12, 500)]
    [InlineData(1920, 1080, 1080, 1920, ExportPixelFormat.Nv12, 300)]
    [InlineData(3840, 2160, 1920, 1080, ExportPixelFormat.Nv12, 500)]
    [InlineData(3840, 2160, 3840, 2160, ExportPixelFormat.Nv12, 150)]
    [InlineData(3840, 2160, 3840, 2160, ExportPixelFormat.P010, 100)]
    public void Conversion_and_download_per_frame(int sourceWidth, int sourceHeight, int width, int height, ExportPixelFormat pixels, int minFps)
    {
        using GpuDevice gpu = GpuDevice.Create();
        using ExportGpu device = ExportGpu.Hardware(gpu);
        var format = new ExportVideoFormat(width, height, new Rational(30, 1), pixels, ExportColor.Bt709Limited);
        nint texture = device.UploadGradient(sourceWidth, sourceHeight);
        try
        {
            using var converter = new ExportFrameConverter(gpu, sourceWidth, sourceHeight, format);
            using var timer = new GpuTimer(gpu);
            byte[] sink = new byte[format.FrameBytes];
            ExportFrameHandler write = (in ExportVideoFrame frame) => frame.CopyPacked(sink);

            var gpuMs = new List<double>(Measured);
            for (int n = 0; n < Warmup + Measured; n++)
            {
                timer.Begin();
                converter.Submit(texture, n);
                timer.End();
                if (timer.WaitMilliseconds(TimeSpan.FromSeconds(2)) is { } ms && n >= Warmup)
                {
                    gpuMs.Add(ms);
                }

                converter.TryRead(write);
            }

            // Caudal: el patrón de la exportación, encolar n y leer n − 2.
            var clock = Stopwatch.StartNew();
            TimeSpan waitBefore = converter.ReadWait;
            for (int n = 0; n < Measured; n++)
            {
                if (converter.Pending == converter.Capacity - 1)
                {
                    converter.TryRead(write);
                }

                converter.Submit(texture, n);
            }

            while (converter.TryRead(write))
            {
            }

            double fps = Measured / clock.Elapsed.TotalSeconds;
            double waited = (converter.ReadWait - waitBefore).TotalMilliseconds / Measured;

            Assert.True(gpuMs.Count >= Measured / 2, "Demasiadas mediciones disjuntas de la GPU.");
            gpuMs.Sort();
            double median = gpuMs[gpuMs.Count / 2];
            double p95 = gpuMs[(int)(gpuMs.Count * 0.95)];
            output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{gpu.AdapterName}: {sourceWidth}×{sourceHeight} → {width}×{height} {pixels} (nivel {converter.Plan.Level}, {converter.Plan.Kernel}): GPU mediana {median:0.000} ms, p95 {p95:0.000} ms; anillo {fps:0} fps ({1000 / fps:0.00} ms/cuadro, {waited:0.000} ms esperando a la GPU)"));
            Assert.True(fps >= 30, $"El anillo dio {fps:0} fps.");
            if (Strict)
            {
                Assert.True(fps >= minFps, $"{width}×{height} {pixels}: {fps:0} fps (objetivo ≥{minFps}).");
            }
        }
        finally
        {
            ExportGpu.Release(texture);
        }
    }
}
