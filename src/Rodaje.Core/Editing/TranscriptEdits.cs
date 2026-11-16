using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

/// <summary>
/// Ediciones desde el texto (D-061): convierten palabras de un <see cref="TranscriptDocument"/> en tramos de la
/// secuencia y los quitan o mueven con las ediciones de siempre (<see cref="AnalysisEdits.RemoveSpans"/>, pegar insertando).
/// </summary>
public static partial class TranscriptEdits
{
    /// <summary>
    /// Tramos de la secuencia, por clip, que ocupan las palabras dadas (índices de <see cref="TranscriptDocument.Words"/>):
    /// de la mitad del hueco con la palabra anterior a la mitad del hueco con la siguiente, o el punto que elija
    /// <paramref name="cutPoint"/> entre esos dos instantes (el más silencioso, si la app analizó el audio). Sin palabra
    /// vecina en el mismo clip, el tramo llega al borde del clip. Palabras seguidas del mismo clip dan un solo tramo.
    /// </summary>
    public static IReadOnlyList<(string ClipId, Time Start, Time End)> SpansOf(
        TranscriptDocument document, Sequence sequence, IEnumerable<int> words, Func<Time, Time, Time>? cutPoint = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(words);
        IReadOnlyList<DocWord> all = document.Words;
        int[] sorted = [.. words.Distinct().Order()];
        if (sorted.Length > 0 && (sorted[0] < 0 || sorted[^1] >= all.Count))
        {
            throw new ArgumentOutOfRangeException(nameof(words), "Hay palabras que no están en el documento.");
        }

        var spans = new List<(string ClipId, Time Start, Time End)>();
        for (int k = 0; k < sorted.Length; k++)
        {
            int first = sorted[k], last = first;
            string clipId = all[first].ClipId;
            while (k + 1 < sorted.Length && sorted[k + 1] == last + 1 && all[last + 1].ClipId == clipId)
            {
                k++;
                last++;
            }

            Clip clip = sequence.GetClip(clipId).Clip;
            Time start = first > 0 && all[first - 1].ClipId == clipId ? Cut(all[first - 1].SeqEnd, all[first].SeqStart, cutPoint) : clip.Start;
            Time end = last + 1 < all.Count && all[last + 1].ClipId == clipId ? Cut(all[last].SeqEnd, all[last + 1].SeqStart, cutPoint) : clip.End;
            spans.Add((clipId, start, end));
        }

        return spans;
    }

    /// <summary>
    /// Quita de la secuencia las palabras dadas, con ripple (según <paramref name="scope"/>) o dejando hueco, con los
    /// clips enlazados. Si todas las palabras de un clip se van, se va el clip entero.
    /// </summary>
    /// <returns>Tramos quitados, en tiempo de la secuencia de antes y en orden.</returns>
    public static IReadOnlyList<(Time Start, Time End)> DeleteWords(
        EditContext ctx, TranscriptDocument document, IEnumerable<int> words, bool ripple, RippleScope scope,
        Func<Time, Time, Time>? cutPoint = null)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        IReadOnlyList<(string ClipId, Time Start, Time End)> spans = SpansOf(document, ctx.Sequence, words, cutPoint);
        List<(Time Start, Time End)> removed = RemoveByClip(ctx, spans, ripple, scope);
        if (removed.Count == 0 && spans.Count > 0)
        {
            throw new EditException("Lo elegido dura menos de un cuadro: no hay nada que borrar.");
        }

