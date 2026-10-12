using System.Diagnostics;
using Rodaje.Module.Voice.Synthesis;
using Rodaje.Module.Voice.Text;
using Xunit.Abstractions;

namespace Rodaje.Module.Voice.Tests;

/// <summary>Solo corre si están los modelos (<c>build\preparar-modelos.ps1</c>).</summary>
public sealed class ModelFactAttribute : FactAttribute
{
    public ModelFactAttribute()
    {
        if (Models.Root is null) Skip = "Omitida: faltan los modelos de voz; ejecuta build\\preparar-modelos.ps1.";
    }
}

internal static class Models
{
    public static readonly string? Root = Voices.FindRoot();

    private static readonly Lazy<Synthesizer> Shared = new(() => new Synthesizer(Root!));

    public static Synthesizer Synth => Shared.Value;

    public static double Seconds(SpeechResult r) => (double)r.Audio.Length / r.SampleRate;
}

[Collection("Kokoro")]
public sealed class SynthesisTests(ITestOutputHelper output)
{
    private const string Spanish = "Rodaje es un editor de video para Windows. Corta, ajusta el color y exporta en segundos.";

    [ModelFact]
    public void Every_language_speaks_with_its_voices()
    {
        (Language L, string Voice, string Text)[] cases =
        [
            (Language.Spanish, "ef_dora", Spanish),
            (Language.SpanishLatam, "em_alex", "Hola, ¿cómo estás? Hoy vamos a grabar un video."),
            (Language.PortugueseBrazil, "pf_dora", "Olá, tudo bem? Hoje vamos gravar um vídeo novo."),
            (Language.PortugueseBrazil, "pm_alex", "A cidade de São Paulo é muito grande."),
            (Language.EnglishUs, "af_heart", "Hello there! This is a quick test of the voice."),
            (Language.EnglishUk, "bm_george", "Good morning. The weather is lovely today."),
        ];
        foreach (var (l, voice, text) in cases)
        {
            SpeechResult r = Models.Synth.Speak(new SpeechRequest(text, l, voice), null, CancellationToken.None);
            double seconds = Models.Seconds(r);
            output.WriteLine($"{Languages.Id(l)} {voice}: {seconds:F2} s, {r.Segments.Count} frases — {r.Phonemes}");
            // Entre 30 y 150 ms por carácter: ni silencio ni basura.
            Assert.InRange(seconds, text.Length * 0.03, text.Length * 0.15);
            Assert.True(r.Audio.Max(Math.Abs) is > 0.05f and <= 1.5f);
            Assert.Equal(r.Words.Count, r.Words.Select(w => w.Text).Count());
            Assert.All(r.Words, w => Assert.True(w.End > w.Start));
        }
    }

    [ModelFact]
    public void Speed_changes_duration_and_pitch_keeps_it()
    {
        double Duration(double speed, double pitch) =>
            Models.Seconds(Models.Synth.Speak(new SpeechRequest(Spanish, Language.Spanish, "ef_dora", speed, pitch, SentencePause: 0), null, CancellationToken.None));
        double normal = Duration(1, 0), fast = Duration(2, 0), slow = Duration(0.5, 0), high = Duration(1, 4), low = Duration(1, -4);
        output.WriteLine($"1×: {normal:F2} s; 2×: {fast:F2} s; 0,5×: {slow:F2} s; +4 st: {high:F2} s; −4 st: {low:F2} s");
        Assert.InRange(fast / normal, 0.4, 0.65);
        Assert.InRange(slow / normal, 1.6, 2.4);
        Assert.InRange(high / normal, 0.85, 1.15);
        Assert.InRange(low / normal, 0.85, 1.15);
    }

    [ModelFact]
    public void Pauses_are_exact_silences_between_sentences()
    {
        SpeechResult a = Models.Synth.Speak(new SpeechRequest("Uno. Dos.", Language.Spanish, "ef_dora", SentencePause: 0), null, CancellationToken.None);
        SpeechResult b = Models.Synth.Speak(new SpeechRequest("Uno. [pausa 1.5] Dos.", Language.Spanish, "ef_dora", SentencePause: 0), null, CancellationToken.None);
        Assert.Equal(1.5, Models.Seconds(b) - Models.Seconds(a), 2);
        Assert.Equal(2, b.Segments.Count);
        Assert.Equal(1.5, b.Segments[1].Start - b.Segments[0].End, 2);
    }

    [ModelFact]
    public void Cancelling_stops_within_a_second()
    {
        string text = string.Join(" ", Enumerable.Repeat(Spanish, 12));
        using CancellationTokenSource cts = new();
        Stopwatch watch = Stopwatch.StartNew();
        Task task = Task.Run(() => Models.Synth.Speak(new SpeechRequest(text, Language.Spanish, "ef_dora"), null, cts.Token));
        Thread.Sleep(600);
        Stopwatch afterCancel = Stopwatch.StartNew();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => task.GetAwaiter().GetResult());
        output.WriteLine($"Cancelado {afterCancel.ElapsedMilliseconds} ms después de pedirlo ({watch.ElapsedMilliseconds} ms en total).");
        Assert.True(afterCancel.Elapsed < TimeSpan.FromSeconds(1), $"tardó {afterCancel.ElapsedMilliseconds} ms");
    }

    [ModelFact]
    [Trait("Categoria", "TiempoReal")]
    public void Speed_in_real_time_on_the_cpu()
    {
        string text = string.Join(" ", Enumerable.Repeat(Spanish, 4));
        Models.Synth.Speak(new SpeechRequest("Hola.", Language.Spanish, "ef_dora"), null, CancellationToken.None);
        List<double> factors = [];
        for (int i = 0; i < 3; i++)
        {
            Stopwatch w = Stopwatch.StartNew();
            SpeechResult r = Models.Synth.Speak(new SpeechRequest(text, Language.Spanish, "ef_dora"), null, CancellationToken.None);
            factors.Add(Models.Seconds(r) / w.Elapsed.TotalSeconds);
        }
        factors.Sort();
        output.WriteLine($"× tiempo real (CPU, {KokoroEngine.DefaultThreads()} hilos): mediana {factors[1]:F1}, mín {factors[0]:F1}, máx {factors[2]:F1}");
        // El criterio de la fase es ≥5× con el equipo libre; con otros procesos compitiendo por la CPU baja.
        Assert.True(factors[1] >= (Environment.GetEnvironmentVariable("RODAJE_PRUEBAS_LARGAS") == "1" ? 5 : 1), $"{factors[1]:F1}× tiempo real");
    }
}
