namespace Rodaje.Module.Translate.Translation;

/// <summary>Lo que escribió el modelo y si paró por llegar al tope de tokens (la respuesta puede estar cortada).</summary>
internal readonly record struct Generation(string Text, bool HitLimit);

/// <summary>El modelo de lenguaje, visto desde el traductor: completa un prompt hasta el fin de turno o el tope de tokens.</summary>
internal interface ITextGenerator
{
    /// <summary>Dónde corre: <c>vulkan</c>, <c>vulkan+cpu</c> (capas repartidas) o <c>cpu</c>.</summary>
    string Device { get; }

    Task<Generation> GenerateAsync(string prompt, int maxTokens, CancellationToken cancellationToken);
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

    /// <summary>Tope de tokens de la respuesta de un lote: con el prompt (~800 tokens) cabe en el contexto.</summary>
    public const int MaxBatchTokens = 1200;

    /// <summary>Tope del reintento de un subtítulo suelto: su prompt (≤500 caracteres) y esto caben en 3072 tokens.</summary>
    public const int MaxRetryTokens = 2048;

    /// <summary>
    /// Tope de tokens de la respuesta: por carácter de origen, uno en escrituras latinas, cirílicas o chinas; en
    /// devanagari, árabe, japonés y coreano Qwen gasta más tokens por palabra, y la traducción se cortaba a mitad de
    /// frase. Más el número y el salto de cada subtítulo.
    /// </summary>
    public static int MaxTokens(IEnumerable<string> texts, TranslationLanguage target) =>
        Math.Min(MaxBatchTokens, EstimatedTokens(texts, target));

    /// <summary>Tokens por carácter de origen que se reservan para la respuesta en <paramref name="target"/>.</summary>
    public static int TokensPerChar(TranslationLanguage target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.Code switch
        {
            "hi" => 3,
            "ar" or "ja" or "ko" => 2,
            _ => 1,
        };
    }

    private static int EstimatedTokens(IEnumerable<string> texts, TranslationLanguage target)
    {
        int factor = TokensPerChar(target);
        return 32 + texts.Sum(t => (t.Length * factor) + 8);
    }

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

            // El lote: subtítulos con texto seguidos, hasta BatchSize, MaxBatchChars o lo que quepa en MaxBatchTokens.
            List<int> batch = [];
            int chars = 0;
            int tokens = 32;
            int factor = TokensPerChar(request.Target);
            for (int j = i; j < segments.Count && batch.Count < request.BatchSize; j++)
            {
                string text = LineWrapper.Normalize(segments[j].Text);
                int more = (text.Length * factor) + 8;
                if (text.Length == 0 || (batch.Count > 0 && (chars + text.Length > MaxBatchChars || tokens + more > MaxBatchTokens)))
                {
                    break;
                }

                batch.Add(j);
                chars += text.Length;
                tokens += more;
            }

            List<string> context = [.. Enumerable.Range(0, i).Reverse().Select(k => LineWrapper.Normalize(segments[k].Text))
                .Where(t => t.Length > 0).Take(ContextLines).Reverse()];
            List<string> texts = [.. batch.Select(k => LineWrapper.Normalize(segments[k].Text))];
            string?[] translated = batch.Count == 1
                ? [await SingleAsync(texts[0], request, cancellationToken).ConfigureAwait(false)]
                : await BatchAsync(texts, context, request, cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// Un lote. Si el modelo llegó al tope, la última traducción que escribía puede estar cortada: se descarta y se pide
    /// suelta, igual que las que no llegó a escribir.
    /// </summary>
    private async Task<string?[]> BatchAsync(List<string> texts, List<string> context, TranslationRequest request, CancellationToken cancellationToken)
    {
        Generation g = await generator.GenerateAsync(Prompt.Batch(texts, context, request.Source, request.Target), MaxTokens(texts, request.Target), cancellationToken)
            .ConfigureAwait(false);
        string?[] parsed = Prompt.Parse(g.Text, texts.Count);
        if (g.HitLimit && Array.FindLastIndex(parsed, t => t is not null) is int last and >= 0)
        {
            parsed[last] = null;
        }

        return parsed;
    }

    /// <summary>
    /// Un subtítulo suelto. Si el modelo llega al tope se reintenta con el doble (hasta <see cref="MaxRetryTokens"/>); si
    /// vuelve a llegar, no se acepta una traducción cortada: sale vacío y queda en <c>untranslated</c>.
    /// </summary>
    private async Task<string> SingleAsync(string text, TranslationRequest request, CancellationToken cancellationToken)
    {
        string prompt = Prompt.Single(text, request.Source, request.Target);
        int maxTokens = MaxTokens([text], request.Target);
        Generation g = await generator.GenerateAsync(prompt, maxTokens, cancellationToken).ConfigureAwait(false);
        if (g.HitLimit)
        {
            g = await generator.GenerateAsync(prompt, Math.Min(MaxRetryTokens, maxTokens * 2), cancellationToken).ConfigureAwait(false);
        }

        return g.HitLimit ? "" : Prompt.ParseSingle(g.Text);
    }
}
