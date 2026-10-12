using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

/// <summary>Dónde está cada palabra transcrita dentro del texto del subtítulo, en caracteres.</summary>
public readonly record struct WordRange(int Start, int Length)
{
    public static readonly WordRange None = new(-1, 0);

    public bool Found => Start >= 0;
}

/// <summary>Edición por palabras de un subtítulo automático (<see cref="ClipGenerator.Words"/>): corregir, dividir y unir.</summary>
public static partial class SubtitleEdits
{
    /// <summary>
    /// Empareja cada palabra con su trozo del texto (lo que va entre espacios). Si hay tantos trozos como palabras se
    /// emparejan en orden; si el texto se editó a mano, cada palabra busca el siguiente trozo igual (sin distinguir
    /// mayúsculas) y las que no lo encuentran quedan sin rango.
    /// </summary>
    public static IReadOnlyList<WordRange> WordRanges(string text, IReadOnlyList<GeneratorWord> words)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(words);
        List<WordRange> tokens = Tokens(text);
        if (tokens.Count == words.Count)
        {
            return tokens;
        }

        var ranges = new WordRange[words.Count];
        int cursor = 0;
        for (int i = 0; i < words.Count; i++)
        {
            ranges[i] = WordRange.None;
            for (int j = cursor; j < tokens.Count; j++)
            {
                if (text.AsSpan(tokens[j].Start, tokens[j].Length).Equals(words[i].Text, StringComparison.OrdinalIgnoreCase))
                {
                    ranges[i] = tokens[j];
                    cursor = j + 1;
                    break;
                }
            }
        }

