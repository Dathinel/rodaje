using System.Text;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

/// <summary>Una palabra transcrita, en tiempo de la línea de tiempo.</summary>
public sealed record SpeechWord(Time Start, Time End, string Text);

/// <summary>
/// Un segmento transcrito (frase o tramo de voz) en tiempo de la línea de tiempo: la app pasa los segundos del medio
/// que devuelve el módulo por <see cref="ClipTimeMap"/> antes. Sin <see cref="Words"/>, los tiempos de cada palabra se
/// reparten por largo.
/// </summary>
public sealed record SpeechSegment(Time Start, Time End, string Text, IReadOnlyList<SpeechWord>? Words = null);

/// <summary>Cómo se parten los subtítulos: líneas, duración y pausas.</summary>
public sealed record SubtitleLayout
{
    public static readonly SubtitleLayout Default = new();

    /// <summary>Caracteres por línea (42 es la norma habitual de subtitulado).</summary>
    public int MaxLineChars { get; init; } = 42;

    public int MaxLines { get; init; } = 2;

    /// <summary>Lo mínimo que se ve un subtítulo; se alarga sin pisar al siguiente.</summary>
    public Time MinDuration { get; init; } = Time.FromSeconds(1);

    public Time MaxDuration { get; init; } = Time.FromSeconds(7);

    /// <summary>Un silencio entre palabras de al menos esto corta el subtítulo.</summary>
    public Time PauseBreak { get; init; } = Time.FromSeconds(0.7);
}

/// <summary>Un subtítulo ya partido: texto con sus saltos de línea y tiempos ajustados al cuadro.</summary>
public readonly record struct SubtitleCue(Time Start, Time End, string Text);

/// <summary>Convierte una transcripción con tiempos por palabra en clips de texto de una pista de subtítulos.</summary>
public static partial class SubtitleEdits
{
    /// <summary>
    /// Pone los subtítulos de <paramref name="segments"/> como clips de texto (copias de <paramref name="template"/>)
    /// en la pista de subtítulos más alta que tenga libre todo el tramo; si ninguna lo tiene, crea una encima de todas.
    /// </summary>
    /// <returns>Los ids de los clips en orden y, al final, el de la pista si se creó.</returns>
    public static IReadOnlyList<string> AddSubtitles(
        EditContext ctx,
        IReadOnlyList<SpeechSegment> segments,
        ClipGenerator template,
        SubtitleLayout? layout = null,
        string newTrackName = "Subtítulos")
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(template);
        IReadOnlyList<SubtitleCue> cues = Layout(segments, layout ?? SubtitleLayout.Default, ctx.Sequence.FrameRate);
        if (cues.Count == 0)
        {
            throw new EditException("La transcripción no trae texto.");
        }

        if (cues[0].Start < Time.Zero)
        {
            throw new EditException("Los subtítulos no pueden empezar antes del inicio de la línea de tiempo.");
        }

        Track? track = null;
        for (int i = ctx.Sequence.VideoTracks.Count - 1; i >= 0 && track is null; i--)
        {
            Track candidate = ctx.Sequence.VideoTracks[i];
            if (candidate.Role == TrackRoles.Subtitles && !candidate.Locked && cues.All(c => TrackOps.FindOverlap(candidate, c.Start, c.End) is null))
            {
                track = candidate;
            }
        }

        string? created = null;
        if (track is null)
        {
            created = TitleEdits.AddSubtitleTrack(ctx, newTrackName);
            track = ctx.Sequence.GetTrack(created);
        }

        List<string> ids = new(cues.Count + 1);
        foreach (SubtitleCue cue in cues)
        {
            ClipGenerator generator = template.Clone();
            generator.Text = cue.Text;
            var clip = new Clip
            {
                Id = ctx.Ids.NewId("clip"),
                Generator = generator,
                SourceIn = Time.Zero,
                SourceOut = cue.End - cue.Start,
                Start = cue.Start,
            };
            TrackOps.AddClipSorted(track, clip);
            ids.Add(clip.Id);
        }

        if (created is not null)
        {
            ids.Add(created);
        }

