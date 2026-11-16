using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Interchange;

/// <summary>Un tramo de una pista recorrida, en tiempo de la secuencia.</summary>
public abstract record WalkItem(Time Start, Time Duration)
{
    public Time End => Start + Duration;
}

/// <summary>
/// Un clip con lo que los escritores necesitan resuelto: su medio (nulo en un generador o un compuesto), la secuencia del
/// compuesto y las transiciones de sus dos cortes. Al aplanar, <see cref="Clip"/> es una copia recortada al tramo que se ve
/// y <see cref="WalkItem.Start"/> está en tiempo de la secuencia de fuera.
/// </summary>
public sealed record WalkClip(Time Start, Time Duration, Clip Clip, MediaRef? Media, Sequence? Compound, TransitionInstance? In, TransitionInstance? Out)
    : WalkItem(Start, Duration);

public sealed record WalkGap(Time Start, Time Duration) : WalkItem(Start, Duration);

/// <summary>Una pista recorrida de principio a fin: clips y huecos seguidos, sin solapes.</summary>
public sealed record WalkTrack(TrackKind Kind, int Index, Track Track, IReadOnlyList<WalkItem> Items);

/// <summary>Vista plana de una secuencia para escribirla en otro formato.</summary>
public static class TimelineWalk
{
    /// <summary>
    /// Las pistas de <paramref name="sequence"/> como tramos seguidos desde el cero. Con <paramref name="flattenCompounds"/>,
    /// cada compuesto se cambia por los clips de su secuencia en el tramo que muestra, con el presupuesto de
    /// <see cref="InterchangeLimits.MaxFlattenedClips"/> y detección de ciclos.
    /// </summary>
    /// <remarks>
    /// Al aplanar el video, en cada instante se queda el clip de la pista más alta del compuesto, como se ve; un clip con
    /// velocidad tapado en parte se deja fuera entero. En el audio solo entra la primera pista del compuesto. Las
    /// transiciones de dentro del compuesto se pierden. Todo eso va al informe como aproximado.
    /// </remarks>
    public static IReadOnlyList<WalkTrack> Of(Project project, Sequence sequence, bool flattenCompounds, InterchangeLimits limits, InterchangeReport report)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(report);
        int budget = limits.MaxFlattenedClips;
        var result = new List<WalkTrack>();
        var media = new Dictionary<string, MediaRef>(StringComparer.Ordinal);
        foreach (MediaRef m in project.Media)
        {
            media.TryAdd(m.Id, m);
        }

        foreach ((List<Track> tracks, TrackKind kind) in new[] { (sequence.VideoTracks, TrackKind.Video), (sequence.AudioTracks, TrackKind.Audio) })
        {
            for (int index = 0; index < tracks.Count; index++)
            {
                Track track = tracks[index];
                var clips = new List<WalkClip>();
                Dictionary<string, TransitionInstance> into = track.Transitions.DistinctBy(t => t.To).ToDictionary(t => t.To, StringComparer.Ordinal);
                Dictionary<string, TransitionInstance> outOf = track.Transitions.DistinctBy(t => t.From).ToDictionary(t => t.From, StringComparer.Ordinal);
                foreach (Clip clip in track.Clips.OrderBy(c => c.Start))
                {
                    WalkClip item = Item(project, media, clip, clip.Start, into.GetValueOrDefault(clip.Id), outOf.GetValueOrDefault(clip.Id));
                    if (flattenCompounds && item.Compound is { } inner)
                    {
                        var path = new HashSet<string>(StringComparer.Ordinal) { sequence.Id };
                        clips.AddRange(Flatten(project, media, item, inner, kind, path, limits, report, ref budget));
                    }
                    else
                    {
                        Spend(ref budget, limits);
                        clips.Add(item);
                    }
                }

                result.Add(new WalkTrack(kind, index, track, WithGaps(clips)));
            }
        }

