using System.Globalization;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Rodaje.Engine.Timeline.Retime;
using static Rodaje.Engine.Tests.Support.AudioSignals;
using static Rodaje.Engine.Tests.Support.Seconds;
using A = Rodaje.Core.Model.BuiltInEffects.AudioParameters;

namespace Rodaje.Engine.Tests.Timeline;

/// <summary>
/// Bordes del audio del motor con semilla fija: clips con velocidad a razones de 44,1/48/96 kHz y raras (por bloques es lo
/// mismo que de una vez, sin muestra perdida ni repetida en el borde del clip), fundidos de longitud 0 o mayores que el
/// clip, y valores no finitos del proyecto, que no llegan a la ganancia.
/// </summary>
public sealed class AudioEdgeTests
{
    /// <summary>Velocidades que pasan un origen de una frecuencia a otra (44,1 ↔ 48 ↔ 96 kHz), una casi 1× y otras raras.</summary>
    public static TheoryData<int, int, bool, bool> Speeds
    {
        get
        {
            var data = new TheoryData<int, int, bool, bool>();
            foreach ((int num, int den) in new[] { (147, 160), (160, 147), (2, 1), (1, 2), (47_999, 48_000), (37, 29), (3, 1) })
            {
                data.Add(num, den, false, false);
                data.Add(num, den, true, false);
                data.Add(num, den, false, true);
                data.Add(num, den, true, true);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Speeds))]
    public void A_retimed_reader_gives_the_same_samples_in_blocks_of_any_size_as_in_one_read(int num, int den, bool preservePitch, bool reverse)
    {
        var random = new Random(21_710 + (num * 31) + den + (preservePitch ? 7 : 0) + (reverse ? 13 : 0));
        long from = random.NextInt64(0, 10 * Rate);
        const int Frames = 30_000;
        var speed = new ClipSpeed { Rate = new Rational(num, den), PreservePitch = preservePitch, Reverse = reverse };
        ClipTimeMap map = ClipTimeMap.Create(Time.Zero, S(300), S(reverse ? 500 : 1), speed);
        Func<long, float> signal = Syllables(5);

        float[] whole = Read(map, preservePitch, signal, from, [Frames]);
        float[] blocks = Read(map, preservePitch, signal, from, Sizes(random, Frames));

        Assert.Equal(whole, blocks);
        Assert.All(whole, v => Assert.True(float.IsFinite(v)));
    }

    [Theory]
    [InlineData(147, 160)]
    [InlineData(160, 147)]
    [InlineData(2, 1)]
    [InlineData(1, 2)]
    [InlineData(47_999, 48_000)]
    [InlineData(37, 29)]
    public void A_clip_without_keeping_pitch_starts_and_ends_on_its_samples_and_reads_its_source_without_drift(int num, int den)
    {
        // Un seno grave: el remuestreador lo deja intacto, así cada muestra del clip se compara con el origen en map(s).
        const double Hz = 220;
        var rate = new Rational(num, den);
        var b = new TimelineBuilder(0, 1);
        b.AddMedia("tono", Rational.Fps30, 30 * 600);
        Clip clip = b.AddAudio(0, "tono", Time.FromSamples(12_345, Rate), S(2), S(6));
        clip.Speed = new ClipSpeed { Rate = rate, PreservePitch = false };
        ClipTimeMap.Fit(clip.Start, clip.SourceIn, clip.SourceOut, clip.Speed).ApplyTo(clip);
        AudioClipPlan planned = b.Plan().Audio.Tracks[0].Clips.Single();
        long start = planned.StartSample;
        long end = planned.EndSample;

        float[] mix = Mix(b, Sine(Hz), 0, (int)end + 500, Sizes(new Random(21_711 + num + den), (int)end + 500));

        Assert.Equal(12_345, start);
        Assert.Equal(0f, mix[(start - 1) * 2]);
        Assert.Equal(0f, mix[end * 2]);
        double worst = 0;
        long worstAt = 0;
        foreach (long s in new[] { start, start + 1, end - 2, end - 1 }.Concat(Enumerable.Range(0, 400).Select(i => start + ((end - start) * i / 400))))
        {
            double source = (2.0 * Rate) + ((s - start) * rate.ToDouble());
            double ideal = 0.5 * Math.Sin(2 * Math.PI * Hz * source / Rate);
            double error = Math.Abs(mix[s * 2] - ideal);
            (worst, worstAt) = error > worst ? (error, s) : (worst, worstAt);
        }

        Assert.True(worst < 1e-3, Invariant($"A {num}/{den} la muestra {worstAt} del clip [{start}, {end}) se aparta {worst:E2} del origen."));
    }