        return removed;
    }

    /// <summary>
    /// Mueve las palabras <c>[first, first + count)</c> delante de la palabra <paramref name="before"/> (o al final de la
    /// pista guía si es <c>Words.Count</c>): corta en los bordes, copia el grupo enlazado entero (video y audio), lo borra
    /// con ripple y lo pega insertando donde quedó el destino.
    /// </summary>
    public static void MoveWords(EditContext ctx, TranscriptDocument document, int first, int count, int before, RippleScope scope)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(document);
        IReadOnlyList<DocWord> all = document.Words;
        if (count < 1 || first < 0 || first + count > all.Count || before < 0 || before > all.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "La frase o el destino no están en el documento.");
        }

        if (before >= first && before <= first + count)
        {
            throw new EditException("La frase ya está ahí: suéltala fuera de ella misma.");
        }

        Sequence sequence = ctx.Sequence;
        Rational rate = sequence.FrameRate;
        Track guide = sequence.GetTrack(document.TrackId!);
        var cuts = new Dictionary<string, List<Time>>(StringComparer.Ordinal);
        var moved = new List<(Time Start, Time End)>();
        foreach ((string clipId, Time start, Time end) in SpansOf(document, sequence, Enumerable.Range(first, count)))
        {
            Time s = start.SnapToFrame(rate), e = end.SnapToFrame(rate);
            if (e > s)
            {
                moved.Add((s, e));
                CutsOf(cuts, clipId).AddRange([s, e]);
            }
        }

        if (moved.Count == 0)
        {
            throw new EditException("La frase dura menos de un cuadro: no hay nada que mover.");
        }

        // Un hueco (o un clip sin texto) en medio se copiaría con la frase y quedaría también en su sitio.
        moved.Sort();
        for (int i = 1; i < moved.Count; i++)
        {
            if (moved[i].Start != moved[i - 1].End)
            {
                throw new EditException("La frase cruza un hueco entre clips: muévela por partes.");
            }
        }

        Time? destination = null;
        if (before < all.Count)
        {
            DocWord next = all[before];
            Clip clip = sequence.GetClip(next.ClipId).Clip;
            destination = before > 0 && all[before - 1].ClipId == next.ClipId
                ? Cut(all[before - 1].SeqEnd, next.SeqStart, null).SnapToFrame(rate)
                : clip.Start;
            CutsOf(cuts, clip.Id).Add(destination.Value);
        }

        foreach ((string clipId, List<Time> times) in cuts)
        {
            AnalysisEdits.SplitAt(ctx, clipId, times);
        }

        string[] middle = [.. guide.Clips.Where(c => moved.Exists(m => c.Start >= m.Start && c.End <= m.End)).Select(c => c.Id)];
        string? anchor = destination is { } at ? guide.Clips.Find(c => c.Start == at)?.Id : null;
        string[] group = [.. TrackOps.ExpandSelection(ctx, middle).Select(p => p.Clip.Id)];
        ClipClipboard copied = ClipClipboard.Capture(sequence, group, alone: true, ctx.Project)
            ?? throw new EditException("No hay nada que mover.");

        // «Solo ellos» para no arrastrar los grupos, pero con su enlace: Paste mantiene unidos los que se copian juntos.
        foreach (CopiedClip item in copied.Items)
        {
            item.Clip.LinkId = sequence.GetClip(item.Clip.Id).Clip.LinkId;
        }

        TimelineEdits.RippleDelete(ctx, middle, scope);
        Time target = anchor is not null ? sequence.GetClip(anchor).Clip.Start
            : destination is { } gap ? gap - moved.Where(m => m.End <= gap).Aggregate(Time.Zero, (sum, m) => sum + (m.End - m.Start))
            : guide.End;
        TimelineEdits.Paste(ctx, copied, target, PlacementMode.Insert);
    }

    /// <summary>
    /// Palabras que parecen muletillas en <paramref name="language"/>, en orden: <c>Safe</c> las seguras (eh, um), que se
    /// ofrecen marcadas; las dudosas («bueno», «o sea»: cada palabra de la expresión) también son palabras de verdad y se
    /// ofrecen sin marcar.
    /// </summary>
    public static IReadOnlyList<(int Word, bool Safe)> FillerCandidates(TranscriptDocument document, string language)
    {
        ArgumentNullException.ThrowIfNull(document);
        IReadOnlyList<DocWord> all = document.Words;
        var safe = new HashSet<string>(FillerWords.Safe(language), StringComparer.Ordinal);
        string[][] doubtful = [.. FillerWords.Doubtful(language).Select(p => p.Split(' '))];
        string[] keys = [.. all.Select(w => FillerWords.Key(w.Text))];
        var found = new SortedDictionary<int, bool>();
        for (int i = 0; i < all.Count; i++)
        {
            if (safe.Contains(keys[i]))
            {
                found[i] = true;
                continue;
            }

            foreach (string[] phrase in doubtful)
            {
                if (Matches(all, keys, i, phrase))
                {
                    for (int j = i; j < i + phrase.Length; j++)
                    {
                        found.TryAdd(j, false);
                    }
                }
            }
        }

        return [.. found.Select(p => (p.Key, p.Value))];
    }

    /// <summary>Huecos entre palabras seguidas del mismo clip de al menos <paramref name="minimum"/>, en tiempo de la secuencia.</summary>
    public static IReadOnlyList<(string ClipId, Time Start, Time End)> Pauses(TranscriptDocument document, Time minimum)
    {
        ArgumentNullException.ThrowIfNull(document);
        IReadOnlyList<DocWord> all = document.Words;
        var pauses = new List<(string ClipId, Time Start, Time End)>();
        for (int i = 1; i < all.Count; i++)
        {
            if (all[i].ClipId == all[i - 1].ClipId && all[i].SeqStart - all[i - 1].SeqEnd >= minimum)
            {
                pauses.Add((all[i].ClipId, all[i - 1].SeqEnd, all[i].SeqStart));
            }
        }

        return pauses;
    }

    /// <summary>
    /// Acorta cada pausa de al menos <paramref name="minimum"/> hasta dejar <paramref name="keep"/> de silencio, la mitad
    /// a cada lado.
    /// </summary>
    /// <returns>Tramos quitados, en tiempo de la secuencia de antes y en orden.</returns>
    public static IReadOnlyList<(Time Start, Time End)> RemovePauses(
        EditContext ctx, TranscriptDocument document, Time minimum, Time keep, bool ripple, RippleScope scope)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (keep < Time.Zero || minimum <= keep)
        {
            throw new ArgumentOutOfRangeException(nameof(keep), "Lo que se deja de cada pausa debe ser menor que la pausa mínima.");
        }

        Time half = Time.FromTicks(keep.Ticks / 2);
        return RemoveByClip(ctx, Pauses(document, minimum).Select(p => (p.ClipId, p.Start + half, p.End - (keep - half))), ripple, scope);
    }

    // Clip a clip, del que empieza más tarde al que empieza antes: el ripple de un clip solo corre lo que va a su derecha,
    // así los tramos de los anteriores siguen valiendo.
    private static List<(Time Start, Time End)> RemoveByClip(
        EditContext ctx, IEnumerable<(string ClipId, Time Start, Time End)> spans, bool ripple, RippleScope scope)
    {
        var removed = new List<(Time Start, Time End)>();
        foreach (IGrouping<string, (string ClipId, Time Start, Time End)> group in
                 spans.GroupBy(s => s.ClipId).OrderByDescending(g => ctx.Sequence.GetClip(g.Key).Clip.Start))
        {
            Clip clip = ctx.Sequence.GetClip(group.Key).Clip;
            if (CoversClip(clip, group, ctx.Sequence.FrameRate))
            {
                removed.Add((clip.Start, clip.End));
                if (ripple)
                {
                    TimelineEdits.RippleDelete(ctx, [clip.Id], scope);
                }
                else
                {
                    TimelineEdits.Lift(ctx, [clip.Id]);
                }

                continue;
            }

            removed.AddRange(AnalysisEdits.RemoveSpans(ctx, clip.Id, group.Select(s => (s.Start, s.End)), ripple, scope));
        }

        removed.Sort();
        return removed;
    }

    // Lo mismo que hace RemoveSpans al ajustar al cuadro y unir: si lo une todo, el clip se quita entero.
    private static bool CoversClip(Clip clip, IEnumerable<(string ClipId, Time Start, Time End)> spans, Rational rate)
    {
        Time reached = clip.Start;
        foreach ((_, Time start, Time end) in spans.OrderBy(s => s.Start))
        {
            Time s = start <= clip.Start ? clip.Start : FrameCeiling(start, rate);
            Time e = end >= clip.End ? clip.End : Time.FromFrames(end.ToFrames(rate), rate);
            if (e <= s)
            {
                continue;
            }

            if (s > reached)
            {
                return false;
            }

            reached = Time.Max(reached, e);
        }

        return reached >= clip.End;
    }

    private static Time FrameCeiling(Time t, Rational rate)
    {
        Time floor = Time.FromFrames(t.ToFrames(rate), rate);
        return floor >= t ? floor : Time.FromFrames(t.ToFrames(rate) + 1, rate);
    }

    private static Time Cut(Time previousEnd, Time nextStart, Func<Time, Time, Time>? cutPoint)
    {
        Time a = Time.Min(previousEnd, nextStart), b = Time.Max(previousEnd, nextStart);
        Time middle = a + Time.FromTicks((b - a).Ticks / 2);
        return cutPoint?.Invoke(a, b) is { } chosen && chosen >= a && chosen <= b ? chosen : middle;
    }

    private static List<Time> CutsOf(Dictionary<string, List<Time>> cuts, string clipId)
    {
        if (!cuts.TryGetValue(clipId, out List<Time>? list))
        {
            list = [];
            cuts[clipId] = list;
        }

        return list;
    }

    private static bool Matches(IReadOnlyList<DocWord> words, string[] keys, int at, string[] phrase)
    {
        if (at + phrase.Length > words.Count)
        {
            return false;
        }

        for (int j = 0; j < phrase.Length; j++)
        {
            if (keys[at + j] != phrase[j] || words[at + j].ClipId != words[at].ClipId)
            {
                return false;
            }
        }

        return true;
    }
}
