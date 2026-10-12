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

namespace Rodaje.Engine.Tests.Timeline;

/// <summary>Ecualizador y compresor en la mezcla: la vista previa y la exportación dan las mismas muestras.</summary>
public sealed class AudioEffectsMixTests(ITestOutputHelper output) : IDisposable
{
    private const int Rate = 48_000;
    private readonly string _folder = Directory.CreateTempSubdirectory("rodaje-efectos-audio-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Preview_and_export_give_the_same_samples_with_equalizer_and_compressor()
    {
        TimelineBuilder b = Project(out Func<string, IPcmSource> open);
        Time length = S(6);
        using var audio = new OfflineAudio(b.Project, b.Sequence.Id, @"C:\proyecto", new Backend(open));
        string path = Path.Combine(_folder, "exportado.wav");
        audio.WriteWave(path, Time.Zero, length, ExportAudioFormat.Default, 0, null, CancellationToken.None);
        float[] exported = ReadFloatWave(path);

        // La vista previa: la mezcla adelantada en su hilo, lecturas de tamaños sueltos y dos recargas por ediciones.
        using var mixer = new AudioMixer(b.Plan().Audio, open, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(5), TimeProvider.System);
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
        double worst = 0;
        for (int i = 0; i < exported.Length; i++)
        {
            double d = Math.Abs(exported[i] - preview[i]);
            different += d > 0 ? 1 : 0;
            worst = Math.Max(worst, d);
        }

        output.WriteLine($"{exported.Length / 2} cuadros: {different} muestras distintas, diferencia máxima {worst}");
        Assert.Equal(0, different);
        Assert.Contains(exported, s => Math.Abs(s) > 0.05f);
    }

    [Fact]
    public void A_preset_changes_the_sound_and_an_unchanged_equalizer_is_not_applied()
    {
        TimelineBuilder b = Project(out Func<string, IPcmSource> open);
        AudioPlan plan = b.Plan().Audio;
        Assert.True(plan.HasEffects);
        Assert.NotNull(plan.Tracks[0].Effects);
        Assert.NotNull(plan.Tracks[0].Clips[0].Effects);

        Clip clip = b.Sequence.AudioTracks[0].Clips[0];
        clip.Effects.Clear();
        clip.Effects.Add(new EffectInstance { Id = "plano", Type = BuiltInEffects.Equalizer });
        b.Sequence.AudioTracks[0].Effects.Clear();
        Assert.False(b.Plan().Audio.HasEffects);
        Assert.False(b.Plan().Audio.SoundsLike(plan));
    }

    private static TimelineBuilder Project(out Func<string, IPcmSource> open)
    {
        var b = new TimelineBuilder(0, 1);
        b.AddMedia("voz", Rational.Fps30, 900, video: false);
        b.AddMedia("musica", Rational.Fps30, 900, video: false);
        Clip voice = b.AddAudio(0, "voz", S(0.25), S(1), S(3));
        b.AddAudio(0, "musica", S(3.25), Time.Zero, S(2.5));
        voice.Effects.Add(Effect("eq", BuiltInEffects.Equalizer, AudioPresets.SocialVoice.Equalizer!));
        voice.Effects.Add(Effect("comp", BuiltInEffects.Compressor, AudioPresets.SocialVoice.Compressor!));
        b.Sequence.AudioTracks[0].Effects.Add(Effect("pista", BuiltInEffects.Compressor, AudioPresets.MusicGlue.Compressor!));
        Func<long, float> speech = AudioSignals.Syllables(7);
        Func<long, float> music = i => (float)((0.3 * Math.Sin(2 * Math.PI * 220 * i / Rate)) + (0.1 * Math.Sin(2 * Math.PI * 3300 * i / Rate)));
        open = path => new FunctionPcm(path.Contains("voz", StringComparison.Ordinal) ? speech : music, 30L * Rate);
        return b;
    }

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

    /// <summary>Las muestras del bloque «data» de un WAV de float de 32 bits.</summary>
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
