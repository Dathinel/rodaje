using System.Diagnostics;
using Rodaje.Analysis.Sync;
using Xunit.Abstractions;

namespace Rodaje.Analysis.Tests;

/// <summary>Sincronizar por forma de onda: dos grabaciones del mismo momento con desfase conocido.</summary>
public sealed class WaveformSyncTests(ITestOutputHelper output)
{
    /// <summary>Una escena de 70 s: frases con pausas al azar, unas palmadas y un fondo suave.</summary>
    private static float[] Scene(int seed)
    {
        var random = new Random(seed);
        float[] scene = Synth.Seconds(70);
        double t = 0.3;
        while (t < 68)
        {
            double length = 0.6 + (random.NextDouble() * 2.4);
            Synth.Speech(scene, t, t + length, 0.25, random);
            t += length + 0.2 + (random.NextDouble() * 1.5);
        }

        Synth.Clicks(scene, [4.2, 19.7, 33.1, 51.8], 0.8, random);
        Synth.Noise(scene, 0.003, random);
        return scene;
    }

    /// <summary>
    /// Otra cámara: el mismo sonido desde la muestra <paramref name="start"/>, con otra ganancia, un micrófono más opaco
    /// (paso bajo de un polo) y su propio ruido.
    /// </summary>
    private static float[] Camera(float[] scene, int start, double seconds, float gain, int seed)
    {
        var random = new Random(seed);
        var result = new float[(int)(seconds * Synth.Rate)];
        double low = 0;
        for (int i = 0; i < result.Length; i++)
        {
            low += 0.6 * (scene[start + i] - low);
            result[i] = (float)((gain * low) + (0.01 * ((random.NextDouble() * 2) - 1)));
        }

        return result;
    }

    [Theory]
    [InlineData(48_000 * 3, 48_000 * 10, 30, 25)] // la otra empezó 7 s después
    [InlineData(48_000 * 20, 48_000 * 2 + 37, 40, 30)] // la otra empezó antes, con un desfase que no es múltiplo del bloque
    [InlineData(48_000 * 5, 48_000 * 5 + 1, 20, 20)] // una sola muestra
    [InlineData(1_234, 48_000 * 31 + 4_321, 35, 12)] // un trozo corto al final
    public void Encuentra_el_desfase_con_error_de_una_muestra_como_mucho(int referenceStart, int otherStart, double referenceSeconds, double otherSeconds)
    {
        float[] scene = Scene(7);
        float[] reference = scene.AsSpan(referenceStart, (int)(referenceSeconds * Synth.Rate)).ToArray();
        float[] other = Camera(scene, otherStart, otherSeconds, 0.35f, 11);
        long truth = otherStart - referenceStart;

        var watch = Stopwatch.StartNew();
        SyncResult result = WaveformSync.FindOffset(reference, other, Synth.Rate)!;
        watch.Stop();

        output.WriteLine($"verdad {truth}, hallado {result.Lag} (error {result.Lag - truth}), confianza {result.Confidence:0.00}, correlación {result.Correlation:0.000}, {watch.ElapsedMilliseconds} ms");
        Assert.InRange(result.Lag - truth, -1, 1);
        Assert.True(result.IsReliable, $"confianza {result.Confidence}");
        Assert.True(result.Correlation > 0.8, $"correlación {result.Correlation}");
    }

    [Fact]
    public void Dos_grabaciones_distintas_no_dan_un_resultado_fiable()
    {
        float[] a = Scene(1).AsSpan(0, 48_000 * 30).ToArray();
        float[] b = Scene(2).AsSpan(0, 48_000 * 30).ToArray();

        SyncResult? result = WaveformSync.FindOffset(a, b, Synth.Rate);

        output.WriteLine($"confianza {result?.Confidence:0.00}, correlación {result?.Correlation:0.000}");
        Assert.True(result is null || !result.IsReliable || result.Correlation < 0.3);
    }

    [Fact]
    public void El_audio_en_silencio_o_muy_corto_no_se_sincroniza()
    {
        Assert.Null(WaveformSync.FindOffset(new float[100], new float[100], Synth.Rate));
        Assert.False(WaveformSync.FindOffset(new float[48_000], new float[48_000 * 2], Synth.Rate)?.IsReliable ?? false);
    }

    [Fact]
    public void La_huella_por_bloques_coincide_con_la_de_una_sola_vez()
    {
        float[] scene = Scene(3).AsSpan(0, 48_000 * 5).ToArray();
        var whole = new SyncFeature(Synth.Rate, 1);
        var pieces = new SyncFeature(Synth.Rate, 1);
        whole.Push(scene);
        for (int i = 0; i < scene.Length; i += 777)
        {
            pieces.Push(scene.AsSpan(i, Math.Min(777, scene.Length - i)));
        }

        Assert.Equal(whole.Energy, pieces.Energy);
        Assert.Equal(scene.Length, pieces.Frames);
    }
}
