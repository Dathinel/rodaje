using System.Diagnostics;
using Rodaje.Analysis;
using Rodaje.Analysis.Ducking;
using Xunit.Abstractions;

namespace Rodaje.Module.AudioAi.Tests;

/// <summary>
/// Silero VAD contra una verdad conocida: frases reales de FLEURS separadas por pausas de 0,8–2 s con ruido de fondo. La
/// verdad de cada frase es su tramo con energía en la versión limpia (sin los silencios propios de la grabación).
/// </summary>
public class VoiceDetectionTests(ITestOutputHelper output)
{
    private const int Rate = 16_000;

    private static (float[] Clean, List<TimeRange> Truth) Build(List<float[]> phrases, int seed)
    {
        var random = new Random(seed);
        var clean = new List<float>();
        var truth = new List<TimeRange>();
        foreach (float[] phrase in phrases)
        {
            clean.AddRange(new float[(int)(Rate * (0.8 + (1.2 * random.NextDouble())))]);
            (int a, int b) = Voiced(phrase);
            truth.Add(new TimeRange((double)(clean.Count + a) / Rate, (double)(clean.Count + b) / Rate));
            clean.AddRange(phrase);
        }

        clean.AddRange(new float[Rate]);
        return ([.. clean], truth);
    }

    /// <summary>
    /// Primera y última muestra de los bloques de 50 ms con voz en la frase limpia: 15 dB sobre su piso (percentil 10;
    /// FLEURS trae silencios grabados con clics y respiraciones) y a menos de 25 dB del bloque más fuerte.
    /// </summary>
    private static (int Start, int End) Voiced(float[] x)
    {
        const int Block = Rate / 20;
        int blocks = x.Length / Block;
        var level = new double[blocks];
        for (int i = 0; i < blocks; i++)
        {
            double sum = 0;
            for (int j = 0; j < Block; j++)
            {
                sum += x[(i * Block) + j] * (double)x[(i * Block) + j];
            }

            level[i] = 10 * Math.Log10((sum / Block) + 1e-12);
        }

        double floor = level.Order().ElementAt(blocks / 10);
        double threshold = Math.Max(floor + 15, level.Max() - 25);
        int first = Array.FindIndex(level, l => l > threshold), last = Array.FindLastIndex(level, l => l > threshold);
        return (first * Block, (last + 1) * Block);
    }

    private static async Task<float[]> ProbabilitiesAsync(AudioAiFunctions functions, float[] audio, CancellationToken token = default)
    {
        var probabilities = new float[Vad.SileroVad.WindowCount(audio.Length)];
        await functions.DetectVoiceAsync(() => audio, () => probabilities, audio.Length, _ => ValueTask.CompletedTask, token);
        return probabilities;
    }

    /// <summary>Exactitud por bloques de 10 ms, sensibilidad, precisión y error de los bordes de inicio (mediana).</summary>
    private static (double Accuracy, double Recall, double Precision, double StartErrorMs) Score(List<TimeRange> truth, List<TimeRange> found, double duration)
    {
        int blocks = (int)(duration * 100);
        int tp = 0, fp = 0, fn = 0, tn = 0;
        for (int i = 0; i < blocks; i++)
        {
            double t = (i + 0.5) / 100;
            bool real = truth.Any(r => r.Contains(t)), said = found.Any(r => r.Contains(t));
            if (real && said) tp++;
            else if (said) fp++;
            else if (real) fn++;
            else tn++;
        }

        List<double> starts = [.. truth.Select(r => found.Count == 0 ? 10.0 : found.Min(f => Math.Abs(f.Start - r.Start))).Order()];
        return ((double)(tp + tn) / blocks, (double)tp / Math.Max(1, tp + fn), (double)tp / Math.Max(1, tp + fp), starts[starts.Count / 2] * 1000);
    }

