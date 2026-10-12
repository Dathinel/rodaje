using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

/// <summary>
/// Mapeo que tendrá un clip; los recortes planean y comprueban todo antes de aplicar nada. Si el cambio no da un clip
/// (se queda sin duración o pasa del largo máximo), <see cref="Map"/> es nulo y <see cref="Problem"/> dice por qué.
/// </summary>
internal readonly record struct TrimPlan(Track Track, Clip Clip, ClipTimeMap? Map, string? Problem = null)
{
    public Time Start => Map!.Start;

    public Time End => Map!.End;

    public static TrimPlan Of(Track track, Clip clip) => new(track, clip, clip.TimeMap);

    /// <summary>El clip empieza en <paramref name="start"/> con el mismo final y el mismo origen en cada instante.</summary>
    public TrimPlan TrimIn(Time start) => Map is { } map && start >= map.End ? Empty() : Change(m => m.TrimIn(start));

    /// <summary>El clip termina en <paramref name="end"/> con el mismo inicio.</summary>
    public TrimPlan TrimOut(Time end) => Map is { } map && end <= map.Start ? Empty() : Change(m => m.TrimOut(end));

    public TrimPlan MoveTo(Time start) => Change(m => m.MoveTo(start));

    public TrimPlan Slip(Time delta) => Change(m => m.Slip(delta));

    private TrimPlan Empty() => this with { Map = null, Problem = "se quedaría sin duración" };

    private TrimPlan Change(Func<ClipTimeMap, ClipTimeMap> change)
    {
        if (Map is null)
        {
            return this;
        }

        try
        {
            return this with { Map = change(Map) };
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        {
            return this with { Map = null, Problem = "pasaría del largo máximo de un clip" };
        }
    }
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

    /// <summary>Los títulos no tienen material: deslizar o cambiar la velocidad no tiene sentido en ellos.</summary>
    public static void EnsureNoGenerator(List<(Track Track, Clip Clip)> group, string action)
    {
        if (group.Find(m => m.Clip.Generator is not null).Clip is { } generator)
        {
            throw new EditException($"El clip «{generator.Id}» es un título y no tiene material: no se puede {action}.");
        }
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
            if (plan.Map is not { } map)
            {
                throw new EditException($"El clip «{name}» {plan.Problem}.");
            }

            // Un generador no tiene archivo: su origen virtual no tiene principio ni final.
            if (plan.Clip.Generator is null)
            {
                if (map.SourceIn < Time.Zero)
                {
                    throw new EditException($"El clip «{name}» empezaría antes del inicio del archivo.");
                }

                Time limit = plan.Clip.IsCompound ? CompoundOps.SourceLimit(ctx.Project, plan.Clip) : ctx.MediaOf(plan.Clip).Duration;
                if (map.SourceOut > limit)
                {
                    throw new EditException($"El clip «{name}» llegaría más allá del final del archivo.");
                }
            }

            if (map.Start < Time.Zero)
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

    /// <summary>Escribe en sus clips los mapeos ya comprobados con <see cref="CheckBounds"/>.</summary>
    public static void Apply(IEnumerable<TrimPlan> plans)
    {
        var tracks = new HashSet<Track>();
        foreach (TrimPlan plan in plans)
        {
            plan.Map!.ApplyTo(plan.Clip);
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

    // El borde de entrada mueve también el inicio; el de salida no. Los deltas son de la línea de tiempo.
    public static TrimPlan EdgePlan(TrimPlan plan, TrimEdge edge, Time delta) => edge == TrimEdge.In
        ? plan.TrimIn(plan.Clip.Start + delta)
        : plan.TrimOut(plan.Clip.End + delta);
}
