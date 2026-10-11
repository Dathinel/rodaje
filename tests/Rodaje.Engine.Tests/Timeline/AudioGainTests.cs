using System.Diagnostics;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Xunit.Abstractions;
using static Rodaje.Engine.Tests.Support.Seconds;
using A = Rodaje.Core.Model.BuiltInEffects.AudioParameters;

namespace Rodaje.Engine.Tests.Timeline;

public sealed class AudioGainTests(ITestOutputHelper output)
{
    private const int Rate = 48_000;

    [Fact]
    public void Minus_six_db_is_half_the_amplitude()
    {
        var b = Builder();
        Clip clip = b.AddAudio(0, "dc", Time.Zero, Time.Zero, S(2));
        clip.Effects.Add(Audio((A.GainDb, -6)));

        float[] mix = Mix(b, 0, 9600);

        Assert.All(mix, s => Assert.Equal(0.5 * 0.501, s, 0.0005));
    }

    [Fact]
    public void Sixty_db_down_or_less_is_silence()
    {
        var b = Builder();
        b.AddAudio(0, "dc", Time.Zero, Time.Zero, S(2)).Effects.Add(Audio((A.GainDb, -60)));

        Assert.All(Mix(b, 0, 4800), s => Assert.Equal(0f, s));
    }

    [Fact]
    public void Fades_follow_constant_power_curves()
    {
        var b = Builder();
        b.AddAudio(0, "dc", S(1), Time.Zero, S(3)).Effects.Add(Audio((A.FadeIn, 0.5), (A.FadeOut, 1)));

        float[] mix = Mix(b, 0, 4 * Rate);

        double worst = 0;
        for (long s = 0; s < 4 * Rate; s++)
        {
            double expected = s < Rate ? 0
                : s < 1.5 * Rate ? Math.Sin((s - Rate) / (0.5 * Rate) * Math.PI / 2)
                : s < 3 * Rate ? 1
                : Math.Cos((s - (3 * Rate)) / (double)Rate * Math.PI / 2);
            worst = Math.Max(worst, Math.Abs((mix[s * 2] / 0.5) - expected));
        }

        output.WriteLine($"MEDIDA error_fundido = {worst:E2}");
        Assert.True(worst <= 1e-4, $"El fundido se aparta {worst:E2} de la curva.");
    }

    [Fact]
    public void Clip_gain_keyframes_are_in_source_time_and_track_gain_in_sequence_time()
    {
        var b = Builder();
        Clip clip = b.AddAudio(0, "dc", S(10), S(5), S(2));
        clip.Effects.Add(new EffectInstance
        {
            Type = BuiltInEffects.Audio,
            Parameters = { [A.GainDb] = new Parameter { Keyframes = [new Keyframe(S(5), 0), new Keyframe(S(6), -20)] } },
        });
        b.Sequence.AudioTracks[0].Effects.Add(new EffectInstance
        {
            Type = BuiltInEffects.Audio,
            Parameters = { [A.GainDb] = new Parameter { Keyframes = [new Keyframe(S(10), -6), new Keyframe(S(12), 0)] } },
        });

        float[] mix = Mix(b, 10 * Rate, 2 * Rate);

        Assert.Equal(0.5 * Math.Pow(10, -6.0 / 20), mix[0], 1e-4);
        Assert.Equal(0.5 * Math.Pow(10, (-20 - 3) / 20.0), mix[2 * Rate], 1e-4);
        Assert.Equal(0.5 * Math.Pow(10, (-20 - 1.5) / 20.0), mix[2 * 3 * Rate / 2], 1e-4);
    }

    [Fact]
    public void An_audio_transition_plays_both_clips_with_complementary_fades()
    {
        var b = Builder();
        b.AddMedia("dc2", Rational.Fps30, 3000);
        b.AddAudio(0, "dc", Time.Zero, S(5), S(2), "sale");
        b.AddAudio(0, "dc2", S(2), S(5), S(2), "entra");
        b.Sequence.AudioTracks[0].Transitions.Add(new TransitionInstance
        {
            Id = "t", Type = BuiltInTransitions.Crossfade, From = "sale", To = "entra", Duration = S(1),
        });
        AudioPlan plan = b.Plan().Audio;
        Assert.Equal(new[] { (0L, 2L * Rate + (Rate / 2)), ((3L * Rate) / 2, 4L * Rate) },
            plan.Tracks[0].Clips.Select(c => (c.StartSample, c.EndSample)));

        float[] mix = Mix(b, 0, 4 * Rate, path => path.Contains("dc2") ? 0.25f : 0.5f);

        for (long s = (3 * Rate / 2) + 7; s < 5 * Rate / 2; s += 97)
        {
            double t = (s - (1.5 * Rate)) / Rate;
            double expected = (0.5 * Math.Cos(t * Math.PI / 2)) + (0.25 * Math.Sin(t * Math.PI / 2));
            Assert.Equal(expected, mix[s * 2], 1e-4);
        }

        Assert.Equal(0.25, mix[3 * Rate * 2], 1e-6);
    }

