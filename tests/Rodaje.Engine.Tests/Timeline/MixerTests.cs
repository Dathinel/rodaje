using System.Buffers.Binary;
using Rodaje.Audio.Metering;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Offline;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Rodaje.Media.Export;
using Xunit;
using Xunit.Abstractions;
using static Rodaje.Engine.Tests.Support.Seconds;
using A = Rodaje.Core.Model.BuiltInEffects.AudioParameters;

namespace Rodaje.Engine.Tests.Timeline;

/// <summary>
/// El mezclador: ley de paneo, buses y envíos (suma exacta), compensación del retraso de los buses, solo, automatización
/// del fader, vista previa igual a la exportación con todos los efectos nuevos y el coste por bloque.
/// </summary>
public sealed class MixerTests(ITestOutputHelper output) : IDisposable
{
    private const int Rate = 48_000;
    private readonly string _folder = Directory.CreateTempSubdirectory("rodaje-mezclador-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Track_pan_follows_the_minus_3_db_law_and_balance_lowers_one_side()
    {
        var b = new TimelineBuilder(0, 1);
        b.AddMedia("uno", Rational.Fps30, 900, video: false);
        b.AddAudio(0, "uno", Time.Zero, Time.Zero, S(1));
        EffectInstance fader = Fader(b.Sequence.AudioTracks[0]);
        fader.Parameters[A.Pan] = new Parameter { Value = -50 };
        float[] panned = Mix(b, Rate / 10, _ => _ => 0.25f);
        double theta = 0.5 * Math.PI / 4;
        output.WriteLine($"Paneo −50: L {panned[2000]:0.000000} (√2·cos(π/8)·0,25 = {Math.Sqrt(2) * Math.Cos(theta) * 0.25:0.000000}), R {panned[2001]:0.000000} ({Math.Sqrt(2) * Math.Sin(theta) * 0.25:0.000000})");
        Assert.Equal(Math.Sqrt(2) * Math.Cos(theta) * 0.25, panned[2000], 6);
        Assert.Equal(Math.Sqrt(2) * Math.Sin(theta) * 0.25, panned[2001], 6);

        fader.Parameters[A.Pan] = new Parameter { Value = 0 };
        fader.Parameters[A.Balance] = new Parameter { Value = 50 };
        float[] balanced = Mix(b, Rate / 10, _ => _ => 0.25f);
        Assert.Equal(0.125, balanced[2000], 6);
        Assert.Equal(0.25, balanced[2001], 6);
    }

    [Fact]
    public void Buses_and_sends_sum_exactly()
    {
        TimelineBuilder b = ThreeTracks(out Func<string, Func<long, float>> sources);
        Sequence sequence = b.Sequence;
        sequence.AudioBuses.Add(new AudioBus { Id = "voces", Name = "Voces", Effects = [FaderEffect("fv", -2, 0)] });
        sequence.AudioBuses.Add(new AudioBus { Id = "efectos", Name = "Efectos", Effects = [FaderEffect("fe", 1, 0)] });
        Track a0 = sequence.AudioTracks[0], a1 = sequence.AudioTracks[1], a2 = sequence.AudioTracks[2];
        a0.Output = "voces";
        a0.Effects.Add(FaderEffect("f0", -6, 0));
        a1.Effects.Add(FaderEffect("f1", -3, 0));
        a1.Sends = [new AudioSend { BusId = "efectos", GainDb = -12, PreFader = true }, new AudioSend { BusId = "voces", GainDb = -20 }];
        a2.Output = "voces";
        a2.Sends = [new AudioSend { BusId = "efectos", GainDb = -6, Muted = true }];

        float[] mix = Mix(b, Rate, sources);
        Func<long, float> s0 = sources("s0"), s1 = sources("s1"), s2 = sources("s2");
        static double G(double db) => Math.Pow(10, db / 20);
        double worst = 0;
        for (long i = 0; i < Rate; i++)
        {
            double voices = G(-2) * ((G(-6) * s0(i)) + s2(i) + (G(-20) * G(-3) * s1(i)));
            double effects = G(1) * G(-12) * s1(i);
            double expected = voices + (G(-3) * s1(i)) + effects;
            worst = Math.Max(worst, Math.Abs(mix[2 * i] - expected));
        }

        output.WriteLine($"Tres pistas, dos buses, envíos pre y post fader y uno silenciado: error máximo frente a la suma teórica {worst:E2}");
        Assert.True(worst < 2e-6, $"{worst}");
    }

    [Fact]
    public void Bus_latency_is_compensated_so_every_path_stays_on_the_sample()
    {
        var b = new TimelineBuilder(0, 2);
        b.AddMedia("clicA", Rational.Fps30, 900, video: false);
        b.AddMedia("clicB", Rational.Fps30, 900, video: false);
        b.AddAudio(0, "clicA", Time.Zero, Time.Zero, S(2));
        b.AddAudio(1, "clicB", Time.Zero, Time.Zero, S(2));
        Sequence sequence = b.Sequence;
        sequence.AudioBuses.Add(new AudioBus
        {
            Id = "lim",
            Name = "Limitada",
            Effects = [Effect("l", BuiltInEffects.Limiter, new() { [BuiltInEffects.LimiterParameters.Ceiling] = 0 })],
        });
        sequence.AudioBuses.Add(new AudioBus
        {
            Id = "tono",
            Name = "Tono",
            Effects = [Effect("p", BuiltInEffects.PitchShift, new() { [BuiltInEffects.PitchShiftParameters.Semitones] = 0 })],
        });
        sequence.AudioTracks[0].Output = "lim";
        sequence.AudioTracks[1].Sends = [new AudioSend { BusId = "tono", GainDb = -6 }];

        float[] mix = Mix(b, 2 * Rate, path => path.Contains("clicA", StringComparison.Ordinal)
            ? i => i == 24_000 ? 0.5f : 0f
            : i => i == 72_000 ? 0.5f : 0f);
        int[] clicks = [.. Enumerable.Range(0, mix.Length / 2).Where(i => Math.Abs(mix[2 * i]) > 1e-3f)];
        output.WriteLine($"Clic por un bus con limitador (56 muestras de retraso) y otro directo más su envío a un bus con tono (1824): {string.Join(", ", clicks)} con {mix[48_000]} y {mix[144_000]}");
        Assert.Equal([24_000, 72_000], clicks);
        Assert.Equal(0.5f, mix[2 * 24_000], 5);
        Assert.Equal(0.5 + (0.5 * Math.Pow(10, -6 / 20.0)), mix[2 * 72_000], 5);
    }

    [Fact]
    public void Solo_plays_only_what_is_soloed_and_what_feeds_a_soloed_bus()
    {
        TimelineBuilder b = ThreeTracks(out Func<string, Func<long, float>> sources);
        Sequence sequence = b.Sequence;
        sequence.AudioBuses.Add(new AudioBus { Id = "voces", Name = "Voces" });
        sequence.AudioBuses.Add(new AudioBus { Id = "efectos", Name = "Efectos" });
        sequence.AudioTracks[0].Output = "voces";
        sequence.AudioTracks[2].Sends = [new AudioSend { BusId = "efectos", GainDb = -6 }];
        Func<long, float> s0 = sources("s0"), s1 = sources("s1"), s2 = sources("s2");
        double sendGain = Math.Pow(10, -6 / 20.0);

        (string Case, Action Set, Func<long, double> Expected)[] cases =
        [
            ("pista 2 sola", () => sequence.AudioTracks[1].Solo = true, i => s1(i)),
            ("bus Voces solo", () => sequence.AudioBuses[0].Solo = true, i => s0(i)),
            ("pista 3 sola (y su envío)", () => sequence.AudioTracks[2].Solo = true, i => s2(i) * (1 + sendGain)),
        ];
        foreach ((string name, Action set, Func<long, double> expected) in cases)
        {
            sequence.AudioTracks.ForEach(t => t.Solo = false);
            sequence.AudioBuses.ForEach(bus => bus.Solo = false);
            set();
            float[] mix = Mix(b, Rate / 4, sources);
            double worst = Enumerable.Range(0, Rate / 4).Max(i => Math.Abs(mix[2 * i] - expected(i)));
            output.WriteLine($"{name}: error {worst:E2}");
            Assert.True(worst < 1e-6, name);
        }
    }

    [Fact]
    public void Fader_automation_follows_the_keyframes_in_volume_and_pan()
    {
        var b = new TimelineBuilder(0, 1);
        b.AddMedia("uno", Rational.Fps30, 900, video: false);
        b.AddAudio(0, "uno", Time.Zero, Time.Zero, S(2));
        EffectInstance fader = Fader(b.Sequence.AudioTracks[0]);
        fader.Parameters[A.GainDb] = new Parameter { Keyframes = [new Keyframe(S(0), -40, Interpolation.Linear), new Keyframe(S(1), 0, Interpolation.Linear)] };
        fader.Parameters[A.Pan] = new Parameter { Keyframes = [new Keyframe(S(0), -100, Interpolation.Linear), new Keyframe(S(1), 100, Interpolation.Linear)] };
        float[] mix = Mix(b, 2 * Rate, _ => _ => 0.25f);
        double worst = 0;
        foreach (double t in new[] { 0.1, 0.25, 0.5, 0.75, 0.9, 1.5 })
        {
            long at = (long)(t * Rate);
            double clamped = Math.Min(1, t);
            double gain = Math.Pow(10, -40 * (1 - clamped) / 20);
            double theta = ((-1 + (2 * clamped)) + 1) * Math.PI / 4;

            // Una fuente mono: cada lado sale con la ley de −3 dB del paneo de ese instante.
            double left = gain * 0.25 * Math.Sqrt(2) * Math.Cos(theta);
            double right = gain * 0.25 * Math.Sqrt(2) * Math.Sin(theta);
            worst = Math.Max(worst, Math.Max(Math.Abs(mix[2 * at] - left), Math.Abs(mix[(2 * at) + 1] - right)));
        }

        output.WriteLine($"Volumen −40 → 0 dB y paneo izquierda → derecha en 1 s: error máximo en seis instantes {worst:E2}");
        Assert.True(worst < 2e-4, $"{worst}");
    }

    [Fact]
    public void Strip_meters_measure_each_track_and_bus_after_its_fader()
    {
        TimelineBuilder b = ThreeTracks(out Func<string, Func<long, float>> sources);
        b.Sequence.AudioBuses.Add(new AudioBus { Id = "voces", Name = "Voces", Effects = [FaderEffect("fv", -6, 0)] });
        b.Sequence.AudioTracks[0].Output = "voces";
        using var mixer = new MixRenderer(b.Plan().Audio, path => new FunctionPcm(sources(path), 30L * Rate), TimeSpan.FromSeconds(5),
            TimeProvider.System, stripMeters: true);
        Read(mixer, Rate);
        AudioLevels track = mixer.StripLevels[MixRenderer.TrackKey("a0")].Measure(Rate / 2, Rate, Rate / 2);
        AudioLevels bus = mixer.StripLevels[MixRenderer.BusKey("voces")].Measure(Rate / 2, Rate, Rate / 2);
        output.WriteLine($"Pista 1 (seno 0,1): pico {track.Left.Peak:0.0000}, RMS {track.Left.Rms:0.0000}; bus a −6 dB: pico {bus.Left.Peak:0.0000}");
        Assert.Equal(0.1, track.Left.Peak, 3);
        Assert.Equal(0.1 / Math.Sqrt(2), track.Left.Rms, 3);
        Assert.Equal(0.1 * Math.Pow(10, -6 / 20.0), bus.Left.Peak, 3);
    }

    [Fact]
    public void Preview_and_export_give_the_same_samples_with_buses_sends_and_every_new_effect()
    {
        TimelineBuilder b = ThreeTracks(out Func<string, Func<long, float>> sources);
        Sequence sequence = b.Sequence;
        sequence.AudioBuses.Add(new AudioBus
        {
            Id = "voces",
            Name = "Voces",
            Effects =
            [
                Effect("dl", BuiltInEffects.Delay, new() { [BuiltInEffects.DelayParameters.Time] = 125, [BuiltInEffects.DelayParameters.Feedback] = 45, [BuiltInEffects.DelayParameters.PingPong] = 1 }),
                Effect("lm", BuiltInEffects.Limiter, new() { [BuiltInEffects.LimiterParameters.InputGain] = 6 }),
                FaderEffect("fv", -1, -20),
            ],
        });
        sequence.AudioBuses.Add(new AudioBus
        {
            Id = "efectos",
            Name = "Efectos",
            Effects = [Effect("rv", BuiltInEffects.Reverb, new() { [BuiltInEffects.ReverbParameters.Mix] = 100, [BuiltInEffects.ReverbParameters.Decay] = 1.2 })],
        });
        Track a0 = sequence.AudioTracks[0], a1 = sequence.AudioTracks[1], a2 = sequence.AudioTracks[2];
        a0.Output = "voces";
        a0.Effects.Add(Effect("g", BuiltInEffects.Gate, new() { [BuiltInEffects.GateParameters.Threshold] = -30 }));
        a0.Effects.Add(Effect("ds", BuiltInEffects.DeEsser, new() { [BuiltInEffects.DeEsserParameters.Threshold] = -40 }));
        a1.Effects.Add(Effect("st", BuiltInEffects.Saturation, new() { [BuiltInEffects.SaturationParameters.Drive] = 12 }));
        a1.Effects.Add(Effect("ge", BuiltInEffects.GraphicEqualizer, new() { ["band1k"] = 4, ["band8k"] = -3 }));
        a1.Sends = [new AudioSend { BusId = "efectos", GainDb = -8, PreFader = true }];
        a2.Effects.Add(Effect("ps", BuiltInEffects.PitchShift, new() { [BuiltInEffects.PitchShiftParameters.Semitones] = 4 }));
        a2.Sends = [new AudioSend { BusId = "efectos", GainDb = -4 }];
        EffectInstance fader = Fader(a2);
        fader.Parameters[A.Pan] = new Parameter { Keyframes = [new Keyframe(S(0.2), -80, Interpolation.Linear), new Keyframe(S(2.2), 60, Interpolation.EaseInOut)] };
        fader.Parameters[A.GainDb] = new Parameter { Keyframes = [new Keyframe(S(0.5), 0, Interpolation.Linear), new Keyframe(S(1.5), -12, Interpolation.Linear)] };
        sequence.MasterEffects.Add(Effect("mc", BuiltInEffects.Compressor, new()
        {
            [BuiltInEffects.CompressorParameters.Threshold] = -18, [BuiltInEffects.CompressorParameters.Ratio] = 3,
            [BuiltInEffects.CompressorParameters.Limiter] = 1, [BuiltInEffects.CompressorParameters.Lookahead] = 2,
        }));

        Time length = S(3);
        IPcmSource Open(string path) => new FunctionPcm(sources(path), 30L * Rate);
        using var audio = new OfflineAudio(b.Project, sequence.Id, @"C:\proyecto", new Backend(Open));
        string path = Path.Combine(_folder, "exportado.wav");
        audio.WriteWave(path, Time.Zero, length, ExportAudioFormat.Default, 0, null, CancellationToken.None);
        float[] exported = ReadFloatWave(path);

        using var mixer = new AudioMixer(b.Plan().Audio, Open, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5), TimeProvider.System);
        float[] preview = new float[exported.Length];
        int done = 0;
        int[] sizes = [441, 1024, 333, 960, 2048, 17];
        for (int n = 0; done < preview.Length / 2; n++)
        {
            int frames = Math.Min(sizes[n % sizes.Length], (preview.Length / 2) - done);
            WaitFor(() => mixer.BufferedFrames >= frames);
            Assert.Equal(frames, mixer.Read(preview.AsSpan(2 * done, 2 * frames)));
            done += frames;
            if (n is 25 or 110)
            {
                mixer.Reload(b.Plan().Audio);
            }
        }

        int different = 0;
        for (int i = 0; i < exported.Length; i++)
        {
            different += exported[i] != preview[i] ? 1 : 0;
        }

        output.WriteLine($"{exported.Length / 2} cuadros con dos buses (delay, limitador, reverb), envíos pre y post, puerta, de-esser, saturación, gráfico, tono y paneo automatizado: {different} muestras distintas");
        Assert.Equal(0, different);
        Assert.Contains(exported, s => Math.Abs(s) > 0.05f);
    }

