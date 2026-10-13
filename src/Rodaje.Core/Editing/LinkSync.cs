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
    /// línea de tiempo, o <c>null</c> si no se pueden comparar (velocidades distintas o cuadro congelado).
    /// </summary>
    public static Time? OffsetBetween(Clip reference, Clip clip)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(clip);
        bool plain = reference.Speed is null && clip.Speed is null;
        if (!plain && (reference.Speed is null || clip.Speed is null || !reference.Speed.ContentEquals(clip.Speed) || reference.Speed.Freeze))
        {
            return null;
        }

        if (plain)
        {
            return (clip.SourceIn - clip.Start) - (reference.SourceIn - reference.Start);
        }

        // Con la misma velocidad constante el desfase es exacto: la diferencia de entradas menos lo que recorre el origen
        // entre los dos inicios (medir un instante con el mapeo arrastra el redondeo de cada clip).
        if (reference.Speed!.Curve is not null || reference.Speed.Reverse)
        {
            return clip.Start == reference.Start && clip.SourceIn == reference.SourceIn ? Time.Zero : null;
        }

        Rational rate = reference.Speed.Rate;
        Int128 travelled = (Int128)(clip.Start - reference.Start).Ticks * rate.Num / rate.Den;
        return Time.FromTicks((long)((Int128)(clip.SourceIn - reference.SourceIn).Ticks - travelled));
    }

    /// <summary>Los desfases de ahora, sin signo (cuál mide a cuál depende del orden de las pistas).</summary>
    internal static HashSet<Time> Snapshot(Sequence sequence) =>
        [.. Find(sequence).Select(o => o.Offset < Time.Zero ? -o.Offset : o.Offset)];

    /// <summary>Falla si la edición dejó un desfase que no estaba (la edición se deshace entera).</summary>
    internal static void EnsureNoNewDesync(Sequence sequence, HashSet<Time> before, string action)
    {
        foreach (SyncOffset offset in Find(sequence))
        {
            Time size = offset.Offset < Time.Zero ? -offset.Offset : offset.Offset;
            if (!before.Contains(size))
            {
                throw new EditException($"{action} desincronizaría «{offset.ClipId}» de su enlazado «{offset.PartnerId}»: hazlo sin ripple o con todas las pistas.");
            }
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
