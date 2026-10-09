using Rodaje.Core.Timing;
using Xunit.Abstractions;

namespace Rodaje.TestMedia.Tests;

/// <summary>Genera archivos reales con ffmpeg, los decodifica y comprueba cada cuadro y cada marca de audio.</summary>
public class EndToEndTests(ITestOutputHelper output)
{
    // AAC y Opus desplazan el ataque del chirp unas pocas muestras; 1 ms es 1/33 de cuadro a 29,97.
    private const int AudioToleranceSamples = MediaSpec.AudioSampleRate / 1000;

    internal static TestMediaGenerator Generator => TestMediaGenerator.CreateDefault()!;

    internal static void AssertEveryFrameCarriesItsIndex(GeneratedMedia media, ITestOutputHelper output)
    {
        MediaSpec spec = media.Spec;
        var wrong = new List<string>();
        int decoded = Generator.Ffmpeg.DecodeGrayFrames(media.Path, spec.Width, spec.Height, (index, gray) =>
        {
            int? code = FrameCode.Read(gray, spec.Width, spec.Height, spec.Width);
            if (code != index && wrong.Count < 10)
            {
                wrong.Add($"cuadro {index} leyó {code?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "nada"}");
            }
        });

        output.WriteLine($"{Path.GetFileName(media.Path)}: {decoded} cuadros decodificados con {media.VideoEncoder}.");
        Assert.True(wrong.Count == 0, "Cuadros con código equivocado: " + string.Join("; ", wrong));
        Assert.Equal(spec.FrameCount, decoded);
    }

    internal static void AssertAudioMarksOnEverySecond(GeneratedMedia media, ITestOutputHelper output)
    {
        float[] audio = Generator.Ffmpeg.DecodeAudio(media.Path);
        IReadOnlyList<AudioMark> marks = AudioCode.Detect(audio, MediaSpec.AudioSampleRate, MediaSpec.AudioChannels);

        // Solo cuentan los segundos cuyo chirp de 20 ms cabe entero; uno cortado al final puede o no aparecer.
        long chirp = MediaSpec.AudioSampleRate / 50;
        int expectedMarks = (int)((media.Spec.AudioSampleCount - chirp) / MediaSpec.AudioSampleRate + 1);
        Assert.InRange(marks.Count, expectedMarks, expectedMarks + 1);
        long worst = 0;
        for (int s = 0; s < expectedMarks; s++)
        {
            long error = marks[s].Sample - (long)s * MediaSpec.AudioSampleRate;
            worst = Math.Max(worst, Math.Abs(error));
            Assert.True(Math.Abs(error) <= AudioToleranceSamples, $"La marca del segundo {s} quedó {error} muestras corrida.");
            if ((long)s * MediaSpec.AudioSampleRate + MediaSpec.AudioSampleRate / 2 <= media.Spec.AudioSampleCount)
            {
                Assert.Equal(s % AudioCode.SecondModulus, marks[s].Second);
            }
        }

        output.WriteLine($"{marks.Count} marcas de audio; mayor desvío {worst} muestras ({audio.Length / MediaSpec.AudioChannels} muestras decodificadas, se generaron {media.Spec.AudioSampleCount}).");
    }

    [FfmpegFact(VideoCodec.H264, AudioCodec.Aac)]
    public void H264WithAac_EveryFrameAndEverySecondIsIdentified()
    {
        var spec = new MediaSpec("e2e-h264-aac", 640, 360, Rational.Ntsc30, TimeSpan.FromSeconds(4))
        {
            GopLength = 30,
            BFrames = 2,
            Audio = AudioCodec.Aac,
        };

        GeneratedMedia media = Generator.GetOrCreate(spec);

        AssertEveryFrameCarriesItsIndex(media, output);
        AssertAudioMarksOnEverySecond(media, output);
    }

    [FfmpegFact(VideoCodec.Hevc, AudioCodec.Opus)]
    public void HevcWithOpusInMkv_EveryFrameAndEverySecondIsIdentified()
    {
        var spec = new MediaSpec("e2e-hevc-opus", 640, 360, Rational.Fps25, TimeSpan.FromSeconds(3))
        {
            Codec = VideoCodec.Hevc,
            GopLength = 25,
            BFrames = 2,
            Audio = AudioCodec.Opus,
            Container = ContainerFormat.Mkv,
        };

        GeneratedMedia media = Generator.GetOrCreate(spec);

        AssertEveryFrameCarriesItsIndex(media, output);
        AssertAudioMarksOnEverySecond(media, output);
    }

    [FfmpegFact(VideoCodec.H264)]
    public void StartOffset_KeepsEveryFrame_AndIsReusedFromCache()
    {
        var spec = new MediaSpec("e2e-desplazado", 320, 240, Rational.Ntsc24, TimeSpan.FromSeconds(2))
        {
            Container = ContainerFormat.Mov,
            StartOffset = TimeSpan.FromSeconds(10),
            GopLength = 12,
        };

        GeneratedMedia first = Generator.GetOrCreate(spec);
        GeneratedMedia second = Generator.GetOrCreate(spec);

        AssertEveryFrameCarriesItsIndex(first, output);
        Assert.True(second.FromCache);
        Assert.Equal(first.Path, second.Path);
        Assert.StartsWith(TestMediaGenerator.DefaultCacheDirectory, first.Path, StringComparison.OrdinalIgnoreCase);
    }

    [FfmpegFact(VideoCodec.H264)]
    public void EveryUsableVideoEncoder_ProducesReadableFrames()
    {
        FfmpegTool ffmpeg = Generator.Ffmpeg;
        output.WriteLine($"{ffmpeg.Version} ({ffmpeg.Source}: {ffmpeg.ExecutablePath})");
        int verified = 0;
        foreach (VideoCodec codec in Enum.GetValues<VideoCodec>())
        {
            foreach (string encoder in FfmpegTool.VideoEncoderCandidates(codec))
            {
                if (ffmpeg.ProbeEncoder(encoder) is { } reason)
                {
                    output.WriteLine($"{encoder}: no disponible. {reason.Split('\n')[0]}");
                    continue;
                }

                var spec = new MediaSpec($"e2e-{encoder}", 320, 240, Rational.Fps30, TimeSpan.FromSeconds(1))
                {
                    Codec = codec,
                    Encoder = encoder,
                    GopLength = 10,
                    BFrames = 2,
                };
                GeneratedMedia media = Generator.GetOrCreate(spec);
                AssertEveryFrameCarriesItsIndex(media, output);
                output.WriteLine($"{encoder}: verificado.");
                verified++;
            }
        }

        Assert.True(verified > 0);
    }
}
