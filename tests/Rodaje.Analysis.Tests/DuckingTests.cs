using System.Diagnostics;
using Rodaje.Analysis.Ducking;
using Xunit.Abstractions;

namespace Rodaje.Analysis.Tests;

public sealed class DuckingTests(ITestOutputHelper output)
{
    private static readonly TimeRange[] Phrases = [new(2.0, 4.5), new(4.8, 6.0), new(9.0, 12.3), new(16.0, 16.6), new(20.0, 23.0)];

    /// <summary>Pista de voz sintética con la música de fondo filtrándose a −30 dB, como un micrófono en la sala.</summary>
    private static float[] VoiceTrack(int seed)
    {
        var random = new Random(seed);
        float[] mono = Synth.Seconds(25);
        foreach (TimeRange p in Phrases)
        {
            Synth.Phrase(mono, p.Start, p.End, 0.3);
        }

        float[] bleed = Synth.Seconds(25);
        Synth.Music(bleed, 0.3 * 0.0316, 120);
        for (int i = 0; i < mono.Length; i++)
        {
            mono[i] += bleed[i];
        }

        Synth.Noise(mono, 0.0005, random);
        return Synth.Stereo(mono);
    }

    private static async Task<IReadOnlyList<TimeRange>> DetectAsync(IVoiceActivityDetector detector, float[] stereo)
    {
        IVoiceActivitySession session = detector.Begin(Synth.Rate, 2);
        Synth.Feed(session, stereo);
        return await session.FinishAsync();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Voice_over_music_bleed_is_found_within_30_ms(int seed)
    {
        IReadOnlyList<TimeRange> voice = await DetectAsync(new EnergyVoiceDetector(), VoiceTrack(seed));
        // La pausa de 0,3 s entre las dos primeras frases se une (MinimumGap).
        TimeRange[] expected = [new(2.0, 6.0), Phrases[2], Phrases[3], Phrases[4]];
        Assert.Equal(expected.Length, voice.Count);
        double worst = expected.Zip(voice, (e, v) => Math.Max(Math.Abs(e.Start - v.Start), Math.Abs(e.End - v.End))).Max();
        output.WriteLine($"semilla {seed}: error máximo de borde {worst * 1000:F1} ms");
        Assert.True(worst <= 0.03, $"{worst * 1000:F1} ms");
    }

    [Fact]
    public async Task Music_alone_is_not_voice()
    {
        float[] mono = Synth.Seconds(10);
        Synth.Music(mono, 0.3, 120);
        IVoiceActivitySession session = new EnergyVoiceDetector().Begin(Synth.Rate, 1);
        Synth.Feed(session, mono);
        Assert.Empty(await session.FinishAsync());
    }

    [Theory]
    [InlineData(0.03)]
    [InlineData(0.1)]
    [InlineData(0.0)]
    public async Task Music_is_fully_down_at_least_30_ms_before_each_voice_and_comes_back_after(double anticipation)
    {
        IReadOnlyList<TimeRange> voice = await DetectAsync(new EnergyVoiceDetector(), VoiceTrack(4));
        var options = new DuckingOptions { ReductionDb = -14, Anticipation = anticipation, Release = 0.6 };
        List<GainPoint> plan = DuckingPlanner.Plan(voice, options);

        double leadWorst = double.MaxValue;
        foreach (TimeRange p in Phrases)
        {
            Assert.Equal(-14, DuckingPlanner.OffsetAt(plan, p.Start - 0.03), 6);
            Assert.Equal(-14, DuckingPlanner.OffsetAt(plan, (p.Start + p.End) / 2), 6);
            double fullyDown = plan.Where(g => g.OffsetDb == -14 && g.Time <= p.Start).Select(g => g.Time).DefaultIfEmpty(double.NaN).Max();
            leadWorst = Math.Min(leadWorst, p.Start - fullyDown);
        }

        output.WriteLine($"anticipación pedida {anticipation * 1000:F0} ms: la música está abajo al menos {leadWorst * 1000:F1} ms antes de la voz");
        Assert.True(leadWorst >= 0.03 - 1e-9);
        Assert.Equal(0, DuckingPlanner.OffsetAt(plan, 14.5));   // entre frases lejanas vuelve a su volumen
        Assert.Equal(0, DuckingPlanner.OffsetAt(plan, 24.5));
        Assert.Equal(-7, DuckingPlanner.OffsetAt(plan, 23.0 + options.Hold + 0.3), 1.0);  // a mitad de la relajación
    }

    [Fact]
    public void Close_phrases_keep_the_music_down_instead_of_pumping()
    {
        List<GainPoint> plan = DuckingPlanner.Plan([new(1, 2), new(2.4, 3)]);
        Assert.Equal(4, plan.Count);
        Assert.Equal(-12, DuckingPlanner.OffsetAt(plan, 2.2));
        Assert.Empty(DuckingPlanner.Plan([new(1, 2)], new DuckingOptions { ReductionDb = 0 }));
    }

    [Fact]
    [Trait("Categoria", "TiempoReal")]
    public async Task Detection_runs_at_least_100_times_real_time()
    {
        float[] stereo = VoiceTrack(9);
        IVoiceActivitySession session = new EnergyVoiceDetector().Begin(Synth.Rate, 2);
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 8; i++)
        {
            Synth.Feed(session, stereo);
        }

        IReadOnlyList<TimeRange> voice = await session.FinishAsync();
        watch.Stop();
        double factor = 200 / watch.Elapsed.TotalSeconds;
        output.WriteLine($"200 s de voz: {watch.Elapsed.TotalMilliseconds:F0} ms ({factor:F0}× tiempo real), {voice.Count} tramos");
        Assert.True(factor >= 100);
    }
}
