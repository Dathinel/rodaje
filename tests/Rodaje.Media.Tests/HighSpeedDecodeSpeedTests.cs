using System.Diagnostics;
using System.Globalization;
using Rodaje.Core.Timing;
using Rodaje.Media.Probe;
using Rodaje.Media.Tests.Support;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Media.Tests;

/// <summary>
/// Cifras para los criterios de cuadros perdidos a 2× y 4× (fase 5c): velocidad de decodificación en 1080p y 4K, H.264 y
/// HEVC, por hardware y por software, exacta y con los modos de alta velocidad. Las anota en la salida de la prueba.
/// </summary>
[Trait("Categoria", "Rendimiento")]
public sealed class HighSpeedDecodeSpeedTests(ITestOutputHelper output)
{
    private const int Frames = 240;

    [HardwareFact(VideoCodec.H264, VideoCodec.Hevc, LongRunning = true)]
    public void Decode_speed_for_high_speed_playback()
    {
        TestMediaGenerator generator = TestMediaGenerator.CreateDefault()!;
        var rows = new List<string>();
        foreach ((string label, int width, int height) in new[] { ("1080p", 1920, 1080), ("4k", 3840, 2160) })
        {
            foreach (VideoCodec codec in new[] { VideoCodec.H264, VideoCodec.Hevc })
            {
                // Los HEVC son los de TimelineLoadTests (misma especificación, mismo archivo en caché).
                string name = codec == VideoCodec.Hevc ? $"carga-hevc-{label}-0" : $"medida-h264-{label}";
                var spec = new MediaSpec(name, width, height, Rational.Fps30, TimeSpan.FromSeconds(20))
                {
                    Codec = codec,
                    GopLength = 30,
                    BFrames = 2,
                };
                string path = generator.GetOrCreate(spec).Path;
                foreach (DecodeMode mode in new[] { DecodeMode.Hardware, DecodeMode.SoftwareUpload })
                {
                    double exact = Measure(path, mode, DecodeAccuracy.Exact, step: 1, out _);
                    double skip2 = Measure(path, mode, DecodeAccuracy.SkipNonReference, step: 2, out double decoded2);
                    double skip4 = Measure(path, mode, DecodeAccuracy.SkipNonReference, step: 4, out double decoded4);
                    double keys = Measure(path, mode, DecodeAccuracy.Keyframe, step: 16, out _);
                    rows.Add(string.Create(CultureInfo.InvariantCulture,
                        $"{label} {codec} {mode}: exacto {exact * 30:0} fps ({exact:0.0}×); sin no-referencia a 2× {skip2:0.0}× " +
                        $"({decoded2:0} cuadros/s), a 4× {skip4:0.0}× ({decoded4:0} cuadros/s); keyframes a 16× {keys:0.0}×"));
                    if (mode == DecodeMode.Hardware)
                    {
                        Assert.True(exact > 1, $"{label} {codec}: el hardware no llega al tiempo real ({exact:0.00}×).");
                    }
                }

                var watch = Stopwatch.StartNew();
                KeyframeIndex index = KeyframeIndex.Read(path);
                double indexMs = watch.Elapsed.TotalMilliseconds;
                watch.Restart();
                KeyframeIndex scanned = KeyframeIndex.Read(path, useContainerIndex: false);
                double scanMs = watch.Elapsed.TotalMilliseconds;
                Assert.Equal(index.Frames, scanned.Frames);
                rows.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{label} {codec}: índice de {index.Frames.Count} keyframes en {indexMs:0} ms ({index.Source}), recorriendo paquetes {scanMs:0} ms " +
                    $"({new FileInfo(path).Length / 1e6:0} MB)"));
            }
        }

        foreach (string row in rows)
        {
            output.WriteLine(row);
        }
    }

    // Devuelve cuántas veces el tiempo real avanza el origen pidiendo cada `step` cuadros, y los cuadros por segundo que
    // salieron del decodificador.
    private static double Measure(string path, DecodeMode mode, DecodeAccuracy accuracy, int step, out double decodedPerSecond)
    {
        using VideoDecoder decoder = VideoDecoder.Open(path, DecodeModes.Options(mode) with { SkipNonReference = true });
        decoder.GetFrame(0, accuracy)!.Dispose();
        long decodedBefore = decoder.Statistics.DecodedFrames;
        int last = (int)Math.Min(decoder.FrameCount - 1, step == 1 ? Frames : Frames * 2L);
        var watch = Stopwatch.StartNew();
        for (int n = step; n <= last; n += step)
        {
            using DecodedFrame? frame = decoder.GetFrame(n, accuracy);
            Assert.NotNull(frame);
        }

        watch.Stop();
        double seconds = Math.Max(watch.Elapsed.TotalSeconds, 1e-6);
        decodedPerSecond = (decoder.Statistics.DecodedFrames - decodedBefore) / seconds;
        return last / seconds / decoder.FrameRate.ToDouble();
    }
}