    [Fact]
    public void A_typical_mix_costs_little_per_block_and_allocates_nothing()
    {
        var b = new TimelineBuilder(0, 8);
        for (int t = 0; t < 8; t++)
        {
            b.AddMedia($"m{t}", Rational.Fps30, 1800, video: false);
            b.AddAudio(t, $"m{t}", Time.Zero, Time.Zero, S(60));
            Track track = b.Sequence.AudioTracks[t];
            track.Effects.Add(Effect($"eq{t}", BuiltInEffects.Equalizer, new() { [BuiltInEffects.EqualizerParameters.LowCut] = 80, [BuiltInEffects.EqualizerParameters.PresenceGain] = 3 }));
            track.Effects.Add(Effect($"c{t}", BuiltInEffects.Compressor, new() { [BuiltInEffects.CompressorParameters.Threshold] = -20 }));
            track.Effects.Add(FaderEffect($"f{t}", -3, (t * 25) - 100));
            track.Output = t < 4 ? "voces" : "musica";
            track.Sends = [new AudioSend { BusId = "sala", GainDb = -10 }];
        }

        b.Sequence.AudioBuses.Add(new AudioBus { Id = "voces", Name = "Voces", Effects = [Effect("vc", BuiltInEffects.Compressor, new() { [BuiltInEffects.CompressorParameters.Lookahead] = 3 })] });
        b.Sequence.AudioBuses.Add(new AudioBus { Id = "musica", Name = "Música", Effects = [Effect("md", BuiltInEffects.Delay, new())] });
        b.Sequence.AudioBuses.Add(new AudioBus { Id = "sala", Name = "Sala", Effects = [Effect("sr", BuiltInEffects.Reverb, new() { [BuiltInEffects.ReverbParameters.Mix] = 100 })] });
        b.Sequence.MasterEffects.Add(Effect("ml", BuiltInEffects.Limiter, new()));
        using var mixer = new MixRenderer(b.Plan().Audio, path => new FunctionPcm(i => (float)(0.3 * Math.Sin(i * 0.05)), 60L * Rate), TimeSpan.FromSeconds(5),
            TimeProvider.System, stripMeters: true);
        float[] block = new float[2048];
        // Calentamiento por lecturas y no solo por reloj: en la CI cargada 1 s daba para pocas lecturas.
        var warmUp = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 20 || warmUp.ElapsedMilliseconds < 1000; i++)
        {
            mixer.Read(block);
        }

