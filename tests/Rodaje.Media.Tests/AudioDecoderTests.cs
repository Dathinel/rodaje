using Rodaje.Core.Timing;
using Rodaje.Media.Audio;
using Rodaje.Media.Tests.Support;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Media.Tests;

/// <summary>El audio decodificado se compara con el que produce ffmpeg.exe para el mismo archivo.</summary>
/// <remarks>
/// Con FLAC (sin pérdida y sin estado entre bloques) el seek tiene que dar exactamente las mismas muestras
/// que una lectura continua. Con AAC no puede: el decodificador sintetiza bandas de ruido (PNS) con un
/// generador pseudoaleatorio cuyo estado depende de todo lo decodificado antes, así que ahí se exige la
/// misma posición (el error mínimo está en el desfase 0) y un error pequeño.
/// </remarks>
public sealed class AudioDecoderTests(ITestOutputHelper output)
{
    private const int Rate = AudioDecoder.OutputSampleRate;

    [FfmpegLibraryFact]
    public void Sequential_read_matches_reference_exactly()
    {
        string clip = TestClips.PathOf(TestClips.H264);
        float[] reference = TestClips.AudioReference(clip);
        using AudioDecoder decoder = AudioDecoder.Open(clip);
        Assert.Equal(decoder.Info.Video!.StartTime, decoder.Origin);
        Assert.Equal(0, ReferenceOffset(decoder));

        float[] decoded = ReadAll(decoder, 1000);
        Assert.Equal(reference.Length, decoded.Length);
        Assert.Equal(0, MaxError(reference, decoded, 0, 0, reference.Length / 2));
        Assert.Equal(0, decoder.Read(new float[64]));
    }

    [FfmpegLibraryFact]
    public void Seek_is_bit_exact_for_lossless_audio()
    {
        string clip = TestClips.FlacStereo48k();
        float[] reference = TestClips.AudioReference(clip);
        using AudioDecoder decoder = AudioDecoder.Open(clip);
        Assert.Equal("flac", decoder.Stream.Codec);
        AssertSeeksMatch(decoder, reference, tolerance: 0, requireAlignment: false);
    }

    [FfmpegLibraryFact]
    public void Seek_lands_on_the_exact_sample_with_aac()
    {
        string clip = TestClips.PathOf(TestClips.H264);
        float[] reference = TestClips.AudioReference(clip);
        using AudioDecoder decoder = AudioDecoder.Open(clip);
        AssertSeeksMatch(decoder, reference, tolerance: 5e-2, requireAlignment: true);
    }

    [FfmpegLibraryFact]
    public void Seeking_between_samples_lands_on_the_sample_that_contains_the_time()
    {
        string clip = TestClips.FlacStereo48k();
        float[] reference = TestClips.AudioReference(clip);
        using AudioDecoder decoder = AudioDecoder.Open(clip);
        Time between = Time.FromSamples(30_000, Rate) + Time.FromTicks(Time.TicksPerSecond / Rate / 2);
        decoder.Seek(between);
        Assert.Equal(30_000, decoder.PositionFrames);
        var buffer = new float[256];
        ReadFully(decoder, buffer);
        Assert.Equal(0, MaxError(reference, buffer, 30_000, 0, 128));
    }

    [FfmpegLibraryFact]
    public void Resampled_lossless_audio_keeps_its_phase_after_seeking()
    {
        string clip = TestClips.FlacMono44k();
        float[] reference = TestClips.AudioReference(clip);
        using AudioDecoder decoder = AudioDecoder.Open(clip);
        Assert.Equal((44100, 1), (decoder.Stream.SampleRate, decoder.Stream.Channels));

        float[] decoded = ReadAll(decoder, 777);
        Assert.Equal(reference.Length, decoded.Length);
        Assert.Equal(0, MaxError(reference, decoded, 0, 0, reference.Length / 2));
        AssertSeeksMatch(decoder, reference, tolerance: 1e-6, requireAlignment: true);
    }

    [FfmpegLibraryFact]
    public void Resampled_aac_stays_aligned_after_seeking()
    {
        string clip = TestClips.MonoAudio44k();
        float[] reference = TestClips.AudioReference(clip);
        using AudioDecoder decoder = AudioDecoder.Open(clip);
        float[] decoded = ReadAll(decoder, 777);
        Assert.Equal(reference.Length, decoded.Length);
        Assert.Equal(0, MaxError(reference, decoded, 0, 0, reference.Length / 2));
        AssertSeeksMatch(decoder, reference, tolerance: 5e-2, requireAlignment: true);
    }

