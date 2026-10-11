using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

/// <remarks>
/// Un <c>delta</c> positivo mueve el borde, corte o clip hacia la derecha; los clips enlazados se editan
/// juntos con el mismo <c>delta</c>.
/// </remarks>
public static partial class TimelineEdits
{
    /// <summary>Corta en dos el clip y los enlazados que pasen por <paramref name="at"/>.</summary>
    /// <remarks>
    /// La pieza izquierda conserva el identificador; la derecha recibe uno nuevo, un enlace derivado y
    /// copias de los efectos. Los keyframes están en tiempo del origen y no cambian.
    /// </remarks>
    /// <returns>Identificadores de las piezas derechas nuevas; la del clip pedido va primero.</returns>
    public static IReadOnlyList<string> Split(EditContext ctx, string clipId, Time at)
    {
        List<(Track Track, Clip Clip)> group = TrimmingSupport.ResolveGroup(ctx, clipId);
        Clip primary = group[0].Clip;
        if (!TrimmingSupport.StrictlyContains(primary, at))
        {
            throw new EditException($"El instante {at} no cae dentro del clip «{primary.Id}», así que no se puede cortar ahí.");
        }

        List<(Track Track, Clip Clip)> toCut = group.FindAll(m => TrimmingSupport.StrictlyContains(m.Clip, at));
        foreach ((Track track, _) in toCut)
        {
            EditContext.EnsureUnlocked(track);
        }

        var newIds = new List<string>(toCut.Count);
        foreach ((Track track, Clip left) in toCut)
        {
            Clip right = TrackOps.SplitOff(ctx, left, at);
            track.Clips.Insert(track.Clips.IndexOf(left) + 1, right);
            newIds.Add(right.Id);
        }

        ctx.EnsureValid();
        return newIds;
    }

    /// <summary>Mueve un borde sin correr nada más; nunca se monta sobre el vecino.</summary>
    public static void Trim(EditContext ctx, string clipId, TrimEdge edge, Time delta)
    {
        List<(Track Track, Clip Clip)> group = TrimmingSupport.ResolveGroup(ctx, clipId);
        List<TrimPlan> plans = group.ConvertAll(m => TrimmingSupport.EdgePlan(TrimPlan.Of(m.Track, m.Clip), edge, delta));
        TrimmingSupport.CheckAndApply(ctx, plans);
        ctx.EnsureValid();
    }

    /// <summary>
    /// Recorta y corre lo que viene después; en el borde de entrada el clip no cambia de inicio. Con
    /// <see cref="RippleScope.Track"/> cada pista del grupo se corre desde el final de su propio clip.
    /// </summary>
    public static void RippleTrim(EditContext ctx, string clipId, TrimEdge edge, Time delta, RippleScope scope)
    {
        List<(Track Track, Clip Clip)> group = TrimmingSupport.ResolveGroup(ctx, clipId);
        List<Time> originalEnds = group.ConvertAll(m => m.Clip.End);

        List<TrimPlan> plans = group.ConvertAll(m =>
        {
            TrimPlan plan = TrimPlan.Of(m.Track, m.Clip);
            return edge == TrimEdge.In
                ? plan.TrimIn(plan.Clip.Start + delta).MoveTo(plan.Clip.Start)
                : plan.TrimOut(plan.Clip.End + delta);
        });

        foreach (TrimPlan plan in plans)
        {
            EditContext.EnsureUnlocked(plan.Track);
        }

        TrimmingSupport.CheckBounds(ctx, plans);
        TrimmingSupport.Apply(plans);

        // En una pista, cada miembro corre lo suyo desde su propio final: los enlazados pueden tener distinto largo.
        Time shift = edge == TrimEdge.In ? -delta : delta;
        var edited = new HashSet<Clip>(plans.Select(p => p.Clip));
        IReadOnlyList<Track> shifted = scope.IncludesAllTracks()
            ? ctx.RippleShift(plans.ConvertAll(p => p.Track), originalEnds[0], shift, scope, edited)
            : ctx.RippleEach(plans.Select((p, i) => (p.Track, originalEnds[i], shift)), edited);

        TrimmingSupport.EnsureNoOverlaps(scope.IncludesAllTracks() ? ctx.Sequence.AllTracks : shifted);
        ctx.EnsureValid();
    }

    /// <summary>Mueve el corte con el clip pegado a la derecha; sin vecino equivale a recortar la salida.</summary>
    public static void Roll(EditContext ctx, string clipId, Time delta)
    {
        List<(Track Track, Clip Clip)> group = TrimmingSupport.ResolveGroup(ctx, clipId);
        var plans = new List<TrimPlan>();
        foreach ((Track track, Clip clip) in group)
        {
            TrimPlan plan = TrimPlan.Of(track, clip);
            plans.Add(plan.TrimOut(clip.End + delta));

            Clip? next = TrimmingSupport.AdjacentNext(track, clip);
            if (next is not null)
            {
                plans.Add(TrimmingSupport.EdgePlan(TrimPlan.Of(track, next), TrimEdge.In, delta));
            }
        }

        TrimmingSupport.CheckAndApply(ctx, plans);
        ctx.EnsureValid();
    }

    /// <summary>Muestra otra parte del origen sin cambiar posición ni duración; <paramref name="delta"/> es tiempo del origen.</summary>
    public static void Slip(EditContext ctx, string clipId, Time delta)
    {
        List<(Track Track, Clip Clip)> group = TrimmingSupport.ResolveGroup(ctx, clipId);
        TrimmingSupport.EnsureNoGenerator(group, "deslizar su contenido");
        List<TrimPlan> plans = group.ConvertAll(m =>
        {
            TrimPlan plan = TrimPlan.Of(m.Track, m.Clip);
            return plan.Slip(delta);
        });

        TrimmingSupport.CheckAndApply(ctx, plans);
        ctx.EnsureValid();
    }

    /// <summary>Mueve el clip entre sus vecinos pegados, que se ajustan para conservar la duración total.</summary>
    public static void Slide(EditContext ctx, string clipId, Time delta)
    {
        List<(Track Track, Clip Clip)> group = TrimmingSupport.ResolveGroup(ctx, clipId);
        var plans = new List<TrimPlan>();
        foreach ((Track track, Clip clip) in group)
        {
            Clip? previous = TrimmingSupport.AdjacentPrevious(track, clip);
            if (previous is not null)
            {
                plans.Add(TrimmingSupport.EdgePlan(TrimPlan.Of(track, previous), TrimEdge.Out, delta));
            }

            TrimPlan plan = TrimPlan.Of(track, clip);
            plans.Add(plan.MoveTo(clip.Start + delta));

            Clip? next = TrimmingSupport.AdjacentNext(track, clip);
            if (next is not null)
            {
                plans.Add(TrimmingSupport.EdgePlan(TrimPlan.Of(track, next), TrimEdge.In, delta));
            }
        }

        TrimmingSupport.CheckAndApply(ctx, plans);
        ctx.EnsureValid();
    }
}
