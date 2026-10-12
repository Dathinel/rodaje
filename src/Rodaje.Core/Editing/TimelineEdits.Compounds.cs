using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

public static partial class TimelineEdits
{
    /// <summary>
    /// Lleva los clips (con sus enlazados) a una secuencia nueva y pone en su lugar un clip compuesto que la muestra: uno
    /// en la pista de video más baja tocada y otro, enlazado, en la de audio más baja. Lo anidado conserva las pistas
    /// relativas, los ids, los enlaces y las transiciones entre clips elegidos; empieza en 0.
    /// </summary>
    /// <returns>Los compuestos creados (video primero) y, al final, el id de la secuencia nueva.</returns>
    public static IReadOnlyList<string> CreateCompound(EditContext ctx, IEnumerable<string> clipIds, string name)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        List<(Track Track, Clip Clip)> chosen = TrackOps.ExpandSelection(ctx, clipIds);
        foreach ((Track track, _) in chosen)
        {
            EditContext.EnsureUnlocked(track);
        }

        Sequence outer = ctx.Sequence;
        Time from = chosen.Min(c => c.Clip.Start);
        Time to = chosen.Max(c => c.Clip.End);
        var nested = new Sequence
        {
            Id = ctx.Ids.NewId("sequence"),
            Name = name,
            FrameRate = outer.FrameRate,
            Width = outer.Width,
            Height = outer.Height,
            SampleRate = outer.SampleRate,
        };

        var created = new List<string>();
        string? linkId = chosen.Exists(c => c.Track.Kind == TrackKind.Video) && chosen.Exists(c => c.Track.Kind == TrackKind.Audio)
            ? ctx.Ids.NewId("link")
            : null;
        foreach (TrackKind kind in (TrackKind[])[TrackKind.Video, TrackKind.Audio])
        {
            List<Track> list = TracksSupport.ListOf(outer, kind);
            List<Track> inner = TracksSupport.ListOf(nested, kind);
            int[] used = [.. chosen.Where(c => c.Track.Kind == kind).Select(c => list.IndexOf(c.Track)).Distinct().Order()];
            if (used.Length == 0)
            {
                inner.Add(new Track { Id = ctx.Ids.NewId("track"), Kind = kind, Name = kind == TrackKind.Video ? "Video 1" : "Audio 1" });
                continue;
            }

            for (int i = used[0]; i <= used[^1]; i++)
            {
                Track source = list[i];
                var track = new Track { Id = ctx.Ids.NewId("track"), Kind = kind, Name = source.Name, Role = source.Role };
                var moving = chosen.Where(c => c.Track == source).Select(c => c.Clip).ToHashSet();
                foreach (Clip clip in moving)
                {
                    source.Clips.Remove(clip);
                    clip.Start -= from;
                    TrackOps.AddClipSorted(track, clip);
                }

                var movingIds = moving.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
                track.Transitions.AddRange(source.Transitions.Where(t => movingIds.Contains(t.From) && movingIds.Contains(t.To)));
                source.Transitions.RemoveAll(t => movingIds.Contains(t.From) || movingIds.Contains(t.To));
                inner.Add(track);
            }

            Track host = list[used[0]];
            if (TrackOps.FindOverlap(host, from, to) is { } blocking)
            {
                throw new EditException(
                    $"El compuesto va en la pista «{host.Name}», pero el clip «{blocking.Id}» ocupa parte de ese tramo. Selecciónalo también o muévelo.");
            }

            var compound = new Clip
            {
                Id = ctx.Ids.NewId("clip"),
                SequenceId = nested.Id,
                SourceIn = Time.Zero,
                SourceOut = to - from,
                Start = from,
                LinkId = linkId,
            };
            TrackOps.AddClipSorted(host, compound);
            created.Add(compound.Id);
        }

        ctx.Project.Sequences.Add(nested);
        if (ModelValidator.ValidateSequence(ctx.Project, nested) is [string problem, ..])
        {
            throw new EditException(problem);
        }

        ctx.EnsureValid();
        return [.. created, nested.Id];
    }

    /// <summary>
    /// Deshace el compuesto (y su enlazado de la misma secuencia): pone en su lugar lo que muestra, la pista anidada
    /// <c>k</c> en la pista <c>k</c> sobre la del compuesto (si está ocupada o no existe, en una pista nueva ahí). Si la
    /// secuencia anidada ya no la usa nadie, se borra y sus clips conservan los ids; si no, son copias con ids nuevos.
    /// </summary>
    /// <returns>Los clips que quedan en la secuencia.</returns>
    public static IReadOnlyList<string> DecomposeCompound(EditContext ctx, string clipId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        (_, Clip clip) = ctx.Sequence.FindClip(clipId) ?? throw new EditException($"No existe el clip «{clipId}» en la secuencia.");
        if (clip.SequenceId is not { } nestedId)
        {
            throw new EditException("El clip no es compuesto: no hay nada que descomponer.");
        }

        Sequence outer = ctx.Sequence;
        Sequence nested = ctx.Project.FindSequence(nestedId)
            ?? throw new EditException($"La secuencia «{nestedId}» del compuesto ya no está en el proyecto.");
        List<(Track Track, Clip Clip)> group = outer.GetLinkGroup(clipId).FindAll(m => m.Clip.SequenceId == nestedId);
        var groupClips = group.Select(m => m.Clip).ToHashSet();
        bool exclusive = !ctx.Project.Sequences.Exists(s => s.AllTracks.Any(t => t.Clips.Exists(c => c.SequenceId == nestedId && !groupClips.Contains(c))));
        Func<string, string> rename = exclusive ? id => id : FreshIds(ctx);

        var restored = new List<string>();
        foreach ((Track host, Clip compound) in group)
        {
            EditContext.EnsureUnlocked(host);
            List<Track> list = TracksSupport.ListOf(outer, host.Kind);
            int baseIndex = list.IndexOf(host);
            host.Clips.Remove(compound);
            host.Transitions.RemoveAll(t => t.From == compound.Id || t.To == compound.Id);
            List<Track> inner = TracksSupport.ListOf(nested, host.Kind);
            for (int k = 0; k < inner.Count; k++)
            {
                var scratch = new Track { Kind = host.Kind };
                List<Clip> clips = CompoundOps.Window(inner[k], compound, scratch, rename);
                if (clips.Count == 0)
                {
                    continue;
                }

                int index = baseIndex + k;
                Track target = index < list.Count ? list[index] : null!;
                if (target is null || target.Locked || clips.Exists(c => TrackOps.FindOverlap(target, c.Start, c.End) is not null))
                {
                    target = new Track { Id = ctx.Ids.NewId("track"), Kind = host.Kind, Name = inner[k].Name, Role = inner[k].Role };
                    list.Insert(Math.Min(index, list.Count), target);
                }

                foreach (Clip c in clips)
                {
                    TrackOps.AddClipSorted(target, c);
                    restored.Add(c.Id);
                }

                target.Transitions.AddRange(scratch.Transitions);
            }
        }

        if (exclusive)
        {
            ctx.Project.Sequences.Remove(nested);
        }

        ctx.EnsureValid();
        return restored;
    }

    /// <summary>Ids nuevos para las copias, con el mismo id nuevo para cada enlace.</summary>
    private static Func<string, string> FreshIds(EditContext ctx)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        return id =>
        {
            if (!map.TryGetValue(id, out string? fresh))
            {
                int dash = id.LastIndexOf('-');
                fresh = ctx.Ids.NewId(dash > 0 ? id[..dash] : "id");
                map[id] = fresh;
            }

            return fresh;
        };
    }
}
