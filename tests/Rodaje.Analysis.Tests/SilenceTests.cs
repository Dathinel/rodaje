using System.Diagnostics;
using Rodaje.Analysis.Silence;
using Xunit.Abstractions;

namespace Rodaje.Analysis.Tests;

public sealed class SilenceTests(ITestOutputHelper output)
{
    /// <summary>Habla con silencios exactos (digitales o con ruido de fondo a −60 dBFS) de 0,25 a 2 s.</summary>
    private static (float[] Audio, List<TimeRange> Silences) Talk(double noise, int seed)
    {
        var random = new Random(seed);
        float[] mono = Synth.Seconds(40);
        var silences = new List<TimeRange>();
        double t = 0.6;
        Synth.Speech(mono, 0, t, 0.3, random);
        double[] gaps = [0.5, 1.0, 0.25, 2.0, 0.7, 0.45, 1.5, 0.9, 0.6, 1.2];
        foreach (double gap in gaps)
        {
            silences.Add(new(t, t + gap));
            t += gap;
            double phrase = 0.8 + (random.NextDouble() * 2);
            Synth.Speech(mono, t, t + phrase, 0.15 + (random.NextDouble() * 0.3), random);
            t += phrase;
        }

        silences.Add(new(t, 40));
        Synth.Noise(mono, noise, random);
        return (Synth.Stereo(mono), silences);
    }

    [Theory]
    [InlineData(0.0, 1)]
    [InlineData(0.001, 2)]
    [InlineData(0.001, 3)]
    public void Synthetic_silences_are_found_within_20_ms(double noise, int seed)
    {
        (float[] audio, List<TimeRange> truth) = Talk(noise, seed);
        var envelope = new LevelEnvelope(Synth.Rate, 2);
        Synth.Feed(envelope, audio);
        SilenceAnalysis result = SilenceDetector.Detect(envelope, new SilenceOptions { MinimumSilence = 0.2, PaddingBefore = 0, PaddingAfter = 0 });

        Assert.Equal(truth.Count, result.Silences.Count);
        double worst = 0;
        for (int i = 0; i < truth.Count; i++)
        {
            worst = Math.Max(worst, Math.Max(Math.Abs(result.Silences[i].Start - truth[i].Start), Math.Abs(result.Silences[i].End - truth[i].End)));
        }

        output.WriteLine($"ruido {noise}: {truth.Count} silencios, error máximo de borde {worst * 1000:F1} ms, referencia {result.ReferenceDb:F1} dBFS");
        Assert.True(worst <= 0.020, $"Error de borde {worst * 1000:F1} ms");
    }

    [Fact]
    public void Margins_minimum_duration_and_clicks_shape_the_cuts()
    {
        var random = new Random(7);
        float[] mono = Synth.Seconds(6);
        Synth.Speech(mono, 0, 1, 0.3, random);
        Synth.Speech(mono, 1.3, 2, 0.3, random);      // 0,3 s: más corto que el mínimo
        Synth.Clicks(mono, [3.0], 0.5, random);        // un clic en medio del silencio de 2 a 4 s
        Synth.Speech(mono, 4, 6, 0.3, random);
        var envelope = new LevelEnvelope(Synth.Rate, 1);
        Synth.Feed(envelope, mono);

        SilenceAnalysis result = SilenceDetector.Detect(envelope, new SilenceOptions { MinimumSilence = 0.4, PaddingBefore = 0.1, PaddingAfter = 0.15 });
        TimeRange cut = Assert.Single(result.Cuts);
        Assert.Equal(2.1, cut.Start, 0.01);
        Assert.Equal(3.85, cut.End, 0.01);
        Assert.Equal(1.75, result.CutSeconds, 0.02);
    }

    [Fact]
    public void Voice_ranges_are_inverted_to_silences_with_minimum_and_margins()
    {
        SilenceAnalysis result = SilenceDetector.DetectFromVoice(
            [new TimeRange(1, 2), new TimeRange(2.2, 4)],
            5,
            new SilenceOptions { MinimumSilence = 0.4, PaddingBefore = 0.1, PaddingAfter = 0.2 });

        Assert.Equal(2, result.Silences.Count);
        Assert.Equal(new TimeRange(0, 1), result.Silences[0]);
        Assert.Equal(new TimeRange(4, 5), result.Silences[1]);
        Assert.Equal(2, result.Cuts.Count);
        Assert.Equal(new TimeRange(0, 0.8), result.Cuts[0]);
        Assert.Equal(new TimeRange(4.1, 5), result.Cuts[1]);
    }

    [Fact]
    public void Leading_and_trailing_silence_is_cut_to_the_edge_and_all_silence_is_one_cut()
    {
        var random = new Random(3);
        float[] mono = Synth.Seconds(4);
        Synth.Speech(mono, 1, 3, 0.3, random);
        var envelope = new LevelEnvelope(Synth.Rate, 1);
        Synth.Feed(envelope, mono);
        SilenceAnalysis result = SilenceDetector.Detect(envelope);
        Assert.Equal(2, result.Cuts.Count);
        Assert.Equal(0, result.Cuts[0].Start);
        Assert.Equal(0.9, result.Cuts[0].End, 0.01);
        Assert.Equal(4, result.Cuts[1].End, 0.001);

        var empty = new LevelEnvelope(Synth.Rate, 2);
        Synth.Feed(empty, new float[Synth.Rate * 2]);
        SilenceAnalysis silent = SilenceDetector.Detect(empty);
        Assert.Equal(1, Assert.Single(silent.Cuts).End, 0.001);
    }

    [Fact]
    public void Blocks_must_be_whole_frames()
    {
        var envelope = new LevelEnvelope(Synth.Rate, 2);
        Assert.Throws<ArgumentException>(() => envelope.Push(new float[3]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LevelEnvelope(100, 1));
    }

    /// <summary>Criterio de la fase 7: una hora en ≤10 s. Se mide el análisis (la decodificación va aparte).</summary>
    [Fact]
    [Trait("Categoria", "TiempoReal")]
    public void One_hour_of_stereo_48k_is_analyzed_in_under_10_seconds()
    {
        (float[] audio, _) = Talk(0.001, 11);
        float[] minute = audio.AsSpan(0, Synth.Rate * 2 * 30).ToArray();
        var envelope = new LevelEnvelope(Synth.Rate, 2);
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 120; i++)
        {
            envelope.Push(minute);
        }

        SilenceAnalysis result = SilenceDetector.Detect(envelope);
        watch.Stop();
        output.WriteLine($"1 h: {watch.Elapsed.TotalSeconds:F2} s ({3600 / watch.Elapsed.TotalSeconds:F0}× tiempo real), {result.Cuts.Count} cortes");
        Assert.Equal(3600, envelope.Duration, 0.001);
        Assert.True(watch.Elapsed.TotalSeconds <= 10, $"{watch.Elapsed.TotalSeconds:F1} s");
    }
}