    [Fact]
    public void A_crossfade_of_pink_noise_keeps_the_power()
    {
        var b = Builder();
        b.AddMedia("ruido1", Rational.Fps30, 3000);
        b.AddMedia("ruido2", Rational.Fps30, 3000);
        b.AddAudio(0, "ruido1", Time.Zero, S(10), S(4), "sale");
        b.AddAudio(0, "ruido2", S(4), S(10), S(4), "entra");
        b.Sequence.AudioTracks[0].Transitions.Add(new TransitionInstance
        {
            Id = "t", Type = BuiltInTransitions.Crossfade, From = "sale", To = "entra", Duration = S(4),
        });
        var noise = new Dictionary<string, float[]>
        {
            ["ruido1"] = PinkNoise(1, 20 * Rate),
            ["ruido2"] = PinkNoise(2, 20 * Rate),
        };

        float[] mix = Mix(b, 0, 8 * Rate, sources: path => noise[Path.GetFileNameWithoutExtension(path)]);

        double reference = Power(noise["ruido1"], 10 * Rate, 4 * Rate);
        double worst = 0;
        const int Window = Rate / 2;
        for (long s = 2 * Rate; s + Window <= 6 * Rate; s += Window)
        {
            double expected = CrossedPower(noise["ruido1"], noise["ruido2"], s, Window);
            double db = 10 * Math.Log10(PowerOfMix(mix, s, Window) / expected);
            worst = Math.Max(worst, Math.Abs(db));
        }

        output.WriteLine($"MEDIDA cruce_potencia_max_db = {worst:F3}");
        Assert.True(reference > 0);
        Assert.True(worst <= 0.5, $"La potencia del cruce se aparta {worst:F2} dB.");
    }

    [Fact]
    public void A_volume_change_ramps_without_jumps()
    {
        var b = Builder();
        Clip clip = b.AddAudio(0, "dc", Time.Zero, Time.Zero, S(4));
        clip.Effects.Add(Audio((A.GainDb, 0)));
        var backend = Backend(b, _ => 0.5f);
        using var mixer = new MixRenderer(b.Plan().Audio, backend, TimeSpan.FromSeconds(5), TimeProvider.System);
        var first = new float[2 * 4800];
        mixer.Read(first);

        clip.Effects[0].Parameters[A.GainDb].Value = -40;
        mixer.SetPlan(b.Plan().Audio);
        mixer.Seek(Time.FromSamples(2400, Rate));
        var after = new float[2 * 4800];
        mixer.Read(after);

        double worst = 0;
        double previous = 20 * Math.Log10(first[(2 * 2400) - 2] / 0.5);
        for (int i = 0; i < 4800; i++)
        {
            double db = 20 * Math.Log10(after[2 * i] / 0.5);
            worst = Math.Max(worst, Math.Abs(db - previous));
            previous = db;
        }

        output.WriteLine($"MEDIDA salto_max_db = {worst:F4}");
        Assert.True(worst <= 0.1, $"Salto de {worst:F3} dB entre muestras.");
        Assert.Equal(0.5 * 0.01, after[2 * 4799], 1e-5);
    }

    [Fact]
    public void A_volume_change_is_heard_within_60_ms()
    {
        var b = Builder();
        Clip clip = b.AddAudio(0, "dc", Time.Zero, Time.Zero, S(20));
        clip.Effects.Add(Audio((A.GainDb, 0)));
        using var mixer = new AudioMixer(b.Plan().Audio, Backend(b, _ => 0.5f), TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5),
            TimeProvider.System);
        WaitFor(() => mixer.BufferedFrames >= 20_000);
        mixer.Read(new float[2 * 4800]);

        clip.Effects[0].Parameters[A.GainDb].Value = -12;
        mixer.Reload(b.Plan().Audio);
        WaitFor(() => mixer.BufferedFrames >= 10_000);
        var next = new float[2 * 6000];
        mixer.Read(next);

