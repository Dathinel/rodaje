using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

/// <summary>Cortes en el cursor que abarcan varias pistas y el borrado de un lado del cursor (Q/W).</summary>
public static partial class TimelineEdits
{
    /// <summary>
    /// Corta en <paramref name="at"/> todos los clips de las pistas desbloqueadas que pasan por ese instante, en una sola
    /// edición. Un clip enlazado con otro de una pista bloqueada se corta igual; la pareja bloqueada queda entera y
    /// enlazada con la pieza izquierda.
    /// </summary>
    /// <remarks>
    /// Como en <see cref="Split"/>, la pieza izquierda conserva el identificador y la derecha recibe uno nuevo; las
    /// piezas derechas de un mismo enlace comparten un enlace derivado.
    /// </remarks>
    /// <returns>Identificadores de las piezas derechas nuevas, en el orden de las pistas (video y luego audio).</returns>
    public static IReadOnlyList<string> SplitAllTracks(EditContext ctx, Time at)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        List<(Track Track, Clip Clip)> toCut = ClipsCrossing(ctx.Sequence, at);
        if (toCut.Count == 0)
        {
            throw new EditException($"Ninguna pista desbloqueada tiene un clip que pase por {at}, así que no hay nada que cortar.");
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

    /// <summary>Hay algún clip de una pista desbloqueada que pasa por <paramref name="at"/> (sin contar sus bordes).</summary>
    public static bool CanSplitAllTracks(Sequence sequence, Time at)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        return ClipsCrossing(sequence, at).Count > 0;
    }

    /// <summary>
    /// Borra de cada clip pedido, y de sus enlazados, la parte a un lado de <paramref name="at"/>: con
    /// <see cref="TrimEdge.In"/> lo que hay antes (Q), con <see cref="TrimEdge.Out"/> lo que hay después (W). Solo se
    /// tocan los clips que pasan por <paramref name="at"/>, y cada uno conserva su identificador.
    /// </summary>
    /// <remarks>
    /// Sin <paramref name="ripple"/> queda el hueco. Con <paramref name="ripple"/> el resultado es el de cortar en
    /// <paramref name="at"/> y hacer <see cref="RippleDelete"/> de las piezas borradas: con <see cref="RippleScope.Track"/>
    /// cada pista (y las de los enlazados de lo que se corre) cierra su propio tramo, y con «todas las pistas» se quita de
    /// toda la secuencia la unión de los tramos, siempre que ningún otro clip los atraviese. Con
    /// <see cref="TrimEdge.In"/> y ripple, el clip queda empezando donde empezaba.
    /// </remarks>
    public static void DeleteToPoint(EditContext ctx, IEnumerable<string> clipIds, Time at, TrimEdge side, bool ripple, RippleScope scope)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        List<(Track Track, Clip Clip)> targets = TrackOps.ExpandSelection(ctx, clipIds)
            .FindAll(t => TrimmingSupport.StrictlyContains(t.Clip, at));
        if (targets.Count == 0)
        {
            throw new EditException($"Ningún clip de la selección pasa por {at}, así que no hay nada que borrar a ese lado.");
        }

        var plans = new List<TrimPlan>(targets.Count);
        var spans = new List<(Track Track, Time Start, Time End)>(targets.Count);
        foreach ((Track track, Clip clip) in targets)
        {
            EditContext.EnsureUnlocked(track);
            TrimPlan plan = TrimPlan.Of(track, clip);
            if (side == TrimEdge.In)
            {
                plans.Add(plan.TrimIn(at));
                spans.Add((track, clip.Start, at));
            }
            else
            {
                plans.Add(plan.TrimOut(at));
                spans.Add((track, at, clip.End));
            }
        }

        TrimmingSupport.CheckBounds(ctx, plans);
        TrimmingSupport.Apply(plans);
        if (ripple)
        {
            var trimmed = new HashSet<Clip>(targets.Select(t => t.Clip));
            if (scope.IncludesAllTracks())
            {
                CloseSpansOnAllTracks(ctx, spans, trimmed, scope);
            }
            else
            {
                CloseSpansPerTrack(ctx, spans, trimmed);
            }
        }

        ctx.EnsureValid();
    }

    /// <summary>Clips de las pistas desbloqueadas que pasan por <paramref name="at"/>, sin contar sus bordes.</summary>
    private static List<(Track Track, Clip Clip)> ClipsCrossing(Sequence sequence, Time at)
    {
        var result = new List<(Track Track, Clip Clip)>();
        foreach (Track track in sequence.AllTracks.Where(t => !t.Locked))
        {
            if (track.Clips.Find(c => TrimmingSupport.StrictlyContains(c, at)) is { } clip)
            {
                result.Add((track, clip));
            }
        }

        return result;
    }

    /// <summary>
    /// Cada pista cierra sus tramos, y las pistas de los enlazados de lo que se corre cierran los mismos (como
    /// <see cref="RippleDelete"/> con <see cref="RippleScope.Track"/>). Los clips recortados no arrastran a sus parejas,
    /// que ya cierran su propio tramo; con Q el recortado empieza justo al final de su tramo y se corre con lo que sigue.
    /// </summary>
    private static void CloseSpansPerTrack(EditContext ctx, List<(Track Track, Time Start, Time End)> spans, HashSet<Clip> trimmed)
    {
        var spansByTrack = new Dictionary<Track, List<(Time Start, Time End)>>();
        var addedTracks = new HashSet<Track>();
        foreach ((Track track, Time start, Time end) in spans)
        {
            foreach (Track t in ctx.LinkedRippleTracks([track], end, trimmed))
            {
                if (!spansByTrack.TryGetValue(t, out List<(Time Start, Time End)>? list))
                {
                    list = [];
                    spansByTrack[t] = list;
                }

                list.Add((start, end));
                if (t != track)
                {
                    addedTracks.Add(t);
                }
            }
        }

        var merged = spansByTrack.ToDictionary(p => p.Key, p => TrackOps.MergeSpans(p.Value));
        foreach (Track track in addedTracks)
        {
            foreach ((Time start, Time end) in merged[track])
            {
                EditContext.EnsureCanClose(track, end, start - end, trimmed);
            }
        }

        foreach ((Track track, List<(Time Start, Time End)> list) in merged)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                (Time start, Time end) = list[i];
                EditContext.ShiftClips(track, end, start - end);
            }
        }

        TrimmingSupport.EnsureNoOverlaps(merged.Keys);
    }

    /// <summary>Quita de todas las pistas desbloqueadas la unión de los tramos (y de los marcadores, si el modo lo pide).</summary>
    private static void CloseSpansOnAllTracks(EditContext ctx, List<(Track Track, Time Start, Time End)> spans, HashSet<Clip> trimmed, RippleScope scope)
    {
        List<(Time Start, Time End)> merged = TrackOps.MergeSpans(spans.Select(s => (s.Start, s.End)));
        foreach ((Time start, Time end) in merged)
        {
            TrackOps.EnsureNothingStraddles(ctx, start, end, trimmed);
        }

        List<Track> editedTracks = [.. spans.Select(s => s.Track).Distinct()];
        for (int i = merged.Count - 1; i >= 0; i--)
        {
            (Time start, Time end) = merged[i];
            ctx.RippleShift(editedTracks, end, start - end, scope);
        }

        TrimmingSupport.EnsureNoOverlaps(ctx.Sequence.AllTracks);
    }
}
