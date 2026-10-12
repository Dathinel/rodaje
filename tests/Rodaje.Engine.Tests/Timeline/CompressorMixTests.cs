using System.Buffers.Binary;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Offline;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Rodaje.Media.Export;
using Xunit;
using Xunit.Abstractions;
using static Rodaje.Engine.Tests.Support.Seconds;
using K = Rodaje.Core.Model.BuiltInEffects.CompressorParameters;

namespace Rodaje.Engine.Tests.Timeline;

/// <summary>
/// El compresor en la mezcla: la anticipación compensada en clips, pistas y mezcla; el sidechain de otra pista; los
/// keyframes; la escucha; y la vista previa igual a la exportación muestra a muestra.
/// </summary>
public sealed class CompressorMixTests(ITestOutputHelper output) : IDisposable
{
    private const int Rate = 48_000;
    private readonly string _folder = Directory.CreateTempSubdirectory("rodaje-compresor-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Theory]
    [InlineData("clip")]
    [InlineData("pista")]
    [InlineData("mezcla")]
    [InlineData("los tres")]
    public void Lookahead_and_limiter_latency_is_compensated_on_the_timeline(string where)
    {
        var b = new TimelineBuilder(0, 1);
        b.AddMedia("clics", Rational.Fps30, 900, video: false);
        Clip clip = b.AddAudio(0, "clics", S(0.25), Time.Zero, S(2.5));

        // Sin reducción (umbral 0, 1:1) y con el limitador por encima de los clics: solo queda el retraso, que se compensa.
        var values = new Dictionary<string, double> { [K.Threshold] = 0, [K.Ratio] = 1, [K.Lookahead] = 10, [K.Limiter] = 1, [K.Ceiling] = -1 };
        if (where is "clip" or "los tres")
        {
            clip.Effects.Add(Effect("c", BuiltInEffects.Compressor, values));
        }

        if (where is "pista" or "los tres")
        {
            b.Sequence.AudioTracks[0].Effects.Add(Effect("p", BuiltInEffects.Compressor, values));
        }

        if (where is "mezcla" or "los tres")
        {
            b.Sequence.MasterEffects.Add(Effect("m", BuiltInEffects.Compressor, values));
        }

        float[] mix = Mix(b, 3 * Rate, _ => i => i is 24_000 or 72_000 ? 0.5f : 0f);
        int[] clicks = [.. Enumerable.Range(0, mix.Length / 2).Where(i => Math.Abs(mix[2 * i]) > 0.01f)];
        output.WriteLine($"{where}: clics en las muestras {string.Join(", ", clicks)} (esperadas 36000 y 84000), valores {mix[2 * 36_000]} y {mix[2 * 84_000]}");
        Assert.Equal([36_000, 84_000], clicks);
        Assert.Equal(0.5f, mix[2 * 36_000]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public void A_voice_track_ducks_the_music_through_the_sidechain(double lookaheadMs)
    {
        TimelineBuilder b = Ducking(lookaheadMs, out Func<string, Func<long, float>> sources);
        string music = b.Sequence.AudioTracks[1].Id;
        using var mixer = new MixRenderer(b.Plan().Audio, path => new FunctionPcm(sources(path), 30L * Rate), TimeSpan.FromSeconds(5), TimeProvider.System)
        {
            Monitor = new DynamicsMonitor(DynamicsMonitor.ForTrack(music)),
        };
        float[] mix = Read(mixer, 3 * Rate);
        Func<long, float> voice = sources("voz");

        // La música es una continua de 0,25: lo que queda al quitar la voz es su ganancia en cada muestra.
        double Gain(long at) => (mix[2 * at] - voice(at)) / 0.25;
        double before = Db(Gain(Rate * 9 / 10));
        double during = Db(Gain(Rate * 3 / 2));
        double onset = Db(Gain(Rate));
        double after = Db(Gain(Rate * 28 / 10));
        double meter = mixer.Dynamics.Measure(Rate * 14 / 10, Rate * 16 / 10)?.ReductionDb ?? 0;
        output.WriteLine($"Anticipación {lookaheadMs} ms: música {before:0.00} dB antes de la voz, {onset:0.00} dB cuando empieza, {during:0.00} dB durante y {after:0.00} dB 0,8 s después; el medidor marca {meter:0.00} dB de reducción");
        Assert.Equal(0, before, 0.01);
        Assert.InRange(during, -22, -15);
        Assert.InRange(meter, 15, 22);
        Assert.True(after > -0.5);
        if (lookaheadMs > 0)
        {
            Assert.True(onset < -10, "Con anticipación la música ya bajó cuando entra la voz.");
        }
        else
        {
            Assert.True(onset > -1);
        }
    }

    [Fact]
    public void Threshold_and_mix_keyframes_move_the_compression()
    {
        var b = new TimelineBuilder(0, 1);
        b.AddMedia("continua", Rational.Fps30, 900, video: false);
        Clip clip = b.AddAudio(0, "continua", Time.Zero, Time.Zero, S(4));
        EffectInstance compressor = Effect("c", BuiltInEffects.Compressor, new Dictionary<string, double>
        {
            [K.Ratio] = 100, [K.Attack] = 0.1, [K.Release] = 10, [K.Knee] = 0,
        });
        compressor.Parameters[K.Threshold] = new Parameter { Keyframes = [new Keyframe(S(0), 0, Interpolation.Linear), new Keyframe(S(4), -40, Interpolation.Linear)] };
        clip.Effects.Add(compressor);
        EffectInstance parallel = Effect("p", BuiltInEffects.Compressor, new Dictionary<string, double> { [K.Threshold] = -60, [K.Ratio] = 100, [K.Knee] = 0 });
        parallel.Parameters[K.Mix] = new Parameter { Keyframes = [new Keyframe(S(0), 100, Interpolation.Linear), new Keyframe(S(4), 0, Interpolation.Linear)] };
        b.Sequence.AudioTracks[0].Effects.Add(parallel);

        // La pista limita a −60 dBFS con una mezcla que va de 100 % a 0 %: la salida es la recta entre el clip y −60.
        float[] mix = Mix(b, 4 * Rate, _ => _ => 0.5f);
        foreach (double t in new[] { 1.0, 2.0, 3.0 })
        {
            long at = (long)(t * Rate);
            double clipOut = Math.Min(0.5, Math.Pow(10, -10 * t / 20));
            double mixValue = 1 - (t / 4);
            double expected = (mixValue * 0.001) + ((1 - mixValue) * clipOut);
            output.WriteLine($"{t} s: umbral del clip {-10 * t} dB, mezcla de la pista {mixValue:P0}: salida {mix[2 * at]:0.00000} (esperada {expected:0.00000})");
            Assert.Equal(expected, mix[2 * at], 0.002);
        }
    }

    [Fact]
    public void Listening_and_bypass_only_change_the_preview_and_stay_aligned()
    {
        TimelineBuilder b = Ducking(10, out Func<string, Func<long, float>> sources);
        string music = DynamicsMonitor.ForTrack(b.Sequence.AudioTracks[1].Id);
        IPcmSource Open(string path) => new FunctionPcm(sources(path), 30L * Rate);
        Func<long, float> voice = sources("voz");

        using var bypassed = new MixRenderer(b.Plan().Audio, Open, TimeSpan.FromSeconds(5), TimeProvider.System) { Monitor = new DynamicsMonitor(music, Bypass: true) };
        float[] dry = Read(bypassed, 2 * Rate);
        using var listening = new MixRenderer(b.Plan().Audio, Open, TimeSpan.FromSeconds(5), TimeProvider.System) { Monitor = new DynamicsMonitor(music, Listen: true) };
        float[] key = Read(listening, 2 * Rate);

        // Comparar sin compresor: la música vuelve a 0,25 exacta. Escuchar el sidechain: en su lugar suena la voz, alineada.
        double worstDry = 0, worstKey = 0;
        for (long i = 0; i < 2 * Rate; i++)
        {
            worstDry = Math.Max(worstDry, Math.Abs(dry[2 * i] - voice(i) - 0.25));
            if (Math.Abs(2 * voice(i)) < 0.85)
            {
                // Por encima de 0,9 actúa el limitador suave de la suma.
                worstKey = Math.Max(worstKey, Math.Abs(key[2 * i] - (2 * voice(i))));
            }
        }

        output.WriteLine($"Sin compresor: error {worstDry:E2}; escuchando el sidechain: error {worstKey:E2}");
        Assert.True(worstDry < 1e-6);
        Assert.True(worstKey < 1e-6);
    }

    [Fact]
    public void Preview_and_export_give_the_same_samples_with_the_full_compressor()
    {
        TimelineBuilder b = Ducking(10, out Func<string, Func<long, float>> sources);
        Clip voice = b.Sequence.AudioTracks[0].Clips[0];
        EffectInstance multiband = Effect("mb", BuiltInEffects.Compressor, new Dictionary<string, double>
        {
            [K.Ratio] = 3, [K.Attack] = 3, [K.Release] = 120, [K.Bands] = 3, [K.Detector] = 1, [K.AutoRelease] = 1, [K.AutoMakeup] = 1,
            [K.Lookahead] = 3, [K.Mix] = 80, [K.LowThreshold] = -4, [K.HighGain] = 2, [K.StereoLink] = 0,
        });
        multiband.Parameters[K.Threshold] = new Parameter { Keyframes = [new Keyframe(voice.SourceIn, -20, Interpolation.Linear), new Keyframe(voice.SourceOut, -30, Interpolation.EaseInOut)] };
        voice.Effects.Add(multiband);
        EffectInstance master = Effect("m", BuiltInEffects.Compressor, new Dictionary<string, double>
        {
            [K.Threshold] = -12, [K.Ratio] = 2, [K.Limiter] = 1, [K.Ceiling] = -1, [K.Makeup] = 6, [K.SidechainHighPass] = 120,
        });
        master.Parameters[K.Mix] = new Parameter { Keyframes = [new Keyframe(S(0.5), 100, Interpolation.Linear), new Keyframe(S(2.5), 50, Interpolation.Linear)] };
        b.Sequence.MasterEffects.Add(master);

        Time length = S(3);
        IPcmSource Open(string path) => new FunctionPcm(sources(path), 30L * Rate);
        using var audio = new OfflineAudio(b.Project, b.Sequence.Id, @"C:\proyecto", new Backend(Open));
        string path = Path.Combine(_folder, "exportado.wav");
        audio.WriteWave(path, Time.Zero, length, ExportAudioFormat.Default, 0, null, CancellationToken.None);
        float[] exported = ReadFloatWave(path);

        // La vista previa: la mezcla adelantada en su hilo, lecturas de tamaños sueltos, dos recargas y el medidor encendido.
        using var mixer = new AudioMixer(b.Plan().Audio, Open, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5), TimeProvider.System);
        mixer.Monitor(new DynamicsMonitor(DynamicsMonitor.ForMix()));
        float[] preview = new float[exported.Length];
        int done = 0;
        int[] sizes = [441, 1024, 333, 960, 2048, 17];
        for (int n = 0; done < preview.Length / 2; n++)
        {
            int frames = Math.Min(sizes[n % sizes.Length], (preview.Length / 2) - done);
            WaitFor(() => mixer.BufferedFrames >= frames);
            Assert.Equal(frames, mixer.Read(preview.AsSpan(2 * done, 2 * frames)));
            done += frames;
            if (n is 20 or 90)
            {
                mixer.Reload(b.Plan().Audio);
            }
        }

        int different = 0;
        for (int i = 0; i < exported.Length; i++)
        {
            different += exported[i] != preview[i] ? 1 : 0;
        }

        output.WriteLine($"{exported.Length / 2} cuadros con sidechain, anticipación, multibanda, limitador y keyframes en clip, pista y mezcla: {different} muestras distintas");
        Assert.Equal(0, different);
        Assert.Contains(exported, s => Math.Abs(s) > 0.05f);
    }

    /// <summary>Pista 0: voz de 1 kHz a −6 dBFS de 1 a 2 s. Pista 1: música continua de 0,25 con un compresor que escucha la voz.</summary>
    private static TimelineBuilder Ducking(double lookaheadMs, out Func<string, Func<long, float>> sources)
    {
        var b = new TimelineBuilder(0, 2);
        b.AddMedia("voz", Rational.Fps30, 900, video: false);
        b.AddMedia("musica", Rational.Fps30, 900, video: false);
        b.AddAudio(0, "voz", Time.Zero, Time.Zero, S(3));
        b.AddAudio(1, "musica", Time.Zero, Time.Zero, S(3));
        EffectInstance ducker = Effect("duck", BuiltInEffects.Compressor, new Dictionary<string, double>
        {
            [K.Threshold] = -30, [K.Ratio] = 10, [K.Attack] = 1, [K.Release] = 60, [K.Knee] = 0, [K.Lookahead] = lookaheadMs,
        });
        ducker.Options[BuiltInEffects.CompressorOptions.Sidechain] = b.Sequence.AudioTracks[0].Id;
        b.Sequence.AudioTracks[1].Effects.Add(ducker);
        static float Voice(long i) => i is >= Rate and < 2 * Rate ? (float)(0.5 * Math.Sin(2 * Math.PI * 1000 * i / Rate)) : 0f;
        sources = path => path.Contains("voz", StringComparison.Ordinal) ? Voice : _ => 0.25f;
        return b;
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

    private static double Db(double gain) => 20 * Math.Log10(Math.Max(Math.Abs(gain), 1e-9));

    private static EffectInstance Effect(string id, string type, IReadOnlyDictionary<string, double> values)
    {
        var effect = new EffectInstance { Id = id, Type = type };
        foreach ((string name, double value) in values)
        {
            effect.Parameters[name] = new Parameter { Value = value };
        }

        return effect;
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
