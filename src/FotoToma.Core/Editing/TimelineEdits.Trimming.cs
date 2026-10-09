using FotoToma.Core.Model;
using FotoToma.Core.Timing;

namespace FotoToma.Core.Editing;

/// <summary>
/// Operaciones de edición sobre la secuencia de un <see cref="EditContext"/>. Cambian el modelo en el
/// sitio y lanzan <see cref="EditException"/> cuando la edición no se puede hacer; el comando que las
/// envuelve guarda una copia antes y la restaura si algo falla.
/// </summary>
/// <remarks>
/// Convención de signos para los recortes: un borde, un corte o un clip se mueve <c>delta</c> en la
/// línea de tiempo, y un <c>delta</c> positivo lo lleva hacia la derecha (más tarde). Los clips
/// enlazados, como un video y su audio, se editan siempre juntos y con el mismo <c>delta</c>.
/// </remarks>
public static partial class TimelineEdits
{
    /// <summary>
    /// Corta el clip, y sus enlazados que también pasen por ese instante, en dos piezas.
    /// </summary>
    /// <remarks>
    /// La pieza izquierda conserva el identificador. La derecha recibe uno nuevo, un enlace derivado
    /// (para que el video y el audio cortados sigan unidos entre sí) y una copia de los efectos con
    /// identificadores nuevos; los keyframes están en tiempo del origen y no cambian.
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
            Time cutInSource = left.ToSourceTime(at);
            var right = new Clip
            {
                Id = ctx.Ids.NewId("clip"),
                MediaId = left.MediaId,
                SourceIn = cutInSource,
                SourceOut = left.SourceOut,
                Start = at,
                Enabled = left.Enabled,
                LinkId = ctx.DerivedLinkId(left.LinkId),
                Effects = left.Effects.ConvertAll(effect =>
                {
                    EffectInstance copy = effect.Clone();
                    copy.Id = ctx.Ids.NewId("fx");
                    return copy;
                }),
            };

            left.SourceOut = cutInSource;
            track.Clips.Insert(track.Clips.IndexOf(left) + 1, right);
            newIds.Add(right.Id);
        }

        ctx.EnsureValid();
        return newIds;
    }

    /// <summary>
    /// Recorte simple: mueve el borde de entrada o de salida sin correr nada más, así que puede abrir un
    /// hueco o cerrar uno, pero nunca montarse sobre el clip vecino.
    /// </summary>
    public static void Trim(EditContext ctx, string clipId, TrimEdge edge, Time delta)
    {
        List<(Track Track, Clip Clip)> group = TrimmingSupport.ResolveGroup(ctx, clipId);
        List<TrimPlan> plans = group.ConvertAll(m => TrimmingSupport.EdgePlan(TrimPlan.Of(m.Track, m.Clip), edge, delta));
        TrimmingSupport.CheckAndApply(ctx, plans);
        ctx.EnsureValid();
    }

    /// <summary>
    /// Recorte con ripple: el clip se alarga o se acorta y todo lo que viene después se corre para que no
    /// queden huecos ni solapes. En el borde de entrada el clip sigue empezando en el mismo lugar y lo que
    /// cambia es qué parte del archivo muestra.
    /// </summary>
    public static void RippleTrim(EditContext ctx, string clipId, TrimEdge edge, Time delta, RippleScope scope)
    {
        List<(Track Track, Clip Clip)> group = TrimmingSupport.ResolveGroup(ctx, clipId);
        Time originalEnd = group[0].Clip.End;

        List<TrimPlan> plans = group.ConvertAll(m =>
        {
            TrimPlan plan = TrimPlan.Of(m.Track, m.Clip);
            return edge == TrimEdge.In
                ? plan with { SourceIn = plan.SourceIn + delta }
                : plan with { SourceOut = plan.SourceOut + delta };
        });

        foreach (TrimPlan plan in plans)
        {
            EditContext.EnsureUnlocked(plan.Track);
        }

        TrimmingSupport.CheckBounds(ctx, plans);
        TrimmingSupport.Apply(plans);

        Time shift = edge == TrimEdge.In ? -delta : delta;
        List<Track> editedTracks = plans.ConvertAll(p => p.Track);
        ctx.RippleShift(editedTracks, originalEnd, shift, scope, new HashSet<Clip>(plans.Select(p => p.Clip)));

        TrimmingSupport.EnsureNoOverlaps(scope == RippleScope.AllTracks ? ctx.Sequence.AllTracks : editedTracks);
        ctx.EnsureValid();
    }

    /// <summary>
    /// Mueve el corte entre el clip y el que va pegado justo después: uno se alarga lo que el otro se
    /// acorta, y la duración total no cambia. Si no hay un clip pegado se comporta como un recorte del
    /// borde de salida.
    /// </summary>
    public static void Roll(EditContext ctx, string clipId, Time delta)
    {
        List<(Track Track, Clip Clip)> group = TrimmingSupport.ResolveGroup(ctx, clipId);
        var plans = new List<TrimPlan>();
        foreach ((Track track, Clip clip) in group)
        {
            TrimPlan plan = TrimPlan.Of(track, clip);
            plans.Add(plan with { SourceOut = plan.SourceOut + delta });

            Clip? next = TrimmingSupport.AdjacentNext(track, clip);
            if (next is not null)
            {
                plans.Add(TrimmingSupport.EdgePlan(TrimPlan.Of(track, next), TrimEdge.In, delta));
            }
        }

        TrimmingSupport.CheckAndApply(ctx, plans);
        ctx.EnsureValid();
    }

    /// <summary>
    /// Desliza el contenido dentro del clip: muestra otra parte del archivo sin cambiar dónde está ni
    /// cuánto dura en la línea de tiempo.
    /// </summary>
    public static void Slip(EditContext ctx, string clipId, Time delta)
    {
        List<(Track Track, Clip Clip)> group = TrimmingSupport.ResolveGroup(ctx, clipId);
        List<TrimPlan> plans = group.ConvertAll(m =>
        {
            TrimPlan plan = TrimPlan.Of(m.Track, m.Clip);
            return plan with { SourceIn = plan.SourceIn + delta, SourceOut = plan.SourceOut + delta };
        });

        TrimmingSupport.CheckAndApply(ctx, plans);
        ctx.EnsureValid();
    }

    /// <summary>
    /// Mueve el clip entre sus vecinos pegados: el de antes se alarga o se acorta para seguir tocándolo y
    /// el de después hace lo contrario, así el clip conserva su contenido y la secuencia su duración. Si
    /// no tiene vecinos pegados el clip simplemente se mueve.
    /// </summary>
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
            plans.Add(plan with { Start = plan.Start + delta });

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
