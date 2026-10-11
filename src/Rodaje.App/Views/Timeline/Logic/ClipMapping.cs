using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Views.Timeline;

/// <summary>
/// Lo que la línea de tiempo necesita del mapeo de un clip (<see cref="ClipTimeMap"/>) para dibujar y editar: dónde cae
/// cada keyframe, qué tramo del origen pedir para la onda y cuánto origen hay entre dos miniaturas.
/// </summary>
/// <remarks>Los keyframes van en el tiempo de los efectos (<see cref="Clip.ToEffectTime"/>): el del origen o, en un congelado, uno virtual.</remarks>
public static class ClipMapping
{
    /// <summary>Tramo cerrado del tiempo de los efectos que cubre el clip.</summary>
    public static (Time Low, Time High) EffectRange(ClipTimeMap map)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (map.Freeze)
        {
            Time first = map.SourceIn + map.Offset;
            return (first, first + map.Duration);
        }

        return (map.SourceIn, map.SourceOut);
    }

    /// <summary>El instante de la línea de tiempo de un keyframe, o <c>null</c> si cae fuera del clip.</summary>
    public static Time? TimelineOfEffect(ClipTimeMap map, Time effectTime)
    {
        ArgumentNullException.ThrowIfNull(map);
        (Time low, Time high) = EffectRange(map);
        if (effectTime < low || effectTime > high)
        {
            return null;
        }

        return map.Freeze ? map.Start + (effectTime - low) : map.TimelineOf(effectTime);
    }

    /// <summary>
    /// Cuánto origen separa dos miniaturas que en la línea de tiempo están a <paramref name="timelineSpacing"/>: a 2× el
    /// doble, así la rejilla de miniaturas sigue el material y no se piden de más.
    /// </summary>
    public static Time SourceSpacing(ClipTimeMap map, Time timelineSpacing)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (map.IsIdentity || map.Freeze)
        {
            return timelineSpacing;
        }

        double ratio = (double)(map.SourceOut - map.SourceIn).Ticks / map.Duration.Ticks;
        return Time.FromTicks(Math.Max(1, (long)Math.Round(timelineSpacing.Ticks * ratio)));
    }

    /// <summary>
    /// El tramo del origen, en orden, que se ve entre <paramref name="from"/> y <paramref name="to"/> (instantes de la
    /// línea de tiempo dentro del clip). La onda se pide para él y cada punto se ubica con <see cref="BucketOf"/>.
    /// </summary>
    public static (Time Low, Time High) VisibleSource(ClipTimeMap map, Time from, Time to)
    {
        ArgumentNullException.ThrowIfNull(map);
        Time a = map.Source(from);
        Time b = map.Source(to);
        Time low = Time.Min(a, b);
        Time high = Time.Max(a, b);
        return (low, high > low ? high : low + Time.FromTicks(1));
    }

    /// <summary>
    /// El tramo de <paramref name="buckets"/> en que se reparte <c>[low, high]</c> donde cae el origen de
    /// <paramref name="timeline"/>. A 1× el punto i cae en el tramo i; en reversa, al revés; con una curva, donde toque.
    /// </summary>
    public static int BucketOf(ClipTimeMap map, Time timeline, Time low, Time high, int buckets)
    {
        ArgumentNullException.ThrowIfNull(map);
        if (buckets <= 1)
        {
            return 0;
        }

        double fraction = (double)(map.Source(timeline) - low).Ticks / (high - low).Ticks;
        return Math.Clamp((int)Math.Round(fraction * (buckets - 1)), 0, buckets - 1);
    }
}
