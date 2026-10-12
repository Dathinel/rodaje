using Rodaje.Audio.Metering;
using Rodaje.Core.Commands;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Offline;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Xunit;
using Xunit.Abstractions;
using static Rodaje.Engine.Tests.Support.Seconds;
using A = Rodaje.Core.Model.BuiltInEffects.AudioParameters;

namespace Rodaje.Engine.Tests.Timeline;

/// <summary>
/// Sonoridad contra las señales de EBU Tech 3341 (casos 1–5 y de pico verdadero 15–18), medida fuera de línea y con el
/// medidor en vivo (analizador, anillo e integrador), y «Normalizar» por clip y de la mezcla.
/// </summary>
public sealed class AudioLoudnessTests(ITestOutputHelper output)
{
    private const int Rate = 48_000;

    public static TheoryData<string, double[], double[], double> Tech3341 => new()
    {
        { "caso 1", [-23], [20], -23 },
        { "caso 2", [-33], [20], -33 },
        { "caso 3", [-36, -23, -36], [10, 60, 10], -23 },
        { "caso 4", [-72, -36, -23, -36, -72], [10, 10, 60, 10, 10], -23 },
        { "caso 5", [-26, -20, -26], [20, 20.1, 20], -23 },
    };

    [Theory]
    [MemberData(nameof(Tech3341))]
    public void Integrated_loudness_matches_ebu_tech_3341(string name, double[] levels, double[] seconds, double expected)
    {
        float[] signal = Steps(levels, seconds);
        var meter = new LoudnessMeter();
        meter.Process(signal);
        double offline = meter.Result().IntegratedLufs;
        (double live, double momentary, double shortTerm) = Live(signal);

        output.WriteLine($"{name}: fuera de línea {offline:0.000} LUFS, en vivo {live:0.000} (M {momentary:0.00}, S {shortTerm:0.00})");
        Assert.Equal(expected, offline, 0.1);
        Assert.Equal(expected, live, 0.1);
        if (levels.Length == 1)
        {
            Assert.Equal(expected, momentary, 0.1);
            Assert.Equal(expected, shortTerm, 0.1);
        }
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(45.0)]
    [InlineData(60.0)]
    [InlineData(67.5)]
    public void True_peak_of_the_quarter_rate_sine_is_within_the_tech_3341_tolerance(double phaseDegrees)
    {
        // Casos 15 a 18: seno de fs/4 con pico −6 dBFS y la fase corrida; el pico verdadero es −6,0 dBTP (+0,2 / −0,4).
        float[] signal = new float[2 * Rate];
        double phase = phaseDegrees * Math.PI / 180;
        for (int i = 0; i < Rate; i++)
        {
            float v = (float)(0.5 * Math.Sin((Math.PI / 2 * i) + phase));
            signal[2 * i] = v;
            signal[(2 * i) + 1] = v;
        }

        var meter = new LoudnessMeter();
        meter.Process(signal);
        var ring = new AudioLevelRing();
        new AudioLevelAnalyzer(ring).Process(signal, 0);
        double live = Decibels.FromAmplitude(ring.Measure(0, Rate, 0).Left.TruePeak);

        output.WriteLine($"fase {phaseDegrees}°: {meter.Result().TruePeakDbtp:0.000} dBTP fuera de línea, {live:0.000} en vivo");
        Assert.InRange(meter.Result().TruePeakDbtp, -6.4, -5.8);
        Assert.InRange(live, -6.4, -5.8);
    }

    [Fact]
    public void Normalizing_a_clip_measures_it_alone_and_shifts_its_gain_in_one_step()
    {
        var b = new TimelineBuilder(0, 1);
        b.AddMedia("voz", Rational.Fps30, 900, video: false);
        Clip clip = b.AddAudio(0, "voz", S(1), Time.Zero, S(20));
        clip.Effects.Add(new EffectInstance
        {
            Id = "vol",
            Type = BuiltInEffects.Audio,
            Parameters =
            {
                [A.GainDb] = new Parameter { Keyframes = [new Keyframe(S(0), 3), new Keyframe(S(20), 3)] },
                [A.FadeIn] = new Parameter { Value = 2 },
            },
        });
        b.Sequence.AudioTracks[0].Effects.Add(new EffectInstance { Id = "pista", Type = BuiltInEffects.Audio, Parameters = { [A.GainDb] = new Parameter { Value = -10 } } });
        b.Sequence.AudioTracks[0].Muted = true;
        Func<string, IPcmSource> open = _ => new FunctionPcm(AudioSignals.Sine(1000, Math.Pow(10, -20 / 20.0)), 30 * Rate);

        LoudnessMeasurement before = OfflineAudio.MeasureClip(b.Project, b.Sequence.Id, clip.Id, @"C:\proyecto", open, CancellationToken.None);
        double delta = before.GainTo(-14, double.PositiveInfinity);
        var stack = new UndoStack(b.Project);
        stack.Execute(Edits.ShiftGain(b.Sequence.Id, new Dictionary<string, double> { [clip.Id] = delta }, "Normalizar a −14 LUFS"));
        LoudnessMeasurement after = OfflineAudio.MeasureClip(b.Project, b.Sequence.Id, clip.Id, @"C:\proyecto", open, CancellationToken.None);

        output.WriteLine($"−20 dBFS + 3 dB del clip: {before.IntegratedLufs:0.000} LUFS; +{delta:0.00} dB → {after.IntegratedLufs:0.000} LUFS");
        Assert.Equal(-17, before.IntegratedLufs, 0.1);
        Assert.Equal(-14, after.IntegratedLufs, 0.05);
        Assert.All(b.Sequence.AudioTracks[0].Clips[0].Effects[0].Parameters[A.GainDb].Keyframes, k => Assert.Equal(3 + delta, k.Value, 1e-9));
        Assert.Equal(-10, b.Sequence.AudioTracks[0].Effects[0].Parameters[A.GainDb].Value);
        Assert.True(stack.Undo());
        Assert.Equal(3, b.Sequence.AudioTracks[0].Clips[0].Effects[0].Parameters[A.GainDb].Keyframes[0].Value);
    }