    [FfmpegLibraryFact]
    public void Reading_past_the_end_returns_zero()
    {
        string clip = TestClips.PathOf(TestClips.H264);
        using AudioDecoder decoder = AudioDecoder.Open(clip);
        decoder.Seek(decoder.Duration + Time.FromSeconds(1));
        Assert.Equal(0, decoder.Read(new float[512]));
        decoder.Seek(Time.Zero);
        Assert.Equal(256, decoder.Read(new float[512]));
        Assert.Equal(Time.FromSamples(256, Rate), decoder.Position);
    }

    [FfmpegLibraryFact]
    public void Odd_buffers_and_files_without_audio_are_rejected()
    {
        AudioDecoder decoder = AudioDecoder.Open(TestClips.PathOf(TestClips.H264));
        Assert.Throws<ArgumentException>(() => decoder.Read(new float[3]));
        Assert.Throws<MediaException>(() => AudioDecoder.Open(TestClips.PathOf(TestClips.Hevc)));
        decoder.Dispose();
        decoder.Dispose();
        Assert.Throws<ObjectDisposedException>(() => decoder.Read(new float[2]));
    }

    private void AssertSeeksMatch(AudioDecoder decoder, float[] reference, double tolerance, bool requireAlignment)
    {
        long total = reference.Length / 2;
        var random = new Random(48000);
        var positions = new List<long> { 0, 1, 146, 147, 1023, 1024, 1025, 2047, 2048, 4097, 47_999, 48_000, total - 4096, total - 1 };
        while (positions.Count < 60)
        {
            positions.Add(random.NextInt64(total));
        }

        var buffer = new float[2048 * 2];
        double worst = 0;
        foreach (long position in positions)
        {
            decoder.Seek(Time.FromSamples(position, Rate));
            Assert.Equal(position, decoder.PositionFrames);
            int frames = ReadFully(decoder, buffer);
            Assert.Equal((int)Math.Min(2048, total - position), frames);
            Assert.Equal(position + frames, decoder.PositionFrames);

            double error = MaxError(reference, buffer, position, 0, frames);
            worst = Math.Max(worst, error);
            Assert.True(error <= tolerance, $"Tras el seek a la muestra {position} el error es {error:E2} (tolerancia {tolerance:E0}).");
            if (requireAlignment && frames > 64)
            {
                double early = MaxError(reference, buffer, position - 1, 0, frames);
                double late = MaxError(reference, buffer, position + 1, 0, frames);
                Assert.True(error < early && error < late,
                    $"Tras el seek a {position} el audio no cae en la muestra pedida: error {error:E2}, desplazado -1 {early:E2}, +1 {late:E2}.");
            }
        }

        output.WriteLine($"{decoder.Stream.Codec} {decoder.Stream.SampleRate} Hz: {positions.Count} seeks, error máximo {worst:E2}.");
    }

    private static long ReferenceOffset(AudioDecoder decoder) =>
        (decoder.Stream.StartTime - decoder.Origin).ToSamples(Rate);

    private static float[] ReadAll(AudioDecoder decoder, int chunkFrames)
    {
        var all = new List<float>();
        var buffer = new float[chunkFrames * 2];
        int frames;
        while ((frames = decoder.Read(buffer)) > 0)
        {
            all.AddRange(buffer.AsSpan(0, frames * 2));
        }

        return [.. all];
    }

    private static int ReadFully(AudioDecoder decoder, float[] buffer)
    {
        int total = 0;
        int frames;
        while (total < buffer.Length / 2 && (frames = decoder.Read(buffer.AsSpan(total * 2))) > 0)
        {
            total += frames;
        }

        return total;
    }

    private static double MaxError(float[] reference, float[] decoded, long referenceStart, long decodedStart, int frames)
    {
        double max = 0;
        for (long i = 0; i < frames * 2L; i++)
        {
            long r = (referenceStart * 2) + i;
            float expected = r >= 0 && r < reference.Length ? reference[r] : 0;
            max = Math.Max(max, Math.Abs(expected - decoded[(decodedStart * 2) + i]));
        }

        return max;
    }
}
