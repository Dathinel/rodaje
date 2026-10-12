using System.Diagnostics;
using Rodaje.Analysis.Rhythm;
using Xunit.Abstractions;

namespace Rodaje.Analysis.Tests;

public sealed class BeatTests(ITestOutputHelper output)
{
    private const double Tolerance = 0.07;

    private static List<double> Grid(double bpm, double start, double end)
    {
        var beats = new List<double>();
        for (double t = start; t < end - 0.05; t += 60 / bpm)
        {
            beats.Add(t);
        }

        return beats;
    }

    private static BeatAnalysis Analyze(float[] mono)
    {
        var envelope = new OnsetEnvelope(Synth.Rate, 2);
        Synth.Feed(envelope, Synth.Stereo(mono));
        return BeatTracker.Track(envelope);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(120)]
    [InlineData(128)]
    [InlineData(140)]
    [InlineData(160)]
    public void Clicks_at_a_fixed_tempo_give_F_at_least_09_and_the_tempo_within_1_BPM(double bpm)
    {
        var random = new Random((int)bpm);
        float[] mono = Synth.Seconds(30);
        List<double> truth = Grid(bpm, 0.5, 30);
        Synth.Clicks(mono, truth, 0.8, random);
        Synth.Noise(mono, 0.01, random);

        BeatAnalysis result = Analyze(mono);
        (double f, double bias) = Synth.FMeasure(result.Beats, truth, Tolerance);
        output.WriteLine($"{bpm} BPM: F = {f:F3}, tempo {result.Bpm:F2}, sesgo {bias * 1000:F1} ms, {result.Beats.Count}/{truth.Count} pulsos");
        Assert.True(f >= 0.9, $"F = {f:F3}");
        Assert.Equal(bpm, result.Bpm, 1.0);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(118)]
    [InlineData(128)]
    public void A_drum_pattern_over_a_pad_gives_F_at_least_09_and_the_tempo_within_1_BPM(double bpm)
    {
        var random = new Random((int)bpm * 3);
        float[] mono = Synth.Seconds(30);
        Synth.Drums(mono, bpm, 1.0, 0.5, random);
        Synth.Music(mono, 0.05, bpm * 0.999); // un fondo tonal casi a tempo, que no debe mover los pulsos
        BeatAnalysis result = Analyze(mono);
        (double f, double bias) = Synth.FMeasure(result.Beats, Grid(bpm, 1.0, 30), Tolerance);
        output.WriteLine($"batería {bpm} BPM: F = {f:F3}, tempo {result.Bpm:F2}, sesgo {bias * 1000:F1} ms");
        Assert.True(f >= 0.9, $"F = {f:F3}");
        Assert.Equal(bpm, result.Bpm, 1.0);
    }

    /// <summary>
    /// Con clics puros a 174 BPM el doble y la mitad se parecen igual: con la preferencia por omisión (120) sale 87, la
    /// mitad exacta; quien edita drum and bass sube la preferencia y sale 174.
    /// </summary>
    [Fact]
    public void A_fast_tempo_is_found_at_its_octave_or_exactly_with_a_higher_preference()
    {
        var random = new Random(174);
        float[] mono = Synth.Seconds(30);
        List<double> truth = Grid(174, 0.5, 30);
        Synth.Clicks(mono, truth, 0.8, random);
        var envelope = new OnsetEnvelope(Synth.Rate, 1);
        Synth.Feed(envelope, mono);

        Assert.Equal(87, BeatTracker.Track(envelope).Bpm, 1.0);
        BeatAnalysis fast = BeatTracker.Track(envelope, new BeatOptions { PreferredBpm = 170 });
        Assert.Equal(174, fast.Bpm, 1.0);
        Assert.True(Synth.FMeasure(fast.Beats, truth, Tolerance).F >= 0.9);
    }

    [Fact]
    public void Silence_and_noise_have_no_beats()
    {
        Assert.Empty(Analyze(Synth.Seconds(10)).Beats);
        Assert.Equal(0, Analyze(new float[Synth.Rate]).Bpm);
    }

    [Fact]
    [Trait("Categoria", "TiempoReal")]
    public void Analysis_runs_at_least_100_times_real_time()
    {
        var random = new Random(5);
        float[] mono = Synth.Seconds(60);
        Synth.Drums(mono, 124, 0.2, 0.5, random);
        float[] stereo = Synth.Stereo(mono);
        var envelope = new OnsetEnvelope(Synth.Rate, 2);
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 10; i++)
        {
            Synth.Feed(envelope, stereo);
        }

        BeatAnalysis result = BeatTracker.Track(envelope);
        watch.Stop();
        double factor = 600 / watch.Elapsed.TotalSeconds;
        output.WriteLine($"10 min: {watch.Elapsed.TotalSeconds:F2} s ({factor:F0}× tiempo real), {result.Beats.Count} pulsos, {result.Bpm:F2} BPM");
        Assert.True(factor >= 100, $"{factor:F0}×");
        Assert.Equal(124, result.Bpm, 1.0);
    }
}