        int first = Enumerable.Range(0, 6000).First(i => next[2 * i] < 0.5f * 0.999f);
        double ms = first * 1000.0 / Rate;
        output.WriteLine($"MEDIDA cambio_volumen_ms = {ms:F1}");
        Assert.True(ms <= 60, $"El cambio se oyó a los {ms:F1} ms.");
        Assert.Equal(0.5 * Math.Pow(10, -12 / 20.0), next[2 * 5999], 1e-4);
    }

    private static TimelineBuilder Builder()
    {
        var b = new TimelineBuilder(0, 1);
        b.AddMedia("dc", Rational.Fps30, 30_000);
        return b;
    }

    private static EffectInstance Audio(params (string Name, double Value)[] parameters) =>
        TimelineEffectsTests.Effect(BuiltInEffects.Audio, parameters);

    private static Func<string, IPcmSource> Backend(TimelineBuilder b, Func<string, float>? level = null,
        Func<string, float[]>? sources = null) =>
        path =>
        {
            long length = b.Project.Media.Single(m => m.Path == path).Duration.ToSamples(Rate);
            if (sources is not null)
            {
                float[] data = sources(path);
                return new SignalPcm(i => i < data.Length ? data[i] : 0, length);
            }

            float value = level?.Invoke(path) ?? 0.5f;
            return new SignalPcm(_ => value, length);
        };

    private static float[] Mix(TimelineBuilder b, long from, int frames, Func<string, float>? level = null, Func<string, float[]>? sources = null)
    {
        using var mixer = new MixRenderer(b.Plan().Audio, Backend(b, level, sources), TimeSpan.FromSeconds(5), TimeProvider.System);
        mixer.Seek(Time.FromSamples(from, Rate));
        var all = new float[frames * 2];
        for (int done = 0; done < frames;)
        {
            int n = Math.Min(1024, frames - done);
            Assert.Equal(n, mixer.Read(all.AsSpan(done * 2, n * 2)));
            done += n;
        }

        return all;
    }

    private static double Power(float[] data, long from, int count)
    {
        double sum = 0;
        for (long i = from; i < from + count; i++)
        {
            sum += data[i] * (double)data[i];
        }

        return sum / count;
    }

    /// <summary>Potencia que debe tener el cruce si los dos ruidos no se correlacionan: cos² de uno más sin² del otro.</summary>
    private static double CrossedPower(float[] outgoing, float[] incoming, long from, int count)
    {
        double sum = 0;
        for (long s = from; s < from + count; s++)
        {
            double t = (s - (2.0 * Rate)) / (4.0 * Rate);
            double a = outgoing[(10 * Rate) + s];
            double b = incoming[(6 * Rate) + s];
            double c = Math.Cos(t * Math.PI / 2);
            sum += (c * c * a * a) + ((1 - (c * c)) * b * b);
        }

        return sum / count;
    }

    private static double PowerOfMix(float[] mix, long from, int count)
    {
        double sum = 0;
        for (long i = from; i < from + count; i++)
        {
            sum += mix[i * 2] * (double)mix[i * 2];
        }

        return sum / count;
    }

    /// <summary>Ruido rosa por el método de Voss-McCartney simplificado de Paul Kellet, con semilla fija.</summary>
    private static float[] PinkNoise(int seed, int count)
    {
        var random = new Random(seed);
        var data = new float[count];
        double b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0;
        for (int i = 0; i < count; i++)
        {
            double white = (random.NextDouble() * 2) - 1;
            b0 = (0.99886 * b0) + (white * 0.0555179);
            b1 = (0.99332 * b1) + (white * 0.0750759);
            b2 = (0.96900 * b2) + (white * 0.1538520);
            b3 = (0.86650 * b3) + (white * 0.3104856);
            b4 = (0.55000 * b4) + (white * 0.5329522);
            b5 = (-0.7616 * b5) - (white * 0.0168980);
            data[i] = (float)((b0 + b1 + b2 + b3 + b4 + b5 + b6 + (white * 0.5362)) * 0.05);
            b6 = white * 0.115926;
        }

        return data;
    }

    private static void WaitFor(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "La mezcla no llegó a tiempo.");
            Thread.Sleep(1);
        }
    }

    /// <summary>Señal estéreo con la misma muestra en los dos canales, dada por su índice.</summary>
    private sealed class SignalPcm(Func<long, float> sample, long length) : IPcmSource
    {
        private long _position;

        public int Read(Span<float> interleaved)
        {
            int frames = (int)Math.Min(interleaved.Length / 2, Math.Max(0, length - _position));
            for (int i = 0; i < frames; i++)
            {
                float v = sample(_position + i);
                interleaved[i * 2] = v;
                interleaved[(i * 2) + 1] = v;
            }

            _position += frames;
            return frames;
        }

        public void Seek(Time position) => _position = position.ToSamples(Rate);

        public void Dispose()
        {
        }
    }
}
