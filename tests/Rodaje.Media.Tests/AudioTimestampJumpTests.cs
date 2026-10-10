using Rodaje.Core.Timing;
using Rodaje.Media.Audio;
using Rodaje.Media.Tests.Support;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Media.Tests;

/// <summary>Un salto en las marcas a mitad del archivo: la salida sigue a las marcas y no al conteo de muestras.</summary>
public sealed class AudioTimestampJumpTests(ITestOutputHelper output)
{
    private const int Rate = AudioDecoder.OutputSampleRate;
    private const int Tolerance = Rate / 1000;

    [FfmpegLibraryTheory]
    [InlineData(0.5, "mkv")]
    [InlineData(0.5, "mp4")]
    [InlineData(-0.5, "mkv")]
    [InlineData(-0.5, "mp4")]
    public void Marks_after_a_jump_land_at_their_timestamps(double jump, string extension)
    {
        string clip = TestClips.AacWithTimestampJump(jump, extension);
        using AudioDecoder decoder = AudioDecoder.Open(clip);
        float[] all = ReadAll(decoder);
        IReadOnlyList<AudioMark> marks = AudioCode.Detect(all, Rate, 2);
        output.WriteLine($"{Path.GetFileName(clip)}: {all.Length / 2} muestras; marcas " +
            string.Join(", ", marks.Select(m => $"{m.Second}@{m.Sample}")));

        // Al copiar AAC de MKV a MP4 se pierde el recorte del retardo inicial del códec: todo el archivo queda
        // corrido lo mismo, así que cada marca se mide desde la del segundo 0.
        long origin = Assert.Single(marks, m => m.Second == 0).Sample;
        Assert.InRange(origin, 0, 1024 + Tolerance);
        foreach (int second in new[] { 1, 2, 4, 5, 6 })
        {
            AudioMark? mark = marks.Where(m => m.Second == second).Cast<AudioMark?>().FirstOrDefault();
            Assert.True(mark is not null, $"No se encontró la marca del segundo {second}.");
            long error = mark.Value.Sample - origin - (second * (long)Rate);
            Assert.True(Math.Abs(error) <= Tolerance, $"La marca del segundo {second} quedó corrida {error} muestras.");
        }

        // Con solape, el muxer apila los paquetes repetidos en la última marca de A; el segundo 3 queda ahí y no se mide.
        Assert.All(marks.Where(m => m.Second is not null && (jump > 0 || m.Second != 3)), m =>
            Assert.InRange(m.Sample - origin - (m.Second!.Value * (long)Rate), -Tolerance, Tolerance));
    }

    [FfmpegLibraryTheory]
    [InlineData("mkv")]
    [InlineData("mp4")]
    public void Seeking_past_a_gap_lands_on_the_timestamp(string extension)
    {
        using AudioDecoder decoder = AudioDecoder.Open(TestClips.AacWithTimestampJump(0.5, extension));
        var buffer = new float[Rate * 2 * 8 / 10];
        int read = ReadFully(decoder, buffer);
        long origin = Assert.Single(AudioCode.Detect(buffer.AsSpan(0, read * 2), Rate, 2), m => m.Second == 0).Sample;
        foreach (int second in new[] { 5, 1, 4, 6 })
        {
            long start = (second * (long)Rate) - (Rate / 10);
            decoder.Seek(Time.FromSamples(start, Rate));
            int frames = ReadFully(decoder, buffer);
            AudioMark mark = Assert.Single(AudioCode.Detect(buffer.AsSpan(0, frames * 2), Rate, 2), m => m.Second == second);
            long error = mark.Sample - origin - (Rate / 10);
            Assert.True(Math.Abs(error) <= Tolerance, $"Tras el seek, la marca del segundo {second} quedó corrida {error} muestras.");
        }

        decoder.Seek(Time.FromSeconds(3.2));
        var silence = new float[Rate / 10 * 2];
        Assert.Equal(Rate / 10, ReadFully(decoder, silence));
        Assert.All(silence, s => Assert.Equal(0f, s));
    }

    private static float[] ReadAll(AudioDecoder decoder)
    {
        var all = new List<float>();
        var buffer = new float[1000 * 2];
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
}
