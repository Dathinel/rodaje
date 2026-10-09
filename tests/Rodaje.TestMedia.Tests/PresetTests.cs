using Xunit.Abstractions;

namespace Rodaje.TestMedia.Tests;

/// <summary>Los archivos de los criterios de la fase 2; la primera vez se codifican y luego salen de la caché.</summary>
public class PresetTests(ITestOutputHelper output)
{
    private GeneratedMedia GenerateAndCheckStructure(MediaSpec spec)
    {
        GeneratedMedia media = EndToEndTests.Generator.GetOrCreate(spec);
        output.WriteLine(media.FromCache
            ? $"{spec.Name}: de la caché ({media.VideoEncoder})."
            : $"{spec.Name}: generado con {media.VideoEncoder} en {media.GenerationTime.TotalSeconds:F1} s.");

        const int Sampled = 300;
        IReadOnlyDictionary<char, int>? types = EndToEndTests.Generator.Ffmpeg.CountPictureTypes(media.Path, Sampled);
        if (types is not null)
        {
            output.WriteLine($"Tipos en los primeros {Sampled} cuadros: " + string.Join(", ", types.OrderBy(t => t.Key).Select(t => $"{t.Key}={t.Value}")));
            Assert.Equal(Math.Min(Sampled, spec.FrameCount), types.Values.Sum());
            if (spec.BFrames > 0 && media.VideoEncoder != "libopenh264")
            {
                Assert.True(types.GetValueOrDefault('B') > 0, $"{media.VideoEncoder} no produjo cuadros B.");
            }
        }

        return media;
    }

    [FfmpegFact(VideoCodec.H264)]
    public void H264Ntsc30BFrames()
    {
        GeneratedMedia media = GenerateAndCheckStructure(TestMediaPresets.H264Ntsc30BFrames);

        EndToEndTests.AssertEveryFrameCarriesItsIndex(media, output);
    }

    [FfmpegFact(VideoCodec.Hevc)]
    public void Hevc25Mkv()
    {
        GeneratedMedia media = GenerateAndCheckStructure(TestMediaPresets.Hevc25Mkv);

        EndToEndTests.AssertEveryFrameCarriesItsIndex(media, output);
    }

    [FfmpegFact(VideoCodec.H264, AudioCodec.Aac)]
    public void H264Ntsc24Aac()
    {
        GeneratedMedia media = GenerateAndCheckStructure(TestMediaPresets.H264Ntsc24Aac);

        EndToEndTests.AssertEveryFrameCarriesItsIndex(media, output);
        EndToEndTests.AssertAudioMarksOnEverySecond(media, output);
    }

    [FfmpegFact(VideoCodec.H264, AudioCodec.Aac, LongRunning = true)]
    public void Drift720p10Min()
    {
        GeneratedMedia media = GenerateAndCheckStructure(TestMediaPresets.Drift720p10Min);

        EndToEndTests.AssertEveryFrameCarriesItsIndex(media, output);
        EndToEndTests.AssertAudioMarksOnEverySecond(media, output);
    }
}
