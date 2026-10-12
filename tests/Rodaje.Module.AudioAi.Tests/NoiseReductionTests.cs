using System.Diagnostics;
using Xunit.Abstractions;

namespace Rodaje.Module.AudioAi.Tests;

/// <summary>
/// DeepFilterNet3 sobre voz real de banda completa (LibriVox, 48 kHz) con ruido sintético a SNR conocida: criterio de la
/// fase 7, +10 dB de SNR y ≥10× tiempo real en CPU.
/// </summary>
public class NoiseReductionTests(ITestOutputHelper output)
{
    private const int Rate = 48_000;

    internal static async Task<float[]> ReduceAsync(AudioAiFunctions functions, float[] input, int channels, CancellationToken token = default)
    {
        var result = new float[input.Length];
        await functions.ReduceNoiseAsync(() => input, () => result, channels, input.Length / channels, _ => ValueTask.CompletedTask, token);
        return result;
    }

    internal static float[]? Voice(int seconds)
    {
        if (Corpus.Voice48k is not { } path)
        {
            return null;
        }

        (float[] samples, int rate) = Corpus.ReadWav(path);
        Assert.Equal(Rate, rate);

        // Se salta la presentación de LibriVox (los primeros 20 s).
        return samples.AsSpan(20 * Rate, seconds * Rate).ToArray();
    }

    /// <summary>
    /// SI-SNR (invariante a la escala) como medida principal: DeepFilterNet3 baja la voz ~2 dB cuando hay mucho ruido, y la
    /// SNR simple cuenta eso como error aunque se oiga igual. El +10 dB del criterio se cumple desde 0 dB de entrada.
    /// </summary>
    [Theory]
    [InlineData("White", 0, 10)]
    [InlineData("Pink", 0, 10)]
    [InlineData("Hum", 0, 8)]
    [InlineData("White", 5, 9)]
    [InlineData("Pink", 5, 8)]
    [InlineData("Hum", 5, 7)]
    public async Task Improves_snr_on_real_voice(string noise, double inputSnr, double minimumGain)
    {
        if (!Corpus.HasModels || Voice(30) is not { } clean)
        {
            output.WriteLine(Corpus.Missing);
            return;
        }

        Noise.Kind kind = Enum.Parse<Noise.Kind>(noise);
        float[] noisy = Noise.Mix(clean, Noise.Make(kind, clean.Length, Rate, seed: 7), inputSnr);
        using var functions = new AudioAiFunctions(Corpus.Models);
        float[] enhanced = await ReduceAsync(functions, noisy, 1);

        double before = Noise.Snr(clean, noisy), after = Noise.Snr(clean, enhanced);
        double siBefore = Noise.SiSnr(clean, noisy), siAfter = Noise.SiSnr(clean, enhanced);
        output.WriteLine($"{kind} a {inputSnr} dB: SI-SNR {siBefore:0.0} → {siAfter:0.0} dB (+{siAfter - siBefore:0.0}); SNR {before:0.0} → {after:0.0} dB (+{after - before:0.0})");
        Assert.True(siAfter - siBefore >= minimumGain, $"SI-SNR +{siAfter - siBefore:0.0} dB");
    }

    /// <summary>Doce hablantes de FLEURS (16 kHz llevado a 48) con ruido rosa a 0 dB: la mejora media no depende de una sola voz.</summary>
    [Fact]
    public async Task Improves_snr_across_speakers()
    {
        if (!Corpus.HasModels || Corpus.Fleurs(12) is not { } phrases)
        {
            output.WriteLine(Corpus.Missing);
            return;
        }

        using var functions = new AudioAiFunctions(Corpus.Models);
        var gains = new List<double>();
        int seed = 1;
        foreach (float[] phrase in phrases)
        {
            float[] clean = Corpus.Upsample3(phrase);
            float[] noisy = Noise.Mix(clean, Noise.Make(Noise.Kind.Pink, clean.Length, Rate, seed++), 0);
            float[] enhanced = await ReduceAsync(functions, noisy, 1);
            gains.Add(Noise.SiSnr(clean, enhanced) - Noise.SiSnr(clean, noisy));
        }

        output.WriteLine($"FLEURS, 12 frases con ruido rosa a 0 dB: SI-SNR +{gains.Average():0.0} dB de media (mín. +{gains.Min():0.0}, máx. +{gains.Max():0.0})");
        Assert.True(gains.Average() >= 10, $"+{gains.Average():0.0} dB");
    }

    [Fact]
    public async Task Clean_voice_passes_almost_untouched()
    {
        if (!Corpus.HasModels || Voice(20) is not { } clean)
        {
            output.WriteLine(Corpus.Missing);
            return;
        }

        using var functions = new AudioAiFunctions(Corpus.Models);
        float[] enhanced = await ReduceAsync(functions, clean, 1);
        double si = Noise.SiSnr(clean, enhanced);
        output.WriteLine($"Voz limpia: SI-SNR de la salida frente a la entrada {si:0.0} dB");
        Assert.True(si >= 15, $"La voz limpia cambió demasiado: {si:0.0} dB");
    }