        return result;
    }

    private static WalkClip Item(Project project, Dictionary<string, MediaRef> media, Clip clip, Time start, TransitionInstance? into, TransitionInstance? outOf) =>
        new(start, clip.Duration, clip,
            clip.Generator is null && clip.SequenceId is null ? media.GetValueOrDefault(clip.MediaId) : null,
            clip.SequenceId is { } id ? project.FindSequence(id) : null,
            into, outOf);

    private static void Spend(ref int budget, InterchangeLimits limits)
    {
        if (--budget < 0)
        {
            throw new InterchangeException($"Al aplanar los clips compuestos salen más de {limits.MaxFlattenedClips} clips.");
        }
    }

    private static List<WalkItem> WithGaps(List<WalkClip> clips)
    {
        var items = new List<WalkItem>(clips.Count * 2);
        Time cursor = Time.Zero;
        foreach (WalkClip clip in clips.OrderBy(c => c.Start))
        {
            if (clip.Start > cursor)
            {
                items.Add(new WalkGap(cursor, clip.Start - cursor));
            }

            items.Add(clip);
            cursor = Time.Max(cursor, clip.End);
        }

        return items;
    }

    /// <summary>Los clips que se ven del compuesto <paramref name="outer"/> en su tramo, en tiempo de fuera.</summary>
    private static List<WalkClip> Flatten(
        Project project, Dictionary<string, MediaRef> media, WalkClip outer, Sequence inner, TrackKind kind, HashSet<string> path, InterchangeLimits limits, InterchangeReport report, ref int budget)
    {
        if (!path.Add(inner.Id) || path.Count > limits.MaxDepth)
        {
            throw new InterchangeException(path.Count > limits.MaxDepth
                ? $"Los clips compuestos están anidados más de {limits.MaxDepth} niveles."
                : "Un clip compuesto se contiene a sí mismo.");
        }

        Time offset = outer.Start - outer.Clip.SourceIn;
        Time from = outer.Clip.SourceIn;
        Time to = outer.Clip.SourceOut;
        List<Track> tracks = kind == TrackKind.Video ? [.. inner.VideoTracks.AsEnumerable().Reverse()] : [.. inner.AudioTracks.Take(1)];
        if (kind == TrackKind.Audio && inner.AudioTracks.Count > 1)
        {
            report.Add(IssueSeverity.Approximate, IssueKind.Compound, "Del audio de un clip compuesto solo se lleva su primera pista.", inner.Name, outer.Clip.Id, outer.Start);
        }

        if (inner.AllTracks.Any(t => t.Transitions.Count > 0))
        {
            report.Add(IssueSeverity.Approximate, IssueKind.Compound, "Las transiciones de dentro de un clip compuesto se pierden al aplanarlo.", inner.Name, outer.Clip.Id, outer.Start);
        }

        var covered = new List<(Time Start, Time End)>();
        var pieces = new List<WalkClip>();
        foreach (Track track in tracks.Where(t => !t.Hidden || kind == TrackKind.Audio))
        {
            // Un clip desactivado no se ve: no tapa al de debajo.
            foreach (Clip clip in track.Clips.Where(c => c.Enabled))
            {
                Time start = Time.Max(clip.Start, from);
                Time end = Time.Min(clip.End, to);
                if (end <= start)
                {
                    continue;
                }

                List<(Time Start, Time End)> free = Subtract(start, end, covered);
                if (clip.Speed is not null && (free.Count != 1 || free[0] != (clip.Start, clip.End)))
                {
                    report.Add(IssueSeverity.Lost, IssueKind.Compound, "Un clip con velocidad tapado en parte dentro de un compuesto no se lleva.", inner.Name, clip.Id, offset + start);
                    covered.Add((start, end));
                    continue;
                }

                foreach ((Time pieceStart, Time pieceEnd) in free)
                {
                    Clip piece = clip.Clone();
                    if (clip.Speed is null)
                    {
                        piece.SourceIn = clip.SourceIn + (pieceStart - clip.Start);
                        piece.SourceOut = piece.SourceIn + (pieceEnd - pieceStart);
                    }

                    piece.Start = offset + pieceStart;
                    WalkClip item = Item(project, media, piece, piece.Start,
                        pieceStart == from ? outer.In : null, pieceEnd == to ? outer.Out : null);
                    if (item.Compound is { } deeper)
                    {
                        pieces.AddRange(Flatten(project, media, item, deeper, kind, path, limits, report, ref budget));
                    }
                    else
                    {
                        Spend(ref budget, limits);
                        pieces.Add(item);
                    }
                }

                covered.Add((start, end));
            }
        }

        path.Remove(inner.Id);
        return pieces;
    }

    // shortcut: cuadrático en los clips de un compuesto; pasar a intervalos ordenados si uno llega a miles de clips.
    private static List<(Time Start, Time End)> Subtract(Time start, Time end, List<(Time Start, Time End)> covered)
    {
        var free = new List<(Time, Time)> { (start, end) };
        foreach ((Time cs, Time ce) in covered)
        {
            var next = new List<(Time, Time)>(free.Count + 1);
            foreach ((Time fs, Time fe) in free)
            {
                if (ce <= fs || cs >= fe)
                {
                    next.Add((fs, fe));
                    continue;
                }

                if (cs > fs)
                {
                    next.Add((fs, cs));
                }

                if (ce < fe)
                {
                    next.Add((ce, fe));
                }
            }

            free = next;
        }

        return free;
    }
}
