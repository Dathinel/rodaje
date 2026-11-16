using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

/// <summary>Palabras del origen ausentes de la pista guía, entre dos palabras visibles.</summary>
public readonly record struct RemovedTranscriptSpan(
    int BeforeWord, string TranscriptId, int FirstWord, int Count, Time SourceIn, Time SourceOut);

public static partial class TranscriptEdits
{
    public static IReadOnlyList<RemovedTranscriptSpan> RemovedWords(Project project, Sequence sequence, TranscriptDocument document)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(document);
        var removed = new List<RemovedTranscriptSpan>();
        var visible = document.Words.GroupBy(w => w.TranscriptId)
            .ToDictionary(g => g.Key, g => g.Select(w => w.WordIndex).ToHashSet(), StringComparer.Ordinal);
        for (int i = 1; i < document.Words.Count; i++)
        {
            DocWord previous = document.Words[i - 1], next = document.Words[i];
            if (previous.TranscriptId != next.TranscriptId || previous.ClipId == next.ClipId) continue;
            Clip left = sequence.GetClip(previous.ClipId).Clip, right = sequence.GetClip(next.ClipId).Clip;
            bool reverse = left.TimeMap.Reverse;
            if (reverse != right.TimeMap.Reverse) continue;
            int first = reverse ? next.WordIndex + 1 : previous.WordIndex + 1;
            int end = reverse ? previous.WordIndex : next.WordIndex;
            Time sourceIn = reverse ? right.SourceOut : left.SourceOut;
            Time sourceOut = reverse ? left.SourceIn : right.SourceIn;
            if (sourceIn >= sourceOut || first >= end) continue;
            MediaTranscript transcript = project.FindTranscript(left.MediaId)!;
            var spans = new List<RemovedTranscriptSpan>();
            for (int word = first; word < end; word++)
            {
                if (visible[transcript.Id].Contains(word)) continue;
                int start = word;
                while (word + 1 < end && !visible[transcript.Id].Contains(word + 1)) word++;
                Time from = start == first ? sourceIn : Cut(transcript.Words[start - 1].End, transcript.Words[start].Start, null);
                Time to = word + 1 == end ? sourceOut : Cut(transcript.Words[word].End, transcript.Words[word + 1].Start, null);
                from = Time.Max(from, sourceIn);
                to = Time.Min(to, sourceOut);
                if (to > from) spans.Add(new RemovedTranscriptSpan(i, transcript.Id, start, word - start + 1, from, to));
            }

            if (reverse) spans.Reverse();
            removed.AddRange(spans);
        }

        return removed;
    }

    /// <summary>Inserta el tramo del medio con el enlace y la velocidad del clip vecino.</summary>
    public static IReadOnlyList<string> RecoverWords(EditContext ctx, TranscriptDocument document, RemovedTranscriptSpan span)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (!RemovedWords(ctx.Project, ctx.Sequence, document).Contains(span))
            throw new EditException("Ese tramo ya no está quitado: actualiza la transcripción.");
        Clip left = ctx.Sequence.GetClip(document.Words[span.BeforeWord - 1].ClipId).Clip;
        Clip right = ctx.Sequence.GetClip(document.Words[span.BeforeWord].ClipId).Clip;
        Track guide = ctx.Sequence.GetTrack(document.TrackId!);
        Clip anchor = left;
        (Track Track, Clip Clip)? partner = LinkedNeighbor(ctx.Sequence, guide, left);
        if (partner is null && LinkedNeighbor(ctx.Sequence, guide, right) is { } rightPartner)
        {
            anchor = right;
            partner = rightPartner;
        }
        string[] ids = partner is { } linked ? [anchor.Id, linked.Clip.Id] : [anchor.Id];
        ClipClipboard copied = ClipClipboard.Capture(ctx.Sequence, ids, alone: true, ctx.Project)!;
        foreach (CopiedClip item in copied.Items)
        {
            // El enlazado conserva su desfase de origen con el clip guía.
            Time offset = item.Clip.SourceIn - anchor.SourceIn;
            item.Clip.LinkId = partner is null ? null : anchor.LinkId;
            item.Clip.Speed = left.Speed?.Clone();
            if (item.Clip.Speed is { } speed) speed.Offset += left.Duration;
            ClipTimeMap.Fit(copied.Origin, span.SourceIn + offset, span.SourceOut + offset, item.Clip.Speed).ApplyTo(item.Clip);
        }

        // Si se borró sin ripple, el hueco que quedó lo vuelve a llenar en vez de alargar la secuencia.
        Time? next = guide.Clips.Where(c => c.Start >= left.End).Select(c => (Time?)c.Start).Min();
        PlacementMode mode = next - left.End >= copied.Length ? PlacementMode.Strict : PlacementMode.Insert;
        return TimelineEdits.Paste(ctx, copied, left.End, mode);
    }

    private static (Track Track, Clip Clip)? LinkedNeighbor(Sequence sequence, Track guide, Clip clip) => clip.LinkId is null ? null
        : sequence.AllTracks.Where(t => t.Kind != guide.Kind)
            .SelectMany(t => t.Clips.Where(c => c.LinkId == clip.LinkId && c.MediaId == clip.MediaId).Select(c => ((Track Track, Clip Clip)?)(t, c)))
            .FirstOrDefault();
}