    [Theory]
    [InlineData("Pink", 10)]
    [InlineData("Pink", 0)]
    [InlineData("White", 5)]
    [InlineData("Hum", 5)]
    public async Task Finds_speech_against_known_truth(string noise, double snr)
    {
        if (!Corpus.HasModels || Corpus.Fleurs(20) is not { } phrases)
        {
            output.WriteLine(Corpus.Missing);
            return;
        }

        (float[] clean, List<TimeRange> truth) = Build(phrases, 11);
        float[] audio = Noise.Mix(clean, Noise.Make(Enum.Parse<Noise.Kind>(noise), clean.Length, Rate, 5), snr);
        double duration = (double)audio.Length / Rate;
        using var functions = new AudioAiFunctions(Corpus.Models);
        float[] probabilities = await ProbabilitiesAsync(functions, audio);
        List<TimeRange> silero = VoiceProbability.ToRanges(probabilities, 0.032, duration);

        IVoiceActivitySession energy = new EnergyVoiceDetector().Begin(Rate, 1);
        energy.Push(audio);
        List<TimeRange> baseline = [.. await energy.FinishAsync()];

        var s = Score(truth, silero, duration);
        var e = Score(truth, baseline, duration);
        output.WriteLine($"{noise} a {snr} dB, {duration:0} s: Silero exactitud {s.Accuracy:P1}, sensibilidad {s.Recall:P1}, precisión {s.Precision:P1}, inicio ±{s.StartErrorMs:0} ms | " +
            $"energía {e.Accuracy:P1}, {e.Recall:P1}, {e.Precision:P1}, ±{e.StartErrorMs:0} ms");
        Assert.True(s.Accuracy >= 0.9, $"Exactitud {s.Accuracy:P1}");
        Assert.True(s.Recall >= 0.9, $"Sensibilidad {s.Recall:P1}");
    }

    [Fact]
    public async Task Silence_and_pure_noise_have_no_speech()
    {
        if (!Corpus.HasModels)
        {
            output.WriteLine(Corpus.Missing);
            return;
        }

        using var functions = new AudioAiFunctions(Corpus.Models);
        float[] silence = new float[Rate * 10];
        float[] hum = Noise.Make(Noise.Kind.Hum, Rate * 10, Rate, 2);
        for (int i = 0; i < hum.Length; i++)
        {
            hum[i] *= 0.05f;
        }

        Assert.Empty(VoiceProbability.ToRanges(await ProbabilitiesAsync(functions, silence), 0.032, 10));
        Assert.Empty(VoiceProbability.ToRanges(await ProbabilitiesAsync(functions, hum), 0.032, 10));
    }

    [Fact]
    [Trait("Categoria", "TiempoReal")]
    public async Task Speed_in_times_real_time()
    {
        if (!Corpus.HasModels || Corpus.Fleurs(40) is not { } phrases)
        {
            output.WriteLine(Corpus.Missing);
            return;
        }

        (float[] clean, _) = Build(phrases, 3);
        float[] audio = Noise.Mix(clean, Noise.Make(Noise.Kind.Pink, clean.Length, Rate, 1), 10);
        using var functions = new AudioAiFunctions(Corpus.Models);
        await ProbabilitiesAsync(functions, audio.AsSpan(0, Rate).ToArray());
        var watch = Stopwatch.StartNew();
        await ProbabilitiesAsync(functions, audio);
        double speed = audio.Length / (double)Rate / watch.Elapsed.TotalSeconds;
        output.WriteLine($"Silero VAD: {audio.Length / Rate} s en {watch.Elapsed.TotalSeconds:0.00} s = {speed:0}× tiempo real");
        Assert.True(speed >= 100, $"{speed:0}×");
    }

    [Fact]
    public async Task Cancellation_stops_between_steps()
    {
        if (!Corpus.HasModels)
        {
            output.WriteLine(Corpus.Missing);
            return;
        }

        using var functions = new AudioAiFunctions(Corpus.Models);
        float[] audio = Noise.Make(Noise.Kind.White, Rate * 600, Rate, 4);
        var probabilities = new float[Vad.SileroVad.WindowCount(audio.Length)];
        using var cancel = new CancellationTokenSource();
        int steps = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => functions.DetectVoiceAsync(() => audio, () => probabilities, audio.Length, _ =>
        {
            if (++steps == 2)
            {
                cancel.Cancel();
            }

            return ValueTask.CompletedTask;
        }, cancel.Token).AsTask());
        Assert.Equal(2, steps);
    }
}