    [Fact]
    public async Task Stereo_with_different_channels_is_processed_per_channel_and_dual_mono_once()
    {
        if (!Corpus.HasModels || Voice(15) is not { } clean)
        {
            output.WriteLine(Corpus.Missing);
            return;
        }

        float[] noise = Noise.Make(Noise.Kind.White, clean.Length, Rate, 3);
        float[] left = Noise.Mix(clean, noise, 5);
        float[] right = Noise.Mix(clean, Noise.Make(Noise.Kind.Pink, clean.Length, Rate, 4), 5);
        var stereo = new float[clean.Length * 2];
        var dual = new float[clean.Length * 2];
        for (int i = 0; i < clean.Length; i++)
        {
            stereo[2 * i] = left[i];
            stereo[(2 * i) + 1] = right[i];
            dual[2 * i] = dual[(2 * i) + 1] = left[i];
        }

        using var functions = new AudioAiFunctions(Corpus.Models);
        var outStereo = new float[stereo.Length];
        var outDual = new float[dual.Length];
        NoiseResult a = await functions.ReduceNoiseAsync(() => stereo, () => outStereo, 2, clean.Length, _ => ValueTask.CompletedTask, default);
        NoiseResult b = await functions.ReduceNoiseAsync(() => dual, () => outDual, 2, clean.Length, _ => ValueTask.CompletedTask, default);
        Assert.Equal(2, a.ChannelsProcessed);
        Assert.Equal(1, b.ChannelsProcessed);
        float[] l = [.. Enumerable.Range(0, clean.Length).Select(i => outStereo[2 * i])];
        float[] r = [.. Enumerable.Range(0, clean.Length).Select(i => outStereo[(2 * i) + 1])];
        double gainLeft = Noise.SiSnr(clean, l) - Noise.SiSnr(clean, left), gainRight = Noise.SiSnr(clean, r) - Noise.SiSnr(clean, right);
        output.WriteLine($"Estéreo: +{gainLeft:0.0} dB a la izquierda (blanco), +{gainRight:0.0} dB a la derecha (rosa)");
        Assert.True(gainLeft >= 7 && gainRight >= 7);
        Assert.Equal(outDual.Where((_, i) => i % 2 == 0), outDual.Where((_, i) => i % 2 == 1));
    }

    [Fact]
    public async Task Cancellation_stops_within_one_chunk()
    {
        if (!Corpus.HasModels || Voice(120) is not { } clean)
        {
            output.WriteLine(Corpus.Missing);
            return;
        }

        using var functions = new AudioAiFunctions(Corpus.Models);
        var result = new float[clean.Length];
        using var cancel = new CancellationTokenSource();
        long cancelledAt = 0;
        int steps = 0;
        Task run = functions.ReduceNoiseAsync(() => clean, () => result, 1, clean.Length, _ =>
        {
            if (++steps == 1)
            {
                cancelledAt = Stopwatch.GetTimestamp();
                cancel.Cancel();
            }

            return ValueTask.CompletedTask;
        }, cancel.Token).AsTask();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        double ms = Stopwatch.GetElapsedTime(cancelledAt).TotalMilliseconds;
        output.WriteLine($"Cancelado tras el primer tramo en {ms:0.0} ms");
        Assert.Equal(1, steps);
        Assert.True(ms < 1000, $"{ms:0} ms");
    }

    [Fact]
    [Trait("Categoria", "TiempoReal")]
    public async Task Speed_in_times_real_time()
    {
        if (!Corpus.HasModels || Voice(300) is not { } clean)
        {
            output.WriteLine(Corpus.Missing);
            return;
        }

        float[] noisy = Noise.Mix(clean, Noise.Make(Noise.Kind.Pink, clean.Length, Rate, 9), 5);
        using var functions = new AudioAiFunctions(Corpus.Models);
        await ReduceAsync(functions, noisy.AsSpan(0, Rate).ToArray(), 1); // carga y primera inferencia
        var watch = Stopwatch.StartNew();
        var result = new float[noisy.Length];
        NoiseResult r = await functions.ReduceNoiseAsync(() => noisy, () => result, 1, noisy.Length, _ => ValueTask.CompletedTask, default);
        double speed = noisy.Length / (double)Rate / watch.Elapsed.TotalSeconds;
        output.WriteLine($"{r.Device}: {noisy.Length / Rate} s de voz en {watch.Elapsed.TotalSeconds:0.00} s = {speed:0.0}× tiempo real");
        Assert.True(speed >= 10, $"{speed:0.0}×");
    }
}
