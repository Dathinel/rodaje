using Rodaje.Audio.Processing;
using Rodaje.Audio.Tests.Processing;
using Rodaje.Engine.Tests.Timeline;

namespace Rodaje.Audio.Tests.Boundaries;

/// <summary>
/// Tarea 215: cada inserción, y la pila entera con sidechain, a 44,1/48/96 kHz y a frecuencias no estándar, procesada en
/// bloques de cualquier tamaño (también 0 y 1 cuadro) da muestra a muestra lo mismo que de una vez, sin NaN en la salida
/// ni en la lectura de dinámica. Semillas de <see cref="StressSeeds"/>.
/// </summary>
public sealed class EffectChainBoundaryTests
{
    private const int Frames = 6_000;

    public static TheoryData<double> Rates() => [8_000, 11_025, 22_050, 37_800, 44_100, 48_000, 88_200, 96_000, 192_000];

    [Theory]
    [MemberData(nameof(Rates))]
    public void CadaInsercion_PorBloquesDaLoMismoQueDeUnaVez(double rate)
    {
        for (int index = 0; index < ExtremeSettingsTests.Samples.Count; index++)
        {
            AudioProcessorSettings sample = ExtremeSettingsTests.Samples[index];
            AssertBlocksMatch(new AudioEffectsSettings([new AudioInsert("x", sample)]), rate, StressSeeds.Seed(215, 6, (long)rate, index),
                sample.GetType().Name);
        }
    }

    [Theory]
    [MemberData(nameof(Rates))]
    public void LaPilaEnteraConSidechain_PorBloquesDaLoMismoQueDeUnaVez(double rate)
    {
        var inserts = new List<AudioInsert>();
        for (int i = 0; i < ExtremeSettingsTests.Samples.Count; i++)
        {
            inserts.Add(new AudioInsert($"i{i}", ExtremeSettingsTests.Samples[i]));
        }

        AssertBlocksMatch(new AudioEffectsSettings(inserts), rate, StressSeeds.Seed(215, 7, (long)rate), "la pila entera", withKey: true);
    }

    private static void AssertBlocksMatch(AudioEffectsSettings settings, double rate, int seed, string what, bool withKey = false)
    {
        var random = new Random(seed);
        float[] input = new float[Frames * 2];
        float[] key = new float[Frames * 2];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (float)((0.7 * Math.Sin(i * 0.013)) + (0.3 * ((random.NextDouble() * 2) - 1)));
            key[i] = (float)((random.NextDouble() * 2) - 1);
        }

        float[] whole = (float[])input.Clone();
        var once = new AudioEffectChain(settings, rate);
        once.Process(whole, withKey ? key : default);

        float[] pieces = (float[])input.Clone();
        var chain = new AudioEffectChain(settings, rate);
        int at = 0;
        while (at < Frames)
        {
            int n = Math.Min(Frames - at, random.Next(0, 600));
            chain.Process(pieces.AsSpan(at * 2, n * 2), withKey ? key.AsSpan(at * 2, n * 2) : default);
            at += n;
            DynamicsReading r = chain.Reading;
            Assert.True(float.IsFinite(r.InputPeak) && float.IsFinite(r.OutputPeak) && float.IsFinite(r.ReductionDb) && float.IsFinite(r.LimiterReductionDb),
                $"semilla {seed}, {what} a {rate} Hz: lectura {r}");
        }

        int bad = Array.FindIndex(whole, s => !float.IsFinite(s));
        Assert.True(bad < 0, $"semilla {seed}, {what} a {rate} Hz: muestra {bad} no finita");
        int diff = -1;
        for (int i = 0; i < whole.Length && diff < 0; i++)
        {
            if (whole[i] != pieces[i])
            {
                diff = i;
            }
        }

        Assert.True(diff < 0, $"semilla {seed}, {what} a {rate} Hz: primera diferencia en la muestra {diff} ({(diff < 0 ? 0 : whole[diff])} contra {(diff < 0 ? 0 : pieces[diff])})");
    }
}
