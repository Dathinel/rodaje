using System.Diagnostics;
using Rodaje.Media.Tests.Support;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Media.Tests;

/// <summary>
/// Material de 120 y 240 fps en una línea de 30 o 60: cuánto cuesta decodificar todos los cuadros y solo los que la
/// línea de tiempo pide (uno de cada N), con el decodificador por software. Mide, no exige más que ir más rápido que el
/// tiempo real de la línea de tiempo.
/// </summary>
[Trait("Categoria", "Rendimiento")]
public sealed class HighFrameRateDecodeTests(ITestOutputHelper output)
{
    [FfmpegLibraryTheory]
    [InlineData(120, 30, 720)]
    [InlineData(120, 60, 720)]
    [InlineData(240, 30, 720)]
    [InlineData(240, 60, 720)]
    [InlineData(120, 30, 1080)]
    public void Decoding_only_the_frames_the_timeline_asks_for_keeps_up_with_the_timeline(int sourceFps, int timelineFps, int height)
    {
        int seconds = 5;
        var spec = new ClipSpec($"h264-{height}p{sourceFps}-g{sourceFps * 2}-bf2", ClipCodec.H264, sourceFps, 1, sourceFps * seconds,
            Width: height * 16 / 9, Height: height, Gop: sourceFps * 2);
        string clip = TestClips.PathOf(spec);
        int step = sourceFps / timelineFps;

        double all = Measure(clip, spec.Frames, 1, out int allFrames);
        double some = Measure(clip, spec.Frames, step, out int someFrames);
        output.WriteLine($"{sourceFps} fps a {height}p en una línea de {timelineFps}: todos los cuadros {allFrames / all:0} cuadros/s " +
            $"({all / seconds:0.00} s de decodificación por s de material); uno de cada {step}: {someFrames / some:0} cuadros pedidos/s " +
            $"({some / seconds:0.00} s por s de material; {someFrames / some / timelineFps:0.0} veces el tiempo real de la línea).");
        Assert.True(someFrames / some > timelineFps, $"Solo {someFrames / some:0} cuadros/s para una línea de {timelineFps}.");
    }

    private static double Measure(string clip, int frames, int step, out int requested)
    {
        using VideoDecoder decoder = VideoDecoder.Open(clip, DecodeModes.Options(DecodeMode.SoftwareCpu));
        decoder.GetFrame(0)!.Dispose();
        var watch = Stopwatch.StartNew();
        requested = 0;
        for (int n = step; n < frames; n += step)
        {
            using DecodedFrame? frame = decoder.GetFrame(n);
            Assert.NotNull(frame);
            requested++;
        }

        return watch.Elapsed.TotalSeconds;
    }
}