    [Theory]
    [InlineData(147, 160, true)]
    [InlineData(160, 147, false)]
    [InlineData(37, 29, true)]
    [InlineData(1, 1, false)]
    public void The_mix_of_split_retimed_clips_with_a_crossfade_and_gain_is_the_same_in_blocks_of_any_size(int num, int den, bool preservePitch)
    {
        var b = new TimelineBuilder(0, 1);
        b.AddMedia("voz", Rational.Fps30, 30 * 600);
        b.AddMedia("otra", Rational.Fps30, 30 * 600);
        Clip first = b.AddAudio(0, "voz", Time.FromSamples(1_234, Rate), S(3), S(4), id: "a");
        first.Speed = new ClipSpeed { Rate = new Rational(num, den), PreservePitch = preservePitch };
        ClipTimeMap.Fit(first.Start, first.SourceIn, first.SourceOut, first.Speed).ApplyTo(first);
        (ClipTimeMap left, ClipTimeMap right) = first.TimeMap.SplitAt(first.Start + S(1.2345));
        Clip second = first.Clone();
        second.Id = "b";
        left.ApplyTo(first);
        right.ApplyTo(second);
        Clip third = b.AddAudio(0, "otra", second.End, S(10), S(2), id: "c");
        b.Sequence.AudioTracks[0].Clips.Add(second);
        b.Sequence.AudioTracks[0].SortClips();
        b.Sequence.AudioTracks[0].Transitions.Add(new TransitionInstance
        {
            Id = "t", Type = BuiltInTransitions.Crossfade, From = "b", To = "c", Duration = S(0.5),
        });
        second.Effects.Add(new EffectInstance
        {
            Id = "g",
            Type = BuiltInEffects.Audio,
            Parameters = { [A.GainDb] = new Parameter { Keyframes = [new Keyframe(S(4), -3, Interpolation.Elastic), new Keyframe(S(6), -20)] } },
        });
        third.Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Audio, (A.FadeOut, 100)));
        int frames = (int)third.End.ToSamples(Rate) + 1000;
        Func<long, float> signal = Syllables(9);

        float[] whole = Mix(b, signal, 0, frames, [frames]);
        float[] blocks = Mix(b, signal, 0, frames, Sizes(new Random(21_714 + num + den), frames));

        Assert.Equal(whole, blocks);
        Assert.All(whole, v => Assert.True(float.IsFinite(v)));
        Assert.Contains(whole, v => Math.Abs(v) > 0.05f);
    }

    [Fact]
    public void Fades_of_zero_length_leave_the_edges_of_the_clip_at_full_level()
    {
        var b = new TimelineBuilder(0, 1);
        b.AddMedia("dc", Rational.Fps30, 30 * 60);
        b.AddAudio(0, "dc", Time.FromSamples(1_001, Rate), S(1), S(1)).Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Audio, (A.FadeIn, 0), (A.FadeOut, 0), (A.GainDb, -6)));
        AudioClipPlan planned = b.Plan().Audio.Tracks[0].Clips.Single();

        float[] mix = Mix(b, _ => 0.5f, 0, 3 * Rate, [3 * Rate]);

        Assert.Null(planned.Gain is { HasFades: true } ? planned.Gain : null);
        double level = 0.5 * Math.Pow(10, -6 / 20.0);
        Assert.Equal(0f, mix[(planned.StartSample - 1) * 2]);
        Assert.Equal(level, mix[planned.StartSample * 2], 1e-4);
        Assert.Equal(level, mix[(planned.EndSample - 1) * 2], 1e-4);
        Assert.Equal(0f, mix[planned.EndSample * 2]);
    }

    [Theory]
    [InlineData(100.0, 0.0)]
    [InlineData(0.0, 100.0)]
    [InlineData(100.0, 100.0)]
    [InlineData(1e300, 1e300)]
    public void Fades_longer_than_the_clip_are_cut_to_the_clip_and_follow_their_curve(double fadeIn, double fadeOut)
    {
        var b = new TimelineBuilder(0, 1);
        b.AddMedia("dc", Rational.Fps30, 30 * 60);
        b.AddAudio(0, "dc", Time.FromSamples(777, Rate), S(1), S(1)).Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Audio, (A.FadeIn, fadeIn), (A.FadeOut, fadeOut)));
        AudioClipPlan planned = b.Plan().Audio.Tracks[0].Clips.Single();
        GainPlan gain = planned.Gain!;
        long start = planned.StartSample;
        long length = planned.EndSample - start;

        Assert.Equal(fadeIn > 0 ? length : 0, gain.FadeIn.Length);
        Assert.Equal(fadeOut > 0 ? length : 0, gain.FadeOut.Length);
        Assert.Equal(start, gain.FadeIn.Start);
        Assert.Equal(fadeOut > 0 ? start : planned.EndSample, gain.FadeOut.Start);

        int frames = (int)planned.EndSample + 100;
        float[] whole = Mix(b, _ => 0.5f, 0, frames, [frames]);
        float[] blocks = Mix(b, _ => 0.5f, 0, frames, Sizes(new Random(21_712), frames));

        Assert.Equal(whole, blocks);
        for (long s = start - 50; s < planned.EndSample + 50; s++)
        {
            double t = (double)(s - start) / length;
            double expected = s < start || s >= planned.EndSample ? 0
                : 0.5 * (fadeIn > 0 ? Math.Sin(t * Math.PI / 2) : 1) * (fadeOut > 0 ? Math.Cos(t * Math.PI / 2) : 1);
            Assert.True(Math.Abs(whole[s * 2] - expected) < 1e-4, Invariant($"Muestra {s}: {whole[s * 2]} en vez de {expected}."));
        }
    }

    [Fact]
    public void A_fade_factor_stays_between_0_and_1_and_reaches_its_ends_for_any_length()
    {
        var random = new Random(21_713);
        for (int trial = 0; trial < 2000; trial++)
        {
            long start = random.NextInt64(-1_000_000, 1_000_000);
            long length = random.Next(5) switch
            {
                0 => 0,
                1 => -random.NextInt64(1, 1000),
                2 => 1,
                3 => random.NextInt64(2, 100),
                _ => random.NextInt64(100, 100_000_000),
            };
            var shape = (FadeShape)random.Next(3);
            var fadeIn = new Fade(start, length, Out: false) { Shape = shape };
            var fadeOut = new Fade(start, length, Out: true) { Shape = shape };
            if (length <= 0)
            {
                Assert.True(fadeIn.IsNone && fadeOut.IsNone);
                Assert.Equal(1, fadeIn.Factor(start));
                Assert.Equal(1, fadeOut.Factor(start + 1));
                continue;
            }

            Assert.Equal(0, fadeIn.Factor(start), 12);
            Assert.Equal(1, fadeIn.Factor(start + length), 12);
            Assert.Equal(1, fadeOut.Factor(start), 12);
            Assert.Equal(0, fadeOut.Factor(start + length), 12);
            Assert.Equal(0, fadeIn.Factor(long.MinValue / 2), 12);
            Assert.Equal(1, fadeIn.Factor(long.MaxValue / 2), 12);
            double previous = 0;
            for (int k = 0; k <= 16; k++)
            {
                long s = start + (length * k / 16);
                double f = fadeIn.Factor(s);
                Assert.InRange(f, previous, 1);
                Assert.InRange(fadeOut.Factor(s), 0, 1);
                previous = f;
            }
        }
    }

    [Fact]
    public void Non_finite_gain_pan_and_fades_in_the_project_never_reach_the_linear_gain()
    {
        var b = new TimelineBuilder(0, 1);
        b.AddMedia("dc", Rational.Fps30, 30 * 60);
        Clip clip = b.AddAudio(0, "dc", Time.Zero, S(1), S(2));
        var audio = new EffectInstance { Id = "a", Type = BuiltInEffects.Audio };
        audio.Parameters[A.GainDb] = new Parameter
        {
            Value = double.NaN,
            Keyframes =
            [
                new Keyframe(S(1), double.NaN, Interpolation.Elastic),
                new Keyframe(S(1.5), double.PositiveInfinity, Interpolation.Bezier) { Ease = new BezierEase(double.NaN, double.PositiveInfinity, 2, -9) },
                new Keyframe(S(2), double.NegativeInfinity, Interpolation.Bounce),
                new Keyframe(S(2.5), 1e300, Interpolation.EaseInOut),
                new Keyframe(S(3), -1e300),
            ],
        };
        audio.Parameters[A.Pan] = new Parameter { Keyframes = [new Keyframe(S(1), double.NaN), new Keyframe(S(3), double.PositiveInfinity)] };
        audio.Parameters[A.Balance] = new Parameter { Value = double.NegativeInfinity };
        audio.Parameters[A.FadeIn] = new Parameter { Value = double.NaN };
        audio.Parameters[A.FadeOut] = new Parameter { Value = double.PositiveInfinity };
        clip.Effects.Add(audio);
        var track = new EffectInstance { Id = "p", Type = BuiltInEffects.Audio };
        track.Parameters[A.GainDb] = new Parameter { Keyframes = [new Keyframe(Time.Zero, double.NaN, Interpolation.Elastic), new Keyframe(S(2), double.PositiveInfinity)] };
        b.Sequence.AudioTracks[0].Effects.Add(track);

        AudioPlan plan = b.Plan().Audio;
        AudioClipPlan planned = plan.Tracks[0].Clips.Single();
        GainPlan gain = planned.Gain!;
        FaderPlan fader = plan.Tracks[0].Fader!;
        for (long s = planned.StartSample - 10; s < planned.EndSample + 10; s += 7)
        {
            double db = gain.Db(s, planned.SourceSampleAt(s)) + fader.GainDb(s);
            Assert.True(double.IsFinite(db), Invariant($"Ganancia no finita ({db}) en la muestra {s}."));
            Assert.True(double.IsFinite(GainPlan.ToLinear(db)));
            Assert.True(double.IsFinite(gain.Fades(s)));
            PanMatrixIsFinite(gain.PanAt(planned.SourceSampleAt(s)));
        }

        float[] mix = Mix(b, _ => 0.5f, 0, 3 * Rate, [3 * Rate]);
        Assert.All(mix, v => Assert.True(float.IsFinite(v)));
    }

    private static void PanMatrixIsFinite(Rodaje.Audio.Processing.PanMatrix m) =>
        Assert.True(float.IsFinite(m.LeftToLeft) && float.IsFinite(m.LeftToRight) && float.IsFinite(m.RightToLeft) && float.IsFinite(m.RightToRight));

    private static float[] Read(ClipTimeMap map, bool preservePitch, Func<long, float> signal, long from, IReadOnlyList<int> sizes)
    {
        var reader = new RetimedReader(new AudioRetime(map, preservePitch), new FunctionPcm(signal, long.MaxValue / 4), -1);
        var all = new float[sizes.Sum() * 2];
        int done = 0;
        foreach (int n in sizes)
        {
            reader.Read(from + done, all.AsSpan(done * 2, n * 2), reader.DecoderNext);
            done += n;
        }

        return all;
    }

    private static float[] Mix(TimelineBuilder b, Func<long, float> signal, long from, int frames, IReadOnlyList<int> sizes)
    {
        using var mixer = new MixRenderer(b.Plan().Audio, _ => new FunctionPcm(signal, 600L * Rate), TimeSpan.FromSeconds(5), TimeProvider.System);
        mixer.Seek(Time.FromSamples(from, Rate));
        var all = new float[frames * 2];
        int done = 0;
        foreach (int n in sizes)
        {
            Assert.Equal(n, mixer.Read(all.AsSpan(done * 2, n * 2)));
            done += n;
        }

        Assert.Equal(frames, done);
        return all;
    }

    /// <summary>Tamaños de bloque con semilla: de una muestra, de unas pocas, de cientos y de miles.</summary>
    private static List<int> Sizes(Random random, int total)
    {
        var sizes = new List<int>();
        for (int left = total; left > 0;)
        {
            int n = Math.Min(left, random.Next(4) switch { 0 => 1, 1 => random.Next(1, 8), 2 => random.Next(1, 300), _ => random.Next(300, 6000) });
            sizes.Add(n);
            left -= n;
        }

        return sizes;
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
