namespace Rodaje.Module.Translate.Translation;

/// <summary>El modelo de lenguaje, visto desde el traductor: completa un prompt hasta el fin de turno.</summary>
internal interface ITextGenerator
{
    /// <summary>Dónde corre: <c>vulkan</c>, <c>vulkan+cpu</c> (capas repartidas) o <c>cpu</c>.</summary>
    string Device { get; }

    Task<string> GenerateAsync(string prompt, int maxTokens, CancellationToken cancellationToken);
}

/// <summary>
/// Traduce una lista de subtítulos por lotes consecutivos (con las líneas anteriores como contexto), conserva ids y
/// orden, vuelve a pedir sueltos los que el modelo juntó, saltó o repitió, y parte cada traducción en líneas de
/// subtítulo. Un subtítulo vacío sale vacío sin pasar por el modelo.
/// </summary>
internal sealed class SubtitleTranslator(ITextGenerator generator)
{
    /// <summary>Tope de caracteres de origen por lote: con el prompt y la respuesta cabe en el contexto de 3072 tokens.</summary>
    public const int MaxBatchChars = 1000;

    public const int ContextLines = 2;

    /// <summary>Tope de tokens de la respuesta: un subtítulo no da más tokens que caracteres, más el número y el salto.</summary>
    public static int MaxTokens(IEnumerable<string> texts) => Math.Min(1200, 32 + texts.Sum(t => t.Length + 8));

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        IReadOnlyList<SourceSegment> segments = request.Segments;
        string[] output = new string[segments.Count];
        List<string> untranslated = [];
        long total = Math.Max(1, segments.Sum(s => (long)s.Text.Length));
        long done = 0;

        int i = 0;
        while (i < segments.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (LineWrapper.Normalize(segments[i].Text).Length == 0)
            {
                output[i] = "";
                i++;
                continue;
            }

            // El lote: subtítulos con texto seguidos, hasta BatchSize o MaxBatchChars.
            List<int> batch = [];
            int chars = 0;
            for (int j = i; j < segments.Count && batch.Count < request.BatchSize; j++)
            {
                string text = LineWrapper.Normalize(segments[j].Text);
                if (text.Length == 0 || (batch.Count > 0 && chars + text.Length > MaxBatchChars))
                {
                    break;
                }

                batch.Add(j);
                chars += text.Length;
            }

            List<string> context = [.. Enumerable.Range(0, i).Reverse().Select(k => LineWrapper.Normalize(segments[k].Text))
                .Where(t => t.Length > 0).Take(ContextLines).Reverse()];
            List<string> texts = [.. batch.Select(k => LineWrapper.Normalize(segments[k].Text))];
            string?[] translated = batch.Count == 1
                ? [await SingleAsync(texts[0], request, cancellationToken).ConfigureAwait(false)]
                : Prompt.Parse(await generator.GenerateAsync(Prompt.Batch(texts, context, request.Source, request.Target), MaxTokens(texts), cancellationToken)
                    .ConfigureAwait(false), batch.Count);

            for (int b = 0; b < batch.Count; b++)
            {
                string? text = translated[b];
                if (string.IsNullOrEmpty(text) && batch.Count > 1)
                {
                    text = await SingleAsync(texts[b], request, cancellationToken).ConfigureAwait(false);
                }

                if (string.IsNullOrEmpty(text))
                {
                    // Sin traducción no se pierde el subtítulo: queda el original y la app lo avisa.
                    untranslated.Add(segments[batch[b]].Id);
                    text = texts[b];
                }

                output[batch[b]] = LineWrapper.Wrap(text, request.MaxLineChars);
                done += segments[batch[b]].Text.Length;
            }

            progress?.Report((double)done / total);
            i = batch[^1] + 1;
        }

        progress?.Report(1);
        return new TranslationResult(
            request.Source?.Code ?? "auto",
            request.Target.Code,
            generator.Device,
            [.. segments.Select((s, k) => new TranslatedSegment(s.Id, output[k]))],
            untranslated);
    }

    private async Task<string> SingleAsync(string text, TranslationRequest request, CancellationToken cancellationToken) =>
        Prompt.ParseSingle(await generator.GenerateAsync(Prompt.Single(text, request.Source, request.Target), MaxTokens([text]), cancellationToken)
            .ConfigureAwait(false));
}
