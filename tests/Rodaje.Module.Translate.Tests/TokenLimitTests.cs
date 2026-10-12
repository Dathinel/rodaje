using System.Text.Json;
using Rodaje.Module.Translate.Translation;

namespace Rodaje.Module.Translate.Tests;

/// <summary>Respuestas cortadas por el tope de tokens: se reintentan con más margen y nunca se aceptan a medias.</summary>
public sealed class TokenLimitTests
{
    private static TranslationRequest Request(string target, params string[] texts) =>
        TranslationRequest.Parse(RequestTests.Json(JsonSerializer.Serialize(new
        {
            source = "es",
            target,
            segments = texts.Select((t, i) => new { id = $"clip-{i}", text = t }),
        })));

    [Fact]
    public async Task A_single_subtitle_cut_by_the_limit_is_retried_with_double_the_tokens()
    {
        FakeGenerator model = new((prompt, max) => max < 200
            ? new Generation("[1] नमस्ते, आज हम", true)
            : new Generation("[1] नमस्ते, आज हम समुद्र तट पर जा रहे हैं।", false));
        TranslationResult r = await new SubtitleTranslator(model).TranslateAsync(Request("hi", "Hola, hoy vamos a la playa."), null, CancellationToken.None);

        Assert.Equal(2, model.Prompts.Count);
        Assert.Equal(model.Limits[0] * 2, model.Limits[1]);
        Assert.Equal("नमस्ते, आज हम समुद्र तट पर जा रहे हैं।", r.Segments[0].Text.Replace('\n', ' '));
        Assert.Empty(r.Untranslated);
    }

    [Fact]
    public async Task If_the_retry_is_cut_too_the_original_stays_and_is_reported()
    {
        FakeGenerator model = new((_, _) => new Generation("[1] こんにちは、今日は", true));
        TranslationResult r = await new SubtitleTranslator(model).TranslateAsync(Request("ja", "Hola, hoy vamos a la playa."), null, CancellationToken.None);

        Assert.Equal(2, model.Prompts.Count);
        Assert.Equal("Hola, hoy vamos a la playa.", r.Segments[0].Text);
        Assert.Equal(["clip-0"], r.Untranslated);
    }

    [Fact]
    public async Task In_a_cut_batch_the_last_translation_is_asked_again_alone()
    {
        FakeGenerator model = new((prompt, _) => FakeGenerator.Items(prompt) switch
        {
            [(1, var text)] => new Generation($"[1] SOLO {text}", false),
            var items => new Generation($"[1] {items[0].Text}\n[2] {items[1].Text[..3]}", true),
        });
        TranslationResult r = await new SubtitleTranslator(model).TranslateAsync(Request("ar", "Uno dos tres.", "Cuatro cinco seis.", "Siete."), null, CancellationToken.None);

        Assert.Equal(["Uno dos tres.", "SOLO Cuatro cinco seis.", "SOLO Siete."], r.Segments.Select(s => s.Text));
        Assert.Empty(r.Untranslated);
    }

    [Theory]
    [InlineData("en", 1)]
    [InlineData("zh-Hans", 1)]
    [InlineData("ar", 2)]
    [InlineData("ja", 2)]
    [InlineData("ko", 2)]
    [InlineData("hi", 3)]
    public void Scripts_that_spend_more_tokens_get_a_bigger_limit(string code, int factor)
    {
        TranslationLanguage target = Languages.Find(code)!;
        Assert.Equal(factor, SubtitleTranslator.TokensPerChar(target));
        Assert.Equal(32 + (100 * factor) + 8, SubtitleTranslator.MaxTokens([new string('a', 100)], target));
        Assert.Equal(SubtitleTranslator.MaxBatchTokens, SubtitleTranslator.MaxTokens([new string('a', 2000)], target));
    }

    [Fact]
    public async Task Batches_into_hindi_stay_within_the_token_budget()
    {
        string text = new('a', 150);
        FakeGenerator model = new();
        await new SubtitleTranslator(model).TranslateAsync(Request("hi", text, text, text, text), null, CancellationToken.None);

        Assert.All(model.Limits, l => Assert.True(l <= SubtitleTranslator.MaxBatchTokens));
        Assert.All(model.Prompts, p => Assert.True(32 + FakeGenerator.Items(p).Sum(i => (i.Text.Length * 3) + 8) <= SubtitleTranslator.MaxBatchTokens));
        Assert.Equal(2, model.Prompts.Count);
    }
}
