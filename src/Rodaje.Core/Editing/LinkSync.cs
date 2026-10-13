using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

/// <summary>Cuánto está corrido un clip respecto de otro enlazado del mismo medio.</summary>
/// <param name="ClipId">El clip que se mide.</param>
/// <param name="PartnerId">Su enlazado de referencia (el de video si lo hay).</param>
/// <param name="Offset">
/// Positivo: el clip muestra material de más adelante que su pareja en el mismo instante (va adelantado).
/// </param>
public readonly record struct SyncOffset(string ClipId, string PartnerId, Time Offset)
{
    /// <summary>El desfase en cuadros de <paramref name="rate"/>, con signo y redondeado al más cercano.</summary>
    public double Frames(Rational rate) => Offset.Ticks / (double)Time.FromFrames(1, rate).Ticks;
}

/// <summary>Cómo se vuelve a sincronizar un clip con su pareja.</summary>
public enum ResyncMode
{
    /// <summary>Se mueve el clip en la línea de tiempo (cambia su lugar, no lo que muestra).</summary>
    Move,

    /// <summary>Se desliza su contenido (slip): queda en su lugar mostrando el material sincronizado.</summary>
    Slip,
}

/// <summary>
/// La sincronía entre clips enlazados de un mismo medio (el video de una toma y su sonido). Solo se puede romper editando
/// un clip con «ignorar grupos»; desenlazar deja de medirla.
/// </summary>
public static class LinkSync
{
    /// <summary>
    /// Los clips desincronizados de la secuencia: cada clip enlazado de un medio con imagen y sonido cuyo material no
    /// coincide con el de su pareja. La pareja es el primer enlazado del mismo medio en una pista de video, o en su
    /// defecto el primero en orden de pistas. Los clips con velocidad distinta de su pareja no se miden.
    /// </summary>
    public static IReadOnlyList<SyncOffset> Find(Sequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        var byLink = new Dictionary<string, List<Clip>>(StringComparer.Ordinal);
        foreach (Track track in sequence.VideoTracks.Concat(sequence.AudioTracks))
        {
            foreach (Clip clip in track.Clips)
            {
                if (clip.LinkId is { } link && clip.Generator is null && !clip.IsCompound)
                {
                    if (!byLink.TryGetValue(link, out List<Clip>? list))
                    {
                        byLink[link] = list = [];
                    }

                    list.Add(clip);
                }
            }
        }

        var result = new List<SyncOffset>();
        foreach (List<Clip> members in byLink.Values)
        {
            foreach (IGrouping<string, Clip> same in members.GroupBy(c => c.MediaId, StringComparer.Ordinal))
            {
                Clip reference = same.First();
                foreach (Clip clip in same.Skip(1))
                {
                    if (OffsetBetween(reference, clip) is { } offset && offset != Time.Zero)
                    {
                        result.Add(new SyncOffset(clip.Id, reference.Id, offset));
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Material de <paramref name="clip"/> menos material de <paramref name="reference"/> en un mismo instante de la
    /// línea de tiempo, o <c>null</c> si no se pueden comparar (velocidades que mueven distinto el origen o cuadro
    /// congelado). Hacia adelante y en reversa, a velocidad constante o con la misma curva en la misma fase, el desfase es
    /// exacto; con la misma curva en otra fase cambia a lo largo del clip y se da el mayor de los dos extremos del tramo
    /// común.
    /// </summary>
    public static Time? OffsetBetween(Clip reference, Clip clip)
    {
        if (Relate(reference, clip) is not { } relation)
        {
            return null;
        }

        return relation.Phase == Time.Zero ? relation.Offset : OffsetAtKnots(reference.Speed!, relation);
    }

    /// <summary>
    /// La relación entre los mapeos de dos enlazados, que recortar y dividir no cambian: el desfase y, con curva, la
    /// diferencia de fase (<c>Offset − Start</c>). A velocidad constante la fase entra en el desfase y vale cero.
    /// </summary>
    internal readonly record struct Relation(Time Offset, Time Phase)
    {
        public bool InSync => Offset == Time.Zero && Phase == Time.Zero;
    }

    private static Relation? Relate(Clip reference, Clip clip)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(clip);
        // Una velocidad a 1× sin curva, reversa ni congelado (la que deja recortar o quitar la reversa) mueve el origen como
        // un clip sin velocidad, sea cual sea su Offset: compararla por contenido escondía el desfase de un par así.
        ClipSpeed? referenceSpeed = reference.Speed is { IsIdentity: false } rs ? rs : null;
        ClipSpeed? clipSpeed = clip.Speed is { IsIdentity: false } cs ? cs : null;
        bool plain = referenceSpeed is null && clipSpeed is null;
        // Solo cuenta lo que mueve el origen (velocidad, curva, reversa, congelado): con ContentEquals un distinto Offset o
        // PreservePitch dejaba sin medir dos enlazados a la misma velocidad constante.
        if (!plain && (referenceSpeed is null || clipSpeed is null || !referenceSpeed.SameTiming(clipSpeed) || referenceSpeed.Freeze))
        {
            return null;
        }

        if (plain)
        {
            return new Relation((clip.SourceIn - clip.Start) - (reference.SourceIn - reference.Start), Time.Zero);
        }

        if (referenceSpeed!.Curve is { } curve && RetimeMath.CurveProblem(curve) is not null)
        {
            return null;
        }

        // Con p(τ) = ⌊I(Offset + τ)⌋ − ⌊I(Offset)⌋ y τ = t − Start, el clip muestra A + ⌊I(t + φ)⌋ hacia adelante, con
        // A = SourceIn − ⌊I(Offset)⌋, y A − 1 − ⌊I(t + φ)⌋ en reversa, con A = SourceOut + ⌊I(Offset)⌋; φ = Offset − Start.
        // Recortar y dividir conservan A y φ (es lo que pide ClipTimeMap.SameCourse), así que se comparan esos dos.
        RetimeMath math = RetimeMath.Of(referenceSpeed);
        bool reverse = referenceSpeed.Reverse;
        Int128 anchors = reverse
            ? ((Int128)clip.SourceOut.Ticks + math.Floor(clipSpeed!.Offset.Ticks)) - ((Int128)reference.SourceOut.Ticks + math.Floor(referenceSpeed.Offset.Ticks))
            : ((Int128)clip.SourceIn.Ticks - math.Floor(clipSpeed!.Offset.Ticks)) - ((Int128)reference.SourceIn.Ticks - math.Floor(referenceSpeed.Offset.Ticks));
        Int128 phase = ((Int128)clipSpeed.Offset.Ticks - clip.Start.Ticks) - ((Int128)referenceSpeed.Offset.Ticks - reference.Start.Ticks);
        if (referenceSpeed.Curve is not null)
        {
            // Con la misma fase los dos recorren la curva a la par y el desfase es A_clip − A_ref en todo instante.
            return new Relation(Time.FromTicks((long)anchors), Time.FromTicks((long)phase));
        }

        // A velocidad constante r, ⌊r(t + φ_clip)⌋ − ⌊r(t + φ_ref)⌋ vale ⌊r·Δφ⌋ o ⌈r·Δφ⌉ según el instante: se toma r·Δφ
        // truncado (exacto si es entero, y antisimétrico al cambiar de referencia). En reversa el origen corre hacia atrás,
        // así que lo recorrido se resta.
        Rational rate = referenceSpeed.Rate;
        Int128 travelled = phase * rate.Num / rate.Den;
        return new Relation(Time.FromTicks((long)(reverse ? anchors - travelled : anchors + travelled)), Time.Zero);
    }

    /// <summary>
    /// Dos enlazados con la misma curva en distinta fase: el desfase cambia a lo largo del clip. Se mide con el recorrido de
    /// cada uno (<c>A + ⌊I(t + φ)⌋</c>, también fuera del clip) en los instantes en que alguno pasa por un nudo de la curva
    /// y se da el de mayor tamaño (el primero si empatan). Así no depende de dónde estén recortados, cambia solo de signo
    /// al invertir el par y no se anula aunque los dos empiecen en el mismo cuadro; <c>null</c> si esos instantes salen
    /// del rango del mapeo.
    /// </summary>
    private static Time? OffsetAtKnots(ClipSpeed speed, Relation relation)
    {
        RetimeMath math = RetimeMath.Of(speed);
        SpeedCurve curve = speed.Curve!;
        Int128 phase = relation.Phase.Ticks;
        Int128 best = 0;
        Int128 bestAt = Int128.MaxValue;
        foreach (SpeedPoint point in curve.Points)
        {
            long knot = (long)((Int128)point.At * curve.Span.Ticks / SpeedCurve.AtScale);
            // Argumento de la referencia en el instante en que ella (fase 0) o el clip (fase φ) pasan por el nudo.
            foreach (Int128 atReference in (Int128[])[knot, knot - phase])
            {
                Int128 atClip = atReference + phase;
                if (Int128.Abs(atReference) > RetimeMath.MaxArgument || Int128.Abs(atClip) > RetimeMath.MaxArgument)
                {
                    return null;
                }

                Int128 travelled = (Int128)math.Floor((long)atClip) - math.Floor((long)atReference);
                Int128 value = relation.Offset.Ticks + (speed.Reverse ? -travelled : travelled);
                if (Int128.Abs(value) > Int128.Abs(best) || (Int128.Abs(value) == Int128.Abs(best) && atReference < bestAt))
                {
                    best = value;
                    bestAt = atReference;
                }
            }
        }

        return Time.FromTicks((long)best);
    }

    /// <summary>
    /// Cada par de enlazados del mismo medio una sola vez, con el de menor <see cref="Clip.Id"/> como referencia (así el
    /// signo no depende del orden de las pistas).
    /// </summary>
    private static IEnumerable<(Clip Reference, Clip Clip)> Pairs(Sequence sequence)
    {
        foreach (IGrouping<(string, string), Clip> same in sequence.VideoTracks.Concat(sequence.AudioTracks)
            .SelectMany(t => t.Clips)
            .Where(c => c.LinkId is not null && c.Generator is null && !c.IsCompound)
            .GroupBy(c => (c.LinkId!, c.MediaId)))
        {
            Clip[] members = [.. same.OrderBy(c => c.Id, StringComparer.Ordinal)];
            for (int i = 0; i < members.Length; i++)
            {
                for (int j = i + 1; j < members.Length; j++)
                {
                    yield return (members[i], members[j]);
                }
            }
        }
    }

    /// <summary>La relación de cada par de enlazados que se puede medir, por los ids del par.</summary>
    internal sealed class SyncSnapshot
    {
        internal SyncSnapshot(Dictionary<(string, string), Relation> pairs) => PairRelations = pairs;

        internal Dictionary<(string, string), Relation> PairRelations { get; }
    }

    /// <summary>Cómo está cada par de enlazados ahora: lo que <see cref="EnsureNoNewDesync"/> compara después.</summary>
    internal static SyncSnapshot Snapshot(Sequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        var pairs = new Dictionary<(string, string), Relation>();
        foreach ((Clip reference, Clip clip) in Pairs(sequence))
        {
            if (Relate(reference, clip) is { } relation)
            {
                pairs[(reference.Id, clip.Id)] = relation;
            }
        }

        return new SyncSnapshot(pairs);
    }

    /// <summary>
    /// Falla si la edición dejó desfasado un par que no lo estaba, o desfasado de otra manera (la edición se deshace
    /// entera). Se compara par a par: un desfase nuevo del mismo tamaño que el de otro par ya no pasa.
    /// </summary>
    internal static void EnsureNoNewDesync(Sequence sequence, SyncSnapshot before, string action)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(before);
        foreach ((Clip reference, Clip clip) in Pairs(sequence))
        {
            if (Relate(reference, clip) is not { InSync: false } relation
                || (before.PairRelations.TryGetValue((reference.Id, clip.Id), out Relation previous) && previous == relation))
            {
                continue;
            }

            throw new EditException($"{action} desincronizaría «{clip.Id}» de su enlazado «{reference.Id}»: hazlo sin ripple o con todas las pistas.");
        }
    }

    /// <summary>
    /// Vuelve a poner <paramref name="clipId"/> en sincronía con su pareja: moviéndolo (sin pisar nada) o deslizando su
    /// contenido (sin pasar del material). Un clip que ya está sincronizado no cambia.
    /// </summary>
    public static void Resync(EditContext ctx, string clipId, ResyncMode mode)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        (Track track, Clip clip) = ctx.Sequence.FindClip(clipId)
            ?? throw new EditException($"No existe el clip «{clipId}» en la secuencia.");
        SyncOffset sync = Find(ctx.Sequence).FirstOrDefault(s => s.ClipId == clipId);
        if (sync.ClipId is null)
        {
            throw new EditException($"El clip «{clipId}» ya está sincronizado con su pareja.");
        }

        EditContext.EnsureUnlocked(track);
        if (clip.Speed is not null)
        {
            throw new EditException("Volver a sincronizar solo funciona con clips a velocidad normal: mueve el clip a mano.");
        }

        if (mode == ResyncMode.Move)
        {
            // Correr el clip +d en la línea de tiempo resta d al desfase.
            Time start = clip.Start + sync.Offset;
            if (start < Time.Zero)
            {
                throw new EditException("El clip quedaría antes del inicio de la línea de tiempo: desliza su contenido en su lugar.");
            }

            var self = new HashSet<Clip> { clip };
            TrackOps.EnsureFree(track, start, start + clip.Duration, self);
            clip.Start = start;
            track.SortClips();
        }
        else
        {
            MediaRef media = ctx.MediaOf(clip);
            Time sourceIn = clip.SourceIn - sync.Offset;
            Time sourceOut = clip.SourceOut - sync.Offset;
            if (sourceIn < Time.Zero || sourceOut > media.Duration)
            {
                throw new EditException($"No hay material suficiente en «{media.Name}» para deslizarlo a su lugar: muévelo en vez de deslizarlo.");
            }

            clip.SourceIn = sourceIn;
            clip.SourceOut = sourceOut;
        }

        ctx.EnsureValid();
    }
}