    [Fact]
    public void Normalizing_the_mix_sets_the_master_gain_and_the_mix_ramps_to_it()
    {
        var b = new TimelineBuilder(0, 1);
        b.AddMedia("musica", Rational.Fps30, 900, video: false);
        b.AddAudio(0, "musica", Time.Zero, Time.Zero, S(20));
        Func<string, IPcmSource> open = _ => new FunctionPcm(AudioSignals.Sine(1000, Math.Pow(10, -26 / 20.0)), 30 * Rate);
        LoudnessMeasurement mix = Measure(b, open);
        var stack = new UndoStack(b.Project);
        double target = Math.Round(b.Sequence.MasterGainDb + mix.GainTo(-16), 2);
        stack.Execute(Edits.SetMasterGain(b.Sequence.Id, target, "Normalizar la mezcla a −16 LUFS"));
        LoudnessMeasurement normalized = Measure(b, open);

        using var mixer = new MixRenderer(b.Plan().Audio, open, TimeSpan.FromSeconds(5), TimeProvider.System);
        var block = new float[2 * 4800];
        mixer.Read(block);
        Assert.True(stack.Undo());
        mixer.SetPlan(b.Plan().Audio);
        var ramp = new float[2 * 4800];
        mixer.Read(ramp);
        double worst = 0;
        for (int i = 2; i < ramp.Length; i += 2)
        {
            worst = Math.Max(worst, Math.Abs(ramp[i] - ramp[i - 2]));
        }

        double sineStep = 2 * Math.PI * 1000 / Rate * Math.Pow(10, (-26 + target) / 20);
        output.WriteLine($"mezcla {mix.IntegratedLufs:0.000} LUFS → maestra {target:+0.00;−0.00} dB → {normalized.IntegratedLufs:0.000} LUFS; " +
            $"salto mayor al quitarla {worst / sineStep:0.000}× el del seno");
        Assert.Equal(-16, normalized.IntegratedLufs, 0.05);
        Assert.Equal(0, b.Sequence.MasterGainDb);
        Assert.InRange(worst, 0, sineStep * 1.05);
        Assert.Equal(Math.Pow(10, -26 / 20.0), AudioSignals.Amplitude(AudioSignals.Left(ramp.AsSpan(2 * 2400)), 1000), 0.002);
    }

    private static LoudnessMeasurement Measure(TimelineBuilder b, Func<string, IPcmSource> open)
    {
        using var mixer = new MixRenderer(b.Plan().Audio, open, TimeSpan.FromSeconds(5), TimeProvider.System);
        var meter = new LoudnessMeter();
        var block = new float[2 * 4096];
        for (long done = 0; done < 20L * Rate; done += 4096)
        {
            mixer.Read(block);
            meter.Process(block);
        }

        return meter.Result();
    }

    /// <summary>Tramos de seno de 1 kHz estéreo a los niveles dados (dBFS), como los archivos de Tech 3341.</summary>
    private static float[] Steps(double[] levels, double[] seconds)
    {
        int total = (int)Math.Round(seconds.Sum() * Rate);
        float[] signal = new float[total * 2];
        int at = 0;
        for (int s = 0; s < levels.Length; s++)
        {
            double amplitude = Math.Pow(10, levels[s] / 20);
            int end = s == levels.Length - 1 ? total : at + (int)Math.Round(seconds[s] * Rate);
            for (int i = at; i < end; i++)
            {
                float v = (float)(amplitude * Math.Sin(2 * Math.PI * 1000 * i / Rate));
                signal[2 * i] = v;
                signal[(2 * i) + 1] = v;
            }

            at = end;
        }

        return signal;
    }

    /// <summary>
    /// El camino de los medidores de la app: el analizador publica bloques en el anillo mientras la mezcla avanza y cada
    /// 100 ms se toma la potencia de los últimos 400 ms para el integrador; al final, la momentánea y la corta.
    /// </summary>
    private static (double Integrated, double Momentary, double ShortTerm) Live(float[] signal)
    {
        var ring = new AudioLevelRing();
        var analyzer = new AudioLevelAnalyzer(ring);
        var integrator = new LoudnessIntegrator();
        int frames = signal.Length / 2;
        const int step = Rate / 10;
        for (int at = 0; at < frames; at += step)
        {
            int n = Math.Min(step, frames - at);
            analyzer.Process(signal.AsSpan(2 * at, 2 * n), at);
            long to = at + n;
            if (to >= 4 * step)
            {
                integrator.Add(ring.WeightedPower(to - (4 * step), to));
            }
        }

        return (integrator.IntegratedLufs, Loudness.ToLufs(ring.WeightedPower(frames - (4 * step), frames)),
            Loudness.ToLufs(ring.WeightedPower(frames - (3 * Rate), frames)));
    }
}