        bool optimized = typeof(MixRenderer).Assembly.GetCustomAttributes(typeof(System.Diagnostics.DebuggableAttribute), false)
            .Cast<System.Diagnostics.DebuggableAttribute>().All(a => !a.IsJITOptimizerDisabled);
        double best = double.PositiveInfinity;
        long allocated = 0;
        for (int run = 0; run < 5; run++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 40; i++)
            {
                mixer.Read(block);
            }

            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            best = Math.Min(best, watch.Elapsed.TotalMilliseconds / 40);
        }

        output.WriteLine($"Bloque de 1024 cuadros: 8 pistas con ecualizador, compresor y fader, 3 buses (compresor, delay, reverb), envíos, medidores y limitador: {best:0.000} ms ({1024.0 / Rate * 1000 / best:0}× tiempo real{(optimized ? "" : ", Debug")}), {allocated} bytes reservados");
        Assert.Equal(0, allocated);
        // La cota de tiempo vale para código optimizado (Release o Native AOT); en Debug, con la máquina cargada, solo se informa.
        Assert.True(!optimized || best < 4, $"{best} ms");
    }

    /// <summary>Tres pistas con senos distintos (0,1 a 440 Hz, 0,15 a 1 kHz y 0,08 a 2,5 kHz) de 2 s.</summary>
    private static TimelineBuilder ThreeTracks(out Func<string, Func<long, float>> sources)
    {
        var b = new TimelineBuilder(0, 3);
        for (int t = 0; t < 3; t++)
        {
            b.AddMedia($"s{t}", Rational.Fps30, 900, video: false);
            b.AddAudio(t, $"s{t}", Time.Zero, Time.Zero, S(3));
        }

        static Func<long, float> Sine(double amplitude, double hz) => i => (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / Rate));
        sources = path => path.Contains("s0", StringComparison.Ordinal) ? Sine(0.1, 440)
            : path.Contains("s1", StringComparison.Ordinal) ? Sine(0.15, 1000)
            : Sine(0.08, 2500);
        return b;
    }

    private static EffectInstance Fader(Track track)
    {
        EffectInstance fader = FaderEffect("f-" + track.Id, 0, 0);
        track.Effects.Add(fader);
        return fader;
    }

    private static EffectInstance FaderEffect(string id, double db, double pan) => new()
    {
        Id = id,
        Type = BuiltInEffects.Audio,
        Parameters = { [A.GainDb] = new Parameter { Value = db }, [A.Pan] = new Parameter { Value = pan } },
    };

    private static EffectInstance Effect(string id, string type, Dictionary<string, double> values)
    {
        var effect = new EffectInstance { Id = id, Type = type };
        foreach ((string name, double value) in values)
        {
            effect.Parameters[name] = new Parameter { Value = value };
        }

        return effect;
    }

    private static float[] Mix(TimelineBuilder b, int frames, Func<string, Func<long, float>> sources)
    {
        using var mixer = new MixRenderer(b.Plan().Audio, path => new FunctionPcm(sources(path), 30L * Rate), TimeSpan.FromSeconds(5), TimeProvider.System);
        return Read(mixer, frames);
    }

    private static float[] Read(MixRenderer mixer, int frames)
    {
        var all = new float[frames * 2];
        int[] sizes = [1024, 333, 2048, 17, 960];
        for (int done = 0, n = 0; done < frames; n++)
        {
            int count = Math.Min(sizes[n % sizes.Length], frames - done);
            Assert.Equal(count, mixer.Read(all.AsSpan(done * 2, count * 2)));
            done += count;
        }

        return all;
    }

    private static void WaitFor(Func<bool> condition)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "La mezcla no llegó a tiempo.");
            Thread.Sleep(1);
        }
    }

    private static float[] ReadFloatWave(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int at = 12;
        while (at + 8 <= bytes.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(bytes, at, 4);
            int size = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at + 4));
            if (id == "data")
            {
                float[] samples = new float[size / 4];
                for (int i = 0; i < samples.Length; i++)
                {
                    samples[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(at + 8 + (4 * i)));
                }

                return samples;
            }

            at += 8 + size + (size & 1);
        }

        throw new InvalidDataException("El WAV no tiene datos.");
    }

    private sealed class Backend(Func<string, IPcmSource> open) : IClipBackend
    {
        public IClipVideo OpenVideo(string path, Rodaje.Media.Video.VideoDecoderOptions options) => throw new NotSupportedException();

        public IPcmSource OpenAudio(string path) => open(path);

        public Rodaje.Audio.IAudioOutput OpenOutput(Rodaje.Audio.IAudioSampleSource source, Rodaje.Audio.AudioOutputOptions options) =>
            throw new NotSupportedException();

        public Rodaje.Playback.IDecodeExecutor CreateExecutor() => throw new NotSupportedException();
    }
}
