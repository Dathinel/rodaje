using System.Reflection;
using Rodaje.Audio.Processing;
using Xunit;

namespace Rodaje.Audio.Tests.Processing;

/// <summary>
/// Tarea 92: cada ajuste numérico de cada inserción de audio con valores extremos (0, negativos, diminutos, enormes,
/// NaN e infinitos) no lanza, no se cuelga ni deja NaN en la salida; y las muestras que no son números (NaN, ±∞) entran
/// como silencio, sin envenenar el estado de los filtros: el audio limpio que sigue suena.
/// </summary>
public sealed class ExtremeSettingsTests
{
    private const int Rate = 48_000;
    private const int Frames = 1024;

    public static readonly double[] Extremes =
    [
        // Cada uno cae en un caso distinto de los límites (cero, negativo, diminuto, enorme, no numérico); más valores
        // repetirían los mismos casos y cargarían el runner.
        0, -1, 1e-9, 1e300, -1e300, double.NaN, double.PositiveInfinity, double.NegativeInfinity,
    ];

    private static readonly MultibandSettings Bands = new(200, 3000, -6, 0, 6, 2, 0, -2);

    /// <summary>Una muestra de cada tipo de inserción con todas sus opciones encendidas.</summary>
    public static IReadOnlyList<AudioProcessorSettings> Samples { get; } =
    [
        new EqualizerSettings(80, 3, 120, -3, 500, 1, 4, 3000, 1, 2, 8000),
        new CompressorSettings(-18, 3, 10, 150, 2, 6)
        {
            Detector = LevelDetector.Rms, AutoRelease = true, AutoMakeup = true, LookaheadMs = 5, Mix = 0.8,
            SidechainHighPassHz = 80, Multiband = Bands, CeilingDb = -1,
        },
        new CompressorSettings(-18, 3, 10, 150, 2, 6) { Linked = false, RmsWindowMs = 20 },
        new GateSettings(-45, 40, 20, 1, 40, 120) { HysteresisDb = 4, SidechainHighPassHz = 100 },
        new GateSettings(-45, 40, 2, 1, 40, 120),
        new DeEsserSettings(6000, -30, 8) { SplitBand = true },
        new DeEsserSettings(6000, -30, 8) { SplitBand = false },
        new ReverbSettings(20, 0.5, 1.6, 7000, 1, 120, 0.25),
        new DelaySettings(350, 0.35, 0.25) { PingPong = true, LowCutHz = 120, HighCutHz = 7000 },
        new DelaySettings(350, 0.35, 0.25) { Sync = true, Tempo = 120, Division = DelayDivision.DottedEighth },
        new SaturationSettings(6, 0.5, 8000, -2, 1),
        new GraphicEqualizerSettings(3, -3, 2, -2, 1, -1, 4, -4, 6, -6) { OutputDb = 1 },
        new PitchShiftSettings(3, 20, 1),
        new LimiterSettings(-1, 6, 60),
    ];

