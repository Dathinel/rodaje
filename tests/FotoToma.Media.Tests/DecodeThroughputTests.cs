using System.Diagnostics;
using FotoToma.Media.Tests.Support;
using FotoToma.Media.Video;
using FotoToma.Playback;
using Xunit.Abstractions;

namespace FotoToma.Media.Tests;

/// <summary>Mide cuadros por segundo (sin leer de vuelta a la CPU) para el informe; solo exige ir más rápido que el tiempo real.</summary>
[Trait("Categoria", "Rendimiento")]
public sealed class DecodeThroughputTests(ITestOutputHelper output)
{
    private static readonly ClipSpec FullHd = new("h264-1080p60-g120-bf2", ClipCodec.H264, 60, 1, 600, Width: 1920, Height: 1080, Gop: 120);

    [HardwareFact]
    public void Hardware_decoding_of_1080p_runs_faster_than_real_time() => Measure(DecodeMode.Hardware);

    [FfmpegFact]
    public void Software_decoding_of_1080p_runs_faster_than_real_time() => Measure(DecodeMode.SoftwareCpu);

    private void Measure(DecodeMode mode)
    {
        string clip = TestClips.PathOf(FullHd);
        using VideoDecoder decoder = VideoDecoder.Open(clip, DecodeModes.Options(mode));
        decoder.GetFrame(0)!.Dispose();
        using var process = Process.GetCurrentProcess();
        TimeSpan cpuBefore = process.TotalProcessorTime;
        var watch = Stopwatch.StartNew();
        int n = 1;
        for (; n < FullHd.Frames; n++)
        {
            using DecodedFrame? frame = decoder.GetFrame(n);
            Assert.NotNull(frame);
        }

        watch.Stop();
        process.Refresh();
        double cpu = (process.TotalProcessorTime - cpuBefore).TotalSeconds;
        double fps = (n - 1) / watch.Elapsed.TotalSeconds;

        var seekWatch = Stopwatch.StartNew();
        var random = new Random(7);
        const int Seeks = 40;
        for (int i = 0; i < Seeks; i++)
        {
            decoder.GetFrame(random.Next(FullHd.Frames))!.Dispose();
        }

        seekWatch.Stop();
        output.WriteLine($"{mode}: {fps:0} fps en 1080p60 H.264 ({n - 1} cuadros, {watch.Elapsed.TotalSeconds:0.00} s, " +
            $"CPU {cpu / watch.Elapsed.TotalSeconds * 100 / Environment.ProcessorCount:0.0} % de {Environment.ProcessorCount} hilos); " +
            $"seek aleatorio medio {seekWatch.Elapsed.TotalMilliseconds / Seeks:0.0} ms (GOP 120).");
        Assert.True(fps > 60, $"Solo {fps:0} fps.");
    }
}
