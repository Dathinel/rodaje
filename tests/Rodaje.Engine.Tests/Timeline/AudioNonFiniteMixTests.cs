using System.Buffers.Binary;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Offline;
using Rodaje.Engine.Tests.Support;
using Rodaje.Media.Export;
using Xunit;
using static Rodaje.Engine.Tests.Support.Seconds;

namespace Rodaje.Engine.Tests.Timeline;

/// <summary>
/// Tarea 92: un origen que entrega NaN o ±∞ (un archivo de float roto, un decodificador que falla) se oye como silencio
/// en ese tramo y nada más: las inserciones del clip, de la pista y de la mezcla con estado (filtros, reverb, delay) no
/// quedan envenenadas y lo que sigue suena. También con cada inserción en sus valores extremos.
/// </summary>
[Collection(ExtremeValuesGroup.Name)]
public sealed class AudioNonFiniteMixTests : IDisposable
{
    private const int Rate = 48_000;
    private readonly string _folder = Directory.CreateTempSubdirectory("rodaje-nan-audio-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    /// <summary>Un tono con un tramo de muestras no numéricas entre 1 y 1,5 s.</summary>
    private static float Poisoned(long i)
    {
        if (i is >= Rate and < 3 * Rate / 2)
        {
            return (i % 5) switch { 0 => float.NaN, 1 => float.PositiveInfinity, 2 => float.NegativeInfinity, 3 => float.MaxValue, _ => 0.5f };
        }

        return (float)(0.4 * Math.Sin(2 * Math.PI * 330 * i / Rate));
    }

    [Fact]
    public void Las_muestras_no_numericas_se_oyen_como_silencio_y_lo_que_sigue_suena()
    {
        float[] mix = Export(b =>
        {
            Clip clip = b.Sequence.AudioTracks[0].Clips[0];
            clip.Effects.Add(Effect("eq", BuiltInEffects.Equalizer, (BuiltInEffects.EqualizerParameters.MidGain, 6)));
            clip.Effects.Add(Effect("rev", BuiltInEffects.Reverb));
            b.Sequence.AudioTracks[0].Effects.Add(Effect("comp", BuiltInEffects.Compressor, (BuiltInEffects.CompressorParameters.Bands, 3)));
            b.Sequence.MasterEffects.Add(Effect("delay", BuiltInEffects.Delay));
            b.Sequence.MasterEffects.Add(Effect("lim", BuiltInEffects.Limiter));
        });

        AssertFinite(mix);
        AssertSounds(mix, from: 3 * Rate, to: 4 * Rate);
    }

    [Fact]
    public void Sin_inserciones_las_muestras_no_numericas_tambien_salen_como_silencio()
    {
        float[] mix = Export(_ => { });
        AssertFinite(mix);
        AssertSounds(mix, from: 2 * Rate, to: 3 * Rate);
    }

    [Fact]
    public void Cada_insercion_en_sus_extremos_con_un_origen_envenenado_no_deja_NaN()
    {
        foreach (EffectDefinition definition in EffectRegistry.All.Where(d => d.Stage == EffectStage.Audio && !d.IsTransition && d.Type != BuiltInEffects.Audio))
        {
            foreach (bool high in new[] { false, true })
            {
                float[] mix = Export(b =>
                {
                    var effect = new EffectInstance { Id = "fx", Type = definition.Type };
                    foreach (ParameterDefinition p in definition.Parameters)
                    {
                        effect.Parameters[p.Name] = new Parameter { Value = high ? p.Maximum : p.Minimum };
                    }

                    b.Sequence.AudioTracks[0].Clips[0].Effects.Add(effect);
                }, seconds: 3);
                AssertFinite(mix, $"{definition.Type} en sus {(high ? "máximos" : "mínimos")}");
            }
        }
    }

    private float[] Export(Action<TimelineBuilder> configure, double seconds = 4)
    {
        var b = new TimelineBuilder(0, 1);
        b.AddMedia("roto", Rational.Fps30, 900, video: false);
        b.AddAudio(0, "roto", Time.Zero, Time.Zero, S(seconds));
        configure(b);
        Func<string, IPcmSource> open = _ => new FunctionPcm(Poisoned, 30L * Rate);
        using var audio = new OfflineAudio(b.Project, b.Sequence.Id, @"C:\proyecto", new Backend(open));
        string path = Path.Combine(_folder, $"{Guid.NewGuid():N}.wav");
        audio.WriteWave(path, Time.Zero, S(seconds), ExportAudioFormat.Default, 0, null, CancellationToken.None);
        return ReadFloatWave(path);
    }

    private static EffectInstance Effect(string id, string type, params (string Name, double Value)[] values)
    {
        var effect = new EffectInstance { Id = id, Type = type };
        foreach ((string name, double value) in values)
        {
            effect.Parameters[name] = new Parameter { Value = value };
        }

        return effect;
    }

    private static void AssertFinite(float[] samples, string what = "")
    {
        int bad = Array.FindIndex(samples, s => !float.IsFinite(s));
        Assert.True(bad < 0, $"{what}: la muestra {bad} ({bad / 2.0 / Rate:0.000} s) no es un número");
    }

    private static void AssertSounds(float[] samples, int from, int to)
    {
        double energy = 0;
        for (int i = 2 * from; i < 2 * to && i < samples.Length; i++)
        {
            energy += samples[i] * (double)samples[i];
        }

        Assert.True(energy / (2 * (to - from)) > 1e-3, $"Tras las muestras no numéricas quedó en silencio ({energy}).");
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
