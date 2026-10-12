using System.Text.Json;
using Rodaje.Module.Translate.Translation;

namespace Rodaje.Module.Translate.Tests;

/// <summary>El traductor con un modelo falso: lotes, ids, reintentos, líneas y avance, sin el modelo de verdad.</summary>
public sealed class SubtitleTranslatorTests
{
    private static TranslationRequest Request(params string[] texts) =>
        TranslationRequest.Parse(RequestTests.Json(JsonSerializer.Serialize(new
        {
            source = "es",
            target = "en",
            segments = texts.Select((t, i) => new { id = $"clip-{i}", text = t }),
        })));

    [Fact]
    public async Task Keeps_ids_and_order_in_batches_with_previous_lines_as_context()
    {
        string[] texts = [.. Enumerable.Range(1, 20).Select(i => $"Frase número {i}.")];
        FakeGenerator model = new();
        List<double> progress = [];
        TranslationResult r = await new SubtitleTranslator(model).TranslateAsync(Request(texts), new SyncProgress(progress.Add), CancellationToken.None);

        Assert.Equal(texts.Select((_, i) => $"clip-{i}"), r.Segments.Select(s => s.Id));
        Assert.Equal(texts.Select(t => "EN: " + t), r.Segments.Select(s => s.Text));
        Assert.Equal(3, model.Prompts.Count);
        Assert.DoesNotContain("> ", model.Prompts[0], StringComparison.Ordinal);
        Assert.Contains("> Frase número 7.\n> Frase número 8.\n", model.Prompts[1], StringComparison.Ordinal);
        Assert.Equal(8, FakeGenerator.Items(model.Prompts[1]).Count);
        Assert.Empty(r.Untranslated);
        Assert.Equal("es", r.Source);
        Assert.Equal("en", r.Target);
        Assert.Equal("falso", r.Device);
        Assert.Equal(1, progress[^1]);
        Assert.True(progress.SequenceEqual(progress.Order()), "el avance no retrocede");
    }

    [Fact]
    public async Task Empty_subtitles_stay_empty_and_split_the_batches_without_calling_the_model()
    {
        FakeGenerator model = new();
        TranslationResult r = await new SubtitleTranslator(model).TranslateAsync(Request("", "Uno.", " \n ", "Dos.", "Tres."), null, CancellationToken.None);
        Assert.Equal(["", "EN: Uno.", "", "EN: Dos.", "EN: Tres."], r.Segments.Select(s => s.Text));
        Assert.Equal(2, model.Prompts.Count);
        Assert.Contains("Translate this video subtitle", model.Prompts[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_the_model_merges_or_skips_is_asked_again_alone()
    {
        FakeGenerator model = new(prompt => FakeGenerator.Items(prompt) switch
        {
            [(1, _)] single => $"[1] SOLO {single[0].Text}",
            var items => $"[1] {items[0].Text} {items[1].Text}\n[3] {items[2].Text}",
        });
        TranslationResult r = await new SubtitleTranslator(model).TranslateAsync(Request("Hoy vamos", "a la playa.", "Llueve."), null, CancellationToken.None);
        Assert.Equal(["Hoy vamos a la playa.", "SOLO a la playa.", "Llueve."], r.Segments.Select(s => s.Text));
        Assert.Equal(2, model.Prompts.Count);
    }

    [Fact]
    public async Task When_the_model_gives_nothing_the_original_stays_and_is_reported()
    {
        FakeGenerator model = new(_ => "   ");
        TranslationResult r = await new SubtitleTranslator(model).TranslateAsync(Request("Hola.", "Adiós."), null, CancellationToken.None);
        Assert.Equal(["Hola.", "Adiós."], r.Segments.Select(s => s.Text));
        Assert.Equal(["clip-0", "clip-1"], r.Untranslated);
    }

    [Fact]
    public async Task Translations_come_wrapped_in_lines_of_at_most_42_characters()
    {
        FakeGenerator model = new(prompt => string.Join('\n', FakeGenerator.Items(prompt)
            .Select(i => $"[{i.N}] This is a much longer translation that will not fit in a single subtitle line at all")));
        TranslationResult r = await new SubtitleTranslator(model).TranslateAsync(Request("Uno.", "Dos."), null, CancellationToken.None);
        foreach (TranslatedSegment s in r.Segments)
        {
            string[] lines = s.Text.Split('\n');
            Assert.True(lines.Length >= 2, s.Text);
            Assert.All(lines, l => Assert.True(l.Length <= 42, l));
        }
    }

    [Fact]
    public async Task Batches_also_stop_at_the_character_budget()
    {
        string longText = new('a', 400);
        FakeGenerator model = new();
        await new SubtitleTranslator(model).TranslateAsync(Request(longText, longText, longText, longText), null, CancellationToken.None);
        Assert.All(model.Prompts, p => Assert.True(FakeGenerator.Items(p).Sum(i => i.Text.Length) <= SubtitleTranslator.MaxBatchChars));
        Assert.Equal(2, model.Prompts.Count);
    }

    [Fact]
    public async Task Cancelling_stops_before_the_next_batch()
    {
        using CancellationTokenSource cts = new();
        int calls = 0;
        FakeGenerator model = new(prompt =>
        {
            if (++calls == 1)
            {
                cts.Cancel();
            }

            return FakeGenerator.Obedient(prompt);
        });
        string[] texts = [.. Enumerable.Range(0, 30).Select(i => $"Texto {i}")];
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SubtitleTranslator(model).TranslateAsync(Request(texts), null, cts.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void The_result_serializes_as_documented_in_phase_7()
    {
        TranslationResult r = new("es", "en", "cpu", [new TranslatedSegment("a", "Hi")], []);
        string json = JsonSerializer.Serialize(r, TranslationJson.Default.TranslationResult);
        Assert.Equal("""{"source":"es","target":"en","device":"cpu","segments":[{"id":"a","text":"Hi"}],"untranslated":[]}""", json);
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
