using System.Diagnostics;
using System.Text.Json;
using Rodaje.Module.Translate.Translation;
using Xunit.Abstractions;

namespace Rodaje.Module.Translate.Tests;

/// <summary>Qwen3-4B de verdad, en el proceso de la prueba: frases conocidas y la velocidad de la fase 7.</summary>
[Collection("Qwen")]
public sealed class ModelTests(ITestOutputHelper output)
{
    private static readonly Lazy<Task<LlamaGenerator>> Shared =
        new(() => LlamaGenerator.LoadAsync(ModelFactAttribute.ModelPath!, "auto", 0, -1, CancellationToken.None));

    private static TranslationRequest Request(IEnumerable<string> texts, string source, string target) =>
        TranslationRequest.Parse(RequestTests.Json(JsonSerializer.Serialize(new
        {
            source,
            target,
            segments = texts.Select((t, i) => new { id = $"clip-{i}", text = t }),
        })));

    [ModelFact]
    public async Task Known_spanish_phrases_come_out_in_english_with_their_key_words()
    {
        LlamaGenerator model = await Shared.Value;
        TranslationResult r = await new SubtitleTranslator(model)
            .TranslateAsync(Request(Corpus.KnownPhrases.Select(p => p.Spanish), "es", "en"), null, CancellationToken.None);
        List<string> misses = [];
        for (int i = 0; i < Corpus.KnownPhrases.Count; i++)
        {
            string english = r.Segments[i].Text.Replace('\n', ' ').ToLowerInvariant();
            output.WriteLine($"{Corpus.KnownPhrases[i].Spanish} → {english}");
            foreach (string[] group in Corpus.KnownPhrases[i].English)
            {
                if (!group.Any(w => english.Contains(w, StringComparison.Ordinal)))
                {
                    misses.Add($"«{english}» sin {string.Join('/', group)}");
                }
            }
        }

        output.WriteLine($"Dispositivo: {r.Device}");
        Assert.Empty(r.Untranslated);
        Assert.True(misses.Count <= 1, string.Join("; ", misses));
    }

    [ModelFact]
    public async Task Other_pairs_work_too()
    {
        LlamaGenerator model = await Shared.Value;
        TranslationResult pt = await new SubtitleTranslator(model).TranslateAsync(Request(["Buenos días.", "Muchas gracias."], "es", "pt-BR"), null, CancellationToken.None);
        TranslationResult es = await new SubtitleTranslator(model).TranslateAsync(Request(["Thank you very much.", "See you tomorrow."], "auto", "es"), null, CancellationToken.None);
        output.WriteLine(string.Join(" | ", pt.Segments.Select(s => s.Text)) + " || " + string.Join(" | ", es.Segments.Select(s => s.Text)));
        Assert.Contains("bom dia", pt.Segments[0].Text.ToLowerInvariant(), StringComparison.Ordinal);
        Assert.Contains("obrigad", pt.Segments[1].Text.ToLowerInvariant(), StringComparison.Ordinal);
        Assert.Contains("gracias", es.Segments[0].Text.ToLowerInvariant(), StringComparison.Ordinal);
        Assert.Contains("mañana", es.Segments[1].Text.ToLowerInvariant(), StringComparison.Ordinal);
    }

    /// <summary>Criterio de la fase 7: 100 subtítulos es→en en ≤90 s, mismos ids y líneas de ≤42 caracteres.</summary>
    [ModelFact]
    [Trait("Categoria", "Rendimiento")]
    public async Task A_hundred_subtitles_in_at_most_90_seconds_keeping_ids_and_line_length()
    {
        LlamaGenerator model = await Shared.Value;
        TranslationRequest request = Request(Corpus.TravelVideo, "es", "en");
        Stopwatch watch = Stopwatch.StartNew();
        TranslationResult r = await new SubtitleTranslator(model).TranslateAsync(request, null, CancellationToken.None);
        double seconds = watch.Elapsed.TotalSeconds;
        output.WriteLine($"100 subtítulos en {seconds:F1} s ({model.Device}); sin traducir: {r.Untranslated.Count}.");
        for (int i = 0; i < 12; i++)
        {
            output.WriteLine($"{Corpus.TravelVideo[i]} → {r.Segments[i].Text.Replace("\n", " / ", StringComparison.Ordinal)}");
        }

        Assert.Equal(request.Segments.Select(s => s.Id), r.Segments.Select(s => s.Id));
        Assert.All(r.Segments, s => Assert.All(s.Text.Split('\n'), l => Assert.InRange(l.Length, 1, 42)));
        Assert.True(r.Untranslated.Count <= 2, string.Join(", ", r.Untranslated));
        Assert.True(seconds <= 90, $"tardó {seconds:F1} s");
    }
}
