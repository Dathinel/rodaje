using FotoToma.Core.Model;
using FotoToma.Core.Timing;

namespace FotoToma.Core.Editing;

/// <summary>Valores que tendrá un clip; los recortes planean y comprueban todo antes de aplicar nada.</summary>
internal readonly record struct TrimPlan(Track Track, Clip Clip, Time Start, Time SourceIn, Time SourceOut)
{
    public Time End => Start + (SourceOut - SourceIn);

    public static TrimPlan Of(Track track, Clip clip) => new(track, clip, clip.Start, clip.SourceIn, clip.SourceOut);
}

internal static class TrimmingSupport
{
    public static List<(Track Track, Clip Clip)> ResolveGroup(EditContext ctx, string clipId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (clipId is null || ctx.Sequence.FindClip(clipId) is null)
        {
            throw new EditException($"No existe el clip «{clipId}» en la secuencia.");
        }

        return ctx.Sequence.GetLinkGroup(clipId);
    }

    public static Track ResolveTrack(EditContext ctx, string trackId)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return (trackId is null ? null : ctx.Sequence.FindTrack(trackId))
            ?? throw new EditException($"No existe la pista «{trackId}» en la secuencia.");
    }

    public static Clip? AdjacentNext(Track track, Clip clip) =>
        track.Clips.Find(c => !ReferenceEquals(c, clip) && c.Start == clip.End);

    public static Clip? AdjacentPrevious(Track track, Clip clip) =>
        track.Clips.Find(c => !ReferenceEquals(c, clip) && c.End == clip.Start);

    public static void CheckBounds(EditContext ctx, IEnumerable<TrimPlan> plans)
    {
        foreach (TrimPlan plan in plans)
        {
            string name = plan.Clip.Id;
            if (plan.SourceIn < Time.Zero)
            {
                throw new EditException($"El clip «{name}» empezaría antes del inicio del archivo.");
            }

            if (plan.SourceOut <= plan.SourceIn)
            {
                throw new EditException($"El clip «{name}» se quedaría sin duración.");
            }

            if (plan.SourceOut > ctx.MediaOf(plan.Clip).Duration)
            {
                throw new EditException($"El clip «{name}» llegaría más allá del final del archivo.");
            }

            if (plan.Start < Time.Zero)
            {
                throw new EditException($"El clip «{name}» empezaría antes del inicio de la línea de tiempo.");
            }
        }
    }

    // Los planes no se comparan entre sí: cada operación los construye pegados, sin solape.
    public static void CheckOverlaps(IReadOnlyCollection<TrimPlan> plans)
    {
        var changed = new HashSet<Clip>(plans.Select(p => p.Clip));
        foreach (TrimPlan plan in plans)
        {
            foreach (Clip other in plan.Track.Clips)
            {
                if (!changed.Contains(other) && plan.Start < other.End && other.Start < plan.End)
                {
                    throw new EditException($"El clip «{plan.Clip.Id}» chocaría con el clip «{other.Id}».");
                }
            }
        }
    }

    public static void CheckAndApply(EditContext ctx, IReadOnlyCollection<TrimPlan> plans)
    {
        foreach (TrimPlan plan in plans)
        {
            EditContext.EnsureUnlocked(plan.Track);
        }

        CheckBounds(ctx, plans);
        CheckOverlaps(plans);
        Apply(plans);
    }

    public static void Apply(IEnumerable<TrimPlan> plans)
    {
        var tracks = new HashSet<Track>();
        foreach (TrimPlan plan in plans)
        {
            plan.Clip.Start = plan.Start;
            plan.Clip.SourceIn = plan.SourceIn;
            plan.Clip.SourceOut = plan.SourceOut;
            tracks.Add(plan.Track);
        }

        foreach (Track track in tracks)
        {
            track.SortClips();
        }
    }

    // Después de un ripple el resultado depende de qué clips se corrieron, así que se revisa ya aplicado.
    public static void EnsureNoOverlaps(IEnumerable<Track> tracks)
    {
        foreach (Track track in tracks)
        {
            track.SortClips();
            for (int i = 1; i < track.Clips.Count; i++)
            {
                if (track.Clips[i].Start < track.Clips[i - 1].End)
                {
                    throw new EditException($"El clip «{track.Clips[i - 1].Id}» chocaría con el clip «{track.Clips[i].Id}».");
                }
            }
        }
    }

    // Quita el enlace a los clips que quedaron solos. Nunca toca una pista bloqueada: un enlace de un
    // solo miembro es inofensivo y así la operación no cambia en silencio una pista bloqueada.
    public static void DropOrphanLinks(Sequence sequence, IEnumerable<string?> linkIds)
    {
        foreach (string linkId in linkIds.OfType<string>().Distinct(StringComparer.Ordinal))
        {
            List<(Track Track, Clip Clip)> members =
            [
                .. sequence.AllTracks.SelectMany(t => t.Clips.Where(c => c.LinkId == linkId).Select(c => (t, c))),
            ];
            if (members.Count == 1 && !members[0].Track.Locked)
            {
                members[0].Clip.LinkId = null;
            }
        }
    }

    public static bool StrictlyContains(Clip clip, Time at) => clip.Start < at && at < clip.End;

    // El borde de entrada mueve también el inicio; el de salida no.
    public static TrimPlan EdgePlan(TrimPlan plan, TrimEdge edge, Time delta) => edge == TrimEdge.In
        ? plan with { Start = plan.Start + delta, SourceIn = plan.SourceIn + delta }
        : plan with { SourceOut = plan.SourceOut + delta };
}
