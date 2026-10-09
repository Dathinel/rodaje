using FotoToma.Core.Model;
using FotoToma.Core.Timing;

namespace FotoToma.Core.Editing;

/// <summary>
/// Cambio planeado sobre un clip: los tres instantes que tendrá cuando se aplique la edición.
/// Las operaciones de recorte primero planean todos los cambios, los comprueban y solo entonces los
/// aplican, para no dejar el modelo a medias cuando algo no se puede hacer.
/// </summary>
internal readonly record struct TrimPlan(Track Track, Clip Clip, Time Start, Time SourceIn, Time SourceOut)
{
    public Time End => Start + (SourceOut - SourceIn);

    /// <summary>Plan que deja el clip como está, para irle cambiando campos con <c>with</c>.</summary>
    public static TrimPlan Of(Track track, Clip clip) => new(track, clip, clip.Start, clip.SourceIn, clip.SourceOut);
}

/// <summary>Piezas compartidas por los recortes y la gestión de pistas.</summary>
internal static class TrimmingSupport
{
    /// <summary>Clip y sus enlazados, con un error claro si el identificador no existe.</summary>
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

    /// <summary>Clip cuyo inicio coincide exactamente con el final de <paramref name="clip"/>.</summary>
    public static Clip? AdjacentNext(Track track, Clip clip) =>
        track.Clips.Find(c => !ReferenceEquals(c, clip) && c.Start == clip.End);

    /// <summary>Clip cuyo final coincide exactamente con el inicio de <paramref name="clip"/>.</summary>
    public static Clip? AdjacentPrevious(Track track, Clip clip) =>
        track.Clips.Find(c => !ReferenceEquals(c, clip) && c.End == clip.Start);

    /// <summary>
    /// Comprueba los límites de cada plan: el origen no puede empezar antes de cero ni pasar el final
    /// del archivo, la duración debe ser positiva y el clip no puede quedar antes del inicio de la línea
    /// de tiempo.
    /// </summary>
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

    /// <summary>
    /// Comprueba que ningún clip planeado se monte sobre un clip de su pista que no cambia. Los clips
    /// planeados entre sí no se comparan: cada operación los construye pegados, sin hueco ni solape.
    /// </summary>
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

    /// <summary>Comprueba los planes, verifica que las pistas no estén bloqueadas y los aplica.</summary>
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

    /// <summary>
    /// Revisa, ya aplicada la edición, que ningún par de clips seguidos de las pistas dadas se solape.
    /// Se usa después de un ripple, donde el resultado depende de qué clips se corrieron.
    /// </summary>
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

    /// <summary>
    /// Quita el enlace a los clips que se quedaron solos en su grupo, por ejemplo cuando se borró la
    /// pista de su pareja o cuando su pareja pasó a otro enlace.
    /// </summary>
    /// <remarks>
    /// Esta limpieza nunca toca un clip de una pista bloqueada: ese clip conserva su enlace aunque se
    /// haya quedado solo. Un enlace con un único miembro es inofensivo (el validador lo admite y el clip
    /// se edita como si fuera suelto), y así la operación que lo dejó huérfano no modifica en silencio una
    /// pista bloqueada ni la rechaza el comando que comprueba que las pistas bloqueadas no cambien. No hace
    /// falta limpiarlo después: el enlace huérfano puede quedarse indefinidamente.
    /// </remarks>
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

    /// <summary>El instante cae dentro del clip sin tocar sus bordes.</summary>
    public static bool StrictlyContains(Clip clip, Time at) => clip.Start < at && at < clip.End;

    /// <summary>Recorte simple de un borde: el de entrada mueve también el inicio, el de salida no.</summary>
    public static TrimPlan EdgePlan(TrimPlan plan, TrimEdge edge, Time delta) => edge == TrimEdge.In
        ? plan with { Start = plan.Start + delta, SourceIn = plan.SourceIn + delta }
        : plan with { SourceOut = plan.SourceOut + delta };
}