        ctx.EnsureValid();
        return ids;
    }

    /// <summary>
    /// Parte la transcripción en subtítulos: corta en cada segmento, en pausas largas, al llenar las líneas o la duración
    /// máxima, y tras un punto si el subtítulo ya va por media línea; reparte las palabras en líneas parejas; ajusta los
    /// tiempos al cuadro, sin solaparse y con la duración mínima si cabe antes del siguiente.
    /// </summary>
    public static IReadOnlyList<SubtitleCue> Layout(IReadOnlyList<SpeechSegment> segments, SubtitleLayout layout, Rational frameRate)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.MaxLineChars < 1 || layout.MaxLines < 1 || layout.MaxDuration <= Time.Zero)
        {
            throw new ArgumentException("La partición de subtítulos necesita líneas, caracteres y duración positivos.", nameof(layout));
        }

        int capacity = layout.MaxLineChars * layout.MaxLines;
        List<List<SpeechWord>> groups = [];
        foreach (SpeechSegment segment in segments.OrderBy(s => s.Start))
        {
            List<SpeechWord> words = WordsOf(segment);
            List<SpeechWord> current = [];
            for (int i = 0; i < words.Count; i++)
            {
                SpeechWord word = words[i];
                if (current.Count > 0)
                {
                    bool pause = word.Start - current[^1].End >= layout.PauseBreak;
                    bool full = Length(current) + 1 + word.Text.Length > capacity
                        || Wrap(current.Append(word).Select(w => w.Text).ToList(), layout.MaxLineChars).Count > layout.MaxLines;
                    bool tooLong = word.End - current[0].Start > layout.MaxDuration;
                    bool sentence = EndsSentence(current[^1].Text) && Length(current) >= layout.MaxLineChars / 2;
                    if (pause || full || tooLong || sentence)
                    {
                        groups.Add(current);
                        current = [];
                    }
                }

                current.Add(word);
            }

            if (current.Count > 0)
            {
                // Una cola de una o dos palabras se une a la anterior si cabe, en vez de quedar sola en pantalla.
                if (groups.Count > 0 && words.Count > current.Count && Length(current) < layout.MaxLineChars / 3)
                {
                    List<SpeechWord> previous = groups[^1];
                    List<SpeechWord> merged = [.. previous, .. current];
                    bool joined = current[0].Start - previous[^1].End < layout.PauseBreak;
                    if (joined
                        && Wrap(merged.Select(w => w.Text).ToList(), layout.MaxLineChars).Count <= layout.MaxLines
                        && merged[^1].End - merged[0].Start <= layout.MaxDuration)
                    {
                        groups[^1] = merged;
                        continue;
                    }

                    // Si no cabe junta, la anterior le cede palabras hasta que la cola deje de quedar huérfana.
                    while (joined && previous.Count > 1 && Length(current) < layout.MaxLineChars / 3
                        && Length(previous.Take(previous.Count - 1)) > Length(current.Prepend(previous[^1])))
                    {
                        current.Insert(0, previous[^1]);
                        previous.RemoveAt(previous.Count - 1);
                    }
                }

                groups.Add(current);
            }
        }

        Time frame = Time.FromFrames(1, frameRate);
        List<SubtitleCue> cues = new(groups.Count);
        Time previousEnd = Time.Zero;
        for (int i = 0; i < groups.Count; i++)
        {
            Time start = groups[i][0].Start.SnapToFrame(frameRate);
            if (i > 0)
            {
                start = Time.Max(start, previousEnd);
            }

            Time end = Time.Max(groups[i][^1].End.SnapToFrame(frameRate), start + layout.MinDuration);
            if (i + 1 < groups.Count)
            {
                end = Time.Min(end, Time.Max(groups[i + 1][0].Start.SnapToFrame(frameRate), start + frame));
            }

            end = Time.Max(end, start + frame);
            cues.Add(new SubtitleCue(start, end, string.Join('\n', Wrap(groups[i].Select(w => w.Text).ToList(), layout.MaxLineChars))));
            previousEnd = end;
        }

        return cues;
    }

    /// <summary>
    /// Reparte las palabras en el mínimo de líneas de hasta <paramref name="maxChars"/> (una palabra más larga va sola)
    /// y, si son dos, elige el corte que deja las líneas más parejas.
    /// </summary>
    internal static List<string> Wrap(IReadOnlyList<string> words, int maxChars)
    {
        List<string> lines = [];
        StringBuilder line = new();
        foreach (string word in words)
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > maxChars)
            {
                lines.Add(line.ToString());
                line.Clear();
            }

            line.Append(line.Length > 0 ? " " : "").Append(word);
        }

        if (line.Length > 0)
        {
            lines.Add(line.ToString());
        }

        if (lines.Count != 2)
        {
            return lines;
        }

        int best = -1, bestWidth = int.MaxValue;
        for (int cut = 1; cut < words.Count; cut++)
        {
            int top = Length(words.Take(cut)), bottom = Length(words.Skip(cut));
            int width = Math.Max(top, bottom);
            // A igual ancho, mejor la línea de arriba más corta (forma de pirámide).
            if (top <= maxChars && bottom <= maxChars && (width < bestWidth || (width == bestWidth && top <= bottom)))
            {
                (best, bestWidth) = (cut, width);
            }
        }

        return best < 0 ? lines : [string.Join(' ', words.Take(best)), string.Join(' ', words.Skip(best))];
    }

    private static List<SpeechWord> WordsOf(SpeechSegment segment)
    {
        if (segment.Words is { Count: > 0 } given)
        {
            return [.. given.Select(w => w with { Text = Clean(w.Text) }).Where(w => w.Text.Length > 0).OrderBy(w => w.Start)];
        }

        string[] parts = Clean(segment.Text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        long total = Math.Max(1, parts.Sum(p => p.Length + 1));
        long span = Math.Max(0, (segment.End - segment.Start).Ticks);
        List<SpeechWord> words = new(parts.Length);
        long chars = 0;
        foreach (string part in parts)
        {
            Time from = segment.Start + Time.FromTicks(span * chars / total);
            chars += part.Length + 1;
            words.Add(new SpeechWord(from, segment.Start + Time.FromTicks(span * chars / total), part));
        }

        return words;
    }

    /// <summary>Sin caracteres de control ni espacios repetidos: el validador no los admite en un título.</summary>
    private static string Clean(string text)
    {
        StringBuilder sb = new(text.Length);
        foreach (char c in text)
        {
            bool space = char.IsWhiteSpace(c) || char.IsControl(c);
            if (!space)
            {
                sb.Append(c);
            }
            else if (sb.Length > 0 && sb[^1] != ' ')
            {
                sb.Append(' ');
            }
        }

        return sb.ToString().Trim();
    }

    private static bool EndsSentence(string word) => word.Length > 0 && word[^1] is '.' or '?' or '!' or '…' or ';' or ':';

    private static int Length(IEnumerable<SpeechWord> words) => Length(words.Select(w => w.Text));

    private static int Length(IEnumerable<string> words)
    {
        int length = -1;
        foreach (string w in words)
        {
            length += w.Length + 1;
        }

        return Math.Max(0, length);
    }
}
