using System.Diagnostics;
using Rodaje.Media.Gpu;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Xunit.Abstractions;

namespace Rodaje.Media.Tests;

/// <summary>
/// Mide la ruta de decodificación de cada archivo de una carpeta (D3D11VA o software y con cuántos hilos), con el
/// dispositivo como en la vista previa. Solo con <c>RODAJE_MEDIR_CARPETA</c>: los archivos los genera quien mide.
/// </summary>
[Trait("Categoria", "Rendimiento")]
public sealed class DecodePathBenchmarkTests(ITestOutputHelper output)
{
    private static string? Folder => Environment.GetEnvironmentVariable("RODAJE_MEDIR_CARPETA");

    [Fact]
    public void Measures_each_file_with_the_preview_device()
    {
        if (Folder is not { } folder || !Directory.Exists(folder))
        {
            output.WriteLine("Sin RODAJE_MEDIR_CARPETA: no se mide.");
            return;
        }

        using GpuDevice device = GpuDevice.Create();
        output.WriteLine(device.ToString());
        output.WriteLine(GpuProfileReader.Read(null).Describe());
        MeasureFolder(folder, device);
        output.WriteLine("Sin dispositivo (solo CPU, sin subir a la GPU):");
        MeasureFolder(folder, null);
    }

    private void MeasureFolder(string folder, GpuDevice? device)
    {
        foreach (string path in Directory.EnumerateFiles(folder).Where(p => p.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
        {
            using VideoDecoder decoder = VideoDecoder.Open(path, new VideoDecoderOptions { Device = device });
            decoder.GetFrame(0)!.Dispose();
            int frames = (int)Math.Min(decoder.FrameCount, 240);
            var watch = Stopwatch.StartNew();
            for (int n = 1; n < frames; n++)
            {
                using DecodedFrame? frame = decoder.GetFrame(n);
                Assert.NotNull(frame);
            }

            watch.Stop();
            double fps = (frames - 1) / watch.Elapsed.TotalSeconds;
            output.WriteLine($"{Path.GetFileName(path)}: {fps:0.0} fps ({fps / decoder.FrameRate.ToDouble():0.00}× tiempo real), " +
                $"{(decoder.UsesHardwareDecoding ? "D3D11VA" : "software")}, {decoder.Stream.Codec} {decoder.Stream.PixelFormat}");
        }
    }
}