    public static TheoryData<int> SampleIndexes()
    {
        var data = new TheoryData<int>();
        for (int i = 0; i < Samples.Count; i++)
        {
            data.Add(i);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SampleIndexes))]
    public void CadaAjusteConValoresExtremos_NoLanzaNiDejaNaN(int index)
    {
        AudioProcessorSettings sample = Samples[index];
        foreach ((string name, AudioProcessorSettings settings) in Variants(sample))
        {
            var chain = new AudioEffectChain(new AudioEffectsSettings([new AudioInsert("x", settings)]), Rate);
            var random = new Random(92);
            for (int block = 0; block < 3; block++)
            {
                float[] io = Noise(random, Frames, 0.5f);
                chain.Process(io);
                AssertFinite(io, $"{sample.GetType().Name}.{name} en el bloque {block}");
            }

            // Cambiar los ajustes sobre el estado ya en marcha (lo que hace la vista previa al arrastrar) tampoco.
            chain.Configure(new AudioEffectsSettings([new AudioInsert("x", sample)]));
            float[] after = Noise(random, Frames, 0.5f);
            chain.Process(after);
            AssertFinite(after, $"{sample.GetType().Name}.{name} al volver a los ajustes normales");
        }
    }

    [Theory]
    [MemberData(nameof(SampleIndexes))]
    public void MuestrasQueNoSonNumeros_EntranComoSilencio_YElAudioSigueSonando(int index)
    {
        AudioProcessorSettings sample = Samples[index];
        var chain = new AudioEffectChain(new AudioEffectsSettings([new AudioInsert("x", sample)]), Rate);
        var random = new Random(92);
        for (int block = 0; block < 3; block++)
        {
            float[] io = Noise(random, Frames, 0.5f);
            for (int i = 0; i < io.Length; i += 37)
            {
                io[i] = (i / 37 % 4) switch { 0 => float.NaN, 1 => float.PositiveInfinity, 2 => float.NegativeInfinity, _ => float.MaxValue };
            }

            chain.Process(io);
            AssertFinite(io, $"{sample.GetType().Name} con muestras no numéricas en el bloque {block}");
        }

        // Después, el audio limpio sale y no es silencio (los filtros no quedaron envenenados).
        double energy = 0;
        for (int block = 0; block < 40; block++)
        {
            float[] io = Noise(random, Frames, 0.5f);
            chain.Process(io);
            AssertFinite(io, $"{sample.GetType().Name} tras las muestras no numéricas, bloque {block}");
            if (block >= 30)
            {
                energy += io.Sum(s => (double)s * s);
            }
        }

        Assert.True(energy > 1, $"{sample.GetType().Name} quedó en silencio tras las muestras no numéricas ({energy}).");
    }

    [Fact]
    public void LaPuertaEnUnoAUno_DejaPasarElAudio()
    {
        // El mínimo del modelo: al arrancar cerrada la ganancia era (−∞ − umbral) · 0 = NaN y la pista quedaba muda.
        var chain = new AudioEffectChain(new AudioEffectsSettings([new AudioInsert("g", new GateSettings(-45, 40, 1, 1, 40, 120))]), Rate);
        Assert.Equal(0, new GateSettings(-45, 40, 1, 1, 40, 120).StaticGainDb(double.NegativeInfinity));
        var random = new Random(92);
        float[] input = Noise(random, Frames, 0.5f);
        float[] io = (float[])input.Clone();
        chain.Process(io);
        AssertFinite(io, "la puerta a 1:1");
        Assert.Equal(input, io);
    }

    [Fact]
    public void ElSidechainConMuestrasNoNumericas_NoEnvenenaAlCompresor()
    {
        var settings = new CompressorSettings(-30, 8, 1, 50, 0, 0) { LookaheadMs = 2, SidechainHighPassHz = 100 };
        var chain = new AudioEffectChain(
            new AudioEffectsSettings([new AudioInsert("eq", Samples[0]), new AudioInsert("c", settings)]), Rate);
        var random = new Random(92);
        float[] key = Noise(random, Frames, 0.5f);
        key[10] = float.NaN;
        key[11] = float.PositiveInfinity;
        float[] io = Noise(random, Frames, 0.5f);
        chain.Process(io, key);
        AssertFinite(io, "con el sidechain envenenado");
        for (int block = 0; block < 20; block++)
        {
            io = Noise(random, Frames, 0.5f);
            chain.Process(io, Noise(random, Frames, 0.5f));
            AssertFinite(io, $"tras el sidechain envenenado, bloque {block}");
        }

        Assert.True(io.Sum(s => (double)s * s) > 1, "El compresor quedó en silencio tras un sidechain no numérico.");
    }

    [Fact]
    public void LosFiltrosConFrecuenciasYQExtremos_DanCoeficientesFinitos()
    {
        foreach (double hz in Extremes)
        {
            foreach (double q in Extremes)
            {
                foreach (double db in Extremes)
                {
                    Biquad[] filters =
                    [
                        Biquad.HighPass(Rate, hz), Biquad.LowShelf(Rate, hz, db), Biquad.HighShelf(Rate, hz, db),
                        Biquad.Peaking(Rate, hz, q, db),
                    ];
                    foreach (Biquad filter in filters)
                    {
                        double response = filter.MagnitudeDb(Rate, 1000);
                        Assert.False(double.IsNaN(response), $"Biquad con {hz} Hz, Q {q}, {db} dB: {response}");
                    }
                }
            }
        }
    }

    /// <summary>La muestra con cada ajuste numérico (también los del multibanda) cambiado por cada valor extremo.</summary>
    private static IEnumerable<(string Name, AudioProcessorSettings Settings)> Variants(AudioProcessorSettings sample)
    {
        foreach (PropertyInfo property in Numeric(sample.GetType()))
        {
            foreach (double value in Extremes)
            {
                yield return ($"{property.Name}={value}", With(sample, property, value));
            }
        }

        if (sample is CompressorSettings { Multiband: { } bands } compressor)
        {
            foreach (PropertyInfo property in Numeric(typeof(MultibandSettings)))
            {
                foreach (double value in Extremes)
                {
                    yield return ($"Multiband.{property.Name}={value}", compressor with { Multiband = With(bands, property, value) });
                }
            }
        }

        if (sample is DelaySettings delay)
        {
            foreach (int division in new[] { -1, 10, 1000, int.MinValue, int.MaxValue })
            {
                yield return ($"Division={division}", delay with { Division = (DelayDivision)division });
            }
        }
    }

    private static IEnumerable<PropertyInfo> Numeric(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && (p.PropertyType == typeof(double) || p.PropertyType == typeof(double?)));

    /// <summary>Copia el registro (su método de clonado) y cambia la propiedad, aunque sea de solo inicio.</summary>
    private static T With<T>(T record, PropertyInfo property, double value)
        where T : class
    {
        MethodInfo clone = record.GetType().GetMethod("<Clone>$")!;
        var copy = (T)clone.Invoke(record, null)!;
        property.SetValue(copy, value);
        return copy;
    }

    private static float[] Noise(Random random, int frames, float amplitude)
    {
        var samples = new float[frames * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = amplitude * ((float)random.NextDouble() * 2 - 1);
        }

        return samples;
    }

    private static void AssertFinite(float[] samples, string what)
    {
        int bad = Array.FindIndex(samples, s => !float.IsFinite(s));
        Assert.True(bad < 0, $"{what}: muestra {bad} = {(bad < 0 ? 0 : samples[bad])}");
    }
}