        return ranges;
    }

    /// <summary>
    /// La palabra que suena en <paramref name="origin"/> (tiempo del origen del clip): la última que ya empezó, y queda
    /// resaltada hasta que empieza la siguiente. -1 antes de la primera.
    /// </summary>
    public static int ActiveWord(IReadOnlyList<GeneratorWord> words, Time origin)
    {
        ArgumentNullException.ThrowIfNull(words);
        int found = -1;
        for (int i = 0; i < words.Count && words[i].Start <= origin; i++)
        {
            found = i;
        }

        return found;
    }

    /// <summary>
    /// Corrige la palabra <paramref name="index"/>: cambia su texto y su trozo del texto visible. Con espacios se vuelve
    /// varias palabras que se reparten su tiempo por largo; vacía, se borra.
    /// </summary>
    public static void CorrectWord(EditContext ctx, string clipId, int index, string replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        (Clip clip, List<GeneratorWord> words) = Subtitle(ctx, clipId);
        if (index < 0 || index >= words.Count)
        {
            throw new EditException("Esa palabra ya no está en el subtítulo.");
        }

        ClipGenerator generator = clip.Generator!;
        GeneratorWord old = words[index];
        string[] parts = Clean(replacement).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p.Length > ModelValidator.MaxWordLength))
        {
            throw new EditException($"Una palabra puede tener hasta {ModelValidator.MaxWordLength} caracteres.");
        }

        if (parts.Length == 0 && words.Count == 1)
        {
            throw new EditException("El subtítulo se quedaría sin palabras: bórralo desde la línea de tiempo.");
        }

        IReadOnlyList<WordRange> ranges = WordRanges(generator.Text, words);
        long span = Math.Max(0, (old.End - old.Start).Ticks);
        long total = Math.Max(1, parts.Sum(p => p.Length));
        long done = 0;
        List<GeneratorWord> added = new(parts.Length);
        foreach (string part in parts)
        {
            Time from = old.Start + Time.FromTicks(span * done / total);
            done += part.Length;
            added.Add(new GeneratorWord { Start = from, End = old.Start + Time.FromTicks(span * done / total), Text = part });
        }

        words.RemoveAt(index);
        words.InsertRange(index, added);
        if (ranges[index] is { Found: true } range)
        {
            string text = generator.Text.Remove(range.Start, range.Length).Insert(range.Start, string.Join(' ', parts));
            // Al borrar quedan dos espacios o una línea vacía: se recogen sin tocar los saltos que haya.
            generator.Text = parts.Length > 0
                ? text
                : string.Join('\n', text.Split('\n').Select(l => string.Join(' ', l.Split(' ', StringSplitOptions.RemoveEmptyEntries))).Where(l => l.Length > 0));
        }
        else
        {
            generator.Text = Rewrap(words.Select(w => w.Text).ToList());
        }

        ctx.EnsureValid();
    }

    /// <summary>
    /// Parte el subtítulo donde empieza la palabra <paramref name="index"/> (ajustado al cuadro): cada trozo se queda con sus
    /// palabras y su texto, repartido en líneas, y anima su entrada y su salida como un subtítulo propio.
    /// </summary>
    /// <returns>El id del trozo de la derecha.</returns>
    public static string SplitAtWord(EditContext ctx, string clipId, int index)
    {
        (Clip clip, List<GeneratorWord> words) = Subtitle(ctx, clipId);
        if (index <= 0 || index >= words.Count)
        {
            throw new EditException("Elige una palabra que no sea la primera para dividir ahí.");
        }

        Track track = ctx.Sequence.FindClip(clipId)!.Value.Track;
        EditContext.EnsureUnlocked(track);
        Time at = (clip.Start + (words[index].Start - clip.SourceIn)).SnapToFrame(ctx.Sequence.FrameRate);
        if (at <= clip.Start || at >= clip.End)
        {
            throw new EditException("Esa palabra empieza fuera del subtítulo: no se puede dividir ahí.");
        }

        List<string> tokens = TokenTexts(clip.Generator!.Text, words);
        Clip right = TrackOps.SplitOff(ctx, clip, at, tidy: false);
        track.Clips.Insert(track.Clips.IndexOf(clip) + 1, right);
        Reset(clip.Generator!, words.Take(index), tokens.Take(index));
        Reset(right.Generator!, words.Skip(index), tokens.Skip(index));
        ctx.EnsureValid();
        return right.Id;
    }

    /// <summary>
    /// Une el subtítulo con el siguiente de su pista: el clip crece hasta el final del otro, se queda con todas las palabras
    /// (pasadas a su origen) y el texto se reparte de nuevo en líneas. El otro clip desaparece.
    /// </summary>
    public static void JoinWithNext(EditContext ctx, string clipId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        (Track track, Clip clip) = ctx.Sequence.FindClip(clipId) ?? throw new EditException($"El clip «{clipId}» ya no está en la secuencia.");
        EditContext.EnsureUnlocked(track);
        Clip? next = track.Clips.Where(c => c.Start >= clip.End).OrderBy(c => c.Start).FirstOrDefault();
        if (clip.Generator is not { Kind: GeneratorKinds.Text } left || next?.Generator is not { Kind: GeneratorKinds.Text } right)
        {
            throw new EditException("No hay otro subtítulo después de este en la pista.");
        }

        List<string> texts = [.. TokenTexts(left.Text, left.Words ?? []), .. TokenTexts(right.Text, right.Words ?? [])];
        Time shift = (next.Start - next.SourceIn) - (clip.Start - clip.SourceIn);
        left.Words = left.Words is null || right.Words is null
            ? null
            : [.. left.Words.Select(w => w.Clone()), .. right.Words.Select(w => new GeneratorWord { Start = w.Start + shift, End = w.End + shift, Text = w.Text })];
        left.Text = Rewrap(texts);
        left.Edges.Out = right.Edges.Out;
        left.Edges.After = right.Edges.After;
        clip.SourceOut += next.End - clip.End;
        track.Clips.Remove(next);
        ctx.EnsureValid();
    }

    private static (Clip Clip, List<GeneratorWord> Words) Subtitle(EditContext ctx, string clipId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        (Track track, Clip clip) = ctx.Sequence.FindClip(clipId) ?? throw new EditException($"El clip «{clipId}» ya no está en la secuencia.");
        EditContext.EnsureUnlocked(track);
        if (clip.Generator is not { Kind: GeneratorKinds.Text, Words: { Count: > 0 } words })
        {
            throw new EditException("Este texto no tiene palabras transcritas: solo los subtítulos automáticos se editan por palabra.");
        }

        return (clip, words);
    }

    /// <summary>El trozo de texto de cada palabra (con la puntuación que se le haya puesto a mano) o, si no se encuentra, la palabra.</summary>
    private static List<string> TokenTexts(string text, List<GeneratorWord> words)
    {
        if (words.Count == 0)
        {
            return [.. Tokens(text).Select(t => text.Substring(t.Start, t.Length))];
        }

        IReadOnlyList<WordRange> ranges = WordRanges(text, words);
        return [.. words.Select((w, i) => ranges[i].Found ? text.Substring(ranges[i].Start, ranges[i].Length) : w.Text)];
    }

    private static void Reset(ClipGenerator generator, IEnumerable<GeneratorWord> words, IEnumerable<string> tokens)
    {
        generator.Words = [.. words.Select(w => w.Clone())];
        generator.Text = Rewrap([.. tokens]);
        generator.Edges = new GeneratorEdges();
    }

    private static string Rewrap(List<string> tokens) => string.Join('\n', Wrap(tokens, SubtitleLayout.Default.MaxLineChars));

    private static List<WordRange> Tokens(string text)
    {
        List<WordRange> tokens = [];
        int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            int start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            if (i > start)
            {
                tokens.Add(new WordRange(start, i - start));
            }
        }

        return tokens;
    }
}
