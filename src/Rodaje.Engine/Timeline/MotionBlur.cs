using System.Collections;
using Rodaje.Core.Timing;
using Rodaje.Playback.Composition;

namespace Rodaje.Engine.Timeline;

/// <summary>
/// Desenfoque de movimiento de la transformación: el obturador abierto <see cref="Shutter"/> cuadros (ángulo / 360),
/// centrado en el instante del cuadro más <see cref="Phase"/> cuadros, muestreado en <see cref="Samples"/> instantes
/// repartidos por igual (en el centro de cada tramo). La vista previa y la exportación usan estos mismos instantes.
/// </summary>
internal readonly record struct MotionBlur(double Shutter, double Phase, int Samples)
{
    /// <summary>Cuánto se corre la muestra <paramref name="index"/> desde el instante del cuadro, en ticks.</summary>
    public long OffsetTicks(int index, long frameTicks) =>
        (long)Math.Round((Phase + (Shutter * (((index + 0.5) / Samples) - 0.5))) * frameTicks);
}

/// <summary>Transformaciones de las muestras del desenfoque de movimiento; se reutiliza entre refrescos como <see cref="EffectList"/>.</summary>
internal sealed class TransformList : IReadOnlyList<LayerTransform>
{
    private LayerTransform[] _items = [];

    public int Count { get; private set; }

    public LayerTransform this[int index] => (uint)index < (uint)Count ? _items[index] : throw new ArgumentOutOfRangeException(nameof(index));

    public Span<LayerTransform> Reset(int count)
    {
        if (_items.Length < count)
        {
            _items = new LayerTransform[count];
        }

        Count = count;
        return _items.AsSpan(0, count);
    }

    public ArraySegment<LayerTransform>.Enumerator GetEnumerator() => new ArraySegment<LayerTransform>(_items, 0, Count).GetEnumerator();

    IEnumerator<LayerTransform> IEnumerable<LayerTransform>.GetEnumerator() => GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>La transformación de una capa en un instante de la línea de tiempo, para muestrear el obturador.</summary>
internal interface IMotionSource
{
    LayerTransform At(Time timeline);
}

internal static class MotionSampler
{
    /// <summary>
    /// Las transformaciones de las muestras del obturador alrededor de <paramref name="time"/>, en una lista que rota con
    /// <paramref name="ring"/> (la misma rotación que los efectos de la capa: <paramref name="lists"/> es la de este
    /// refresco, o se toma una). Nulo sin desenfoque o si la capa no se mueve en todo el obturador.
    /// </summary>
    public static IReadOnlyList<LayerTransform>? Sample<TSource>(MotionBlur? blur, in TSource source, Time time, long frameTicks,
        EffectListRing ring, ref EffectList? lists)
        where TSource : struct, IMotionSource
    {
        if (blur is not { } motion || frameTicks <= 0)
        {
            return null;
        }

        lists ??= ring.Next();
        Span<LayerTransform> samples = lists.Motion.Reset(motion.Samples);
        bool moves = false;
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = source.At(time + Time.FromTicks(motion.OffsetTicks(i, frameTicks)));
            moves |= i > 0 && samples[i] != samples[0];
        }

        return moves ? lists.Motion : null;
    }
}

/// <summary>Un clip de video: su transformación (con estabilizar y la transición del instante) en cada muestra.</summary>
internal readonly struct ClipMotionSource(EffectPlan? plan, VideoClipPlan clip, long frameIndex, TransitionPlan? transition, bool entering,
    float progress) : IMotionSource
{
    public LayerTransform At(Time timeline)
    {
        LayerTransform transform = EffectPlan.Stabilized(plan, EffectPlan.Transform(plan, clip.EffectTimeAt(timeline)), frameIndex);
        return transition is null ? transform : transition.Apply(transform, entering, progress);
    }
}

/// <summary>Un compuesto: su transformación (y la de su transición) en cada muestra.</summary>
internal readonly struct GroupMotionSource(GroupClipPlan group, TransitionPlan? transition, bool entering, float progress) : IMotionSource
{
    public LayerTransform At(Time timeline)
    {
        LayerTransform transform = EffectPlan.Transform(group.Effects, group.EffectTimeAt(timeline));
        return transition is null ? transform : transition.Apply(transform, entering, progress);
    }
}

/// <summary>Un título: la transformación del clip con la animación de entrada y salida del estilo.</summary>
internal readonly struct TitleMotionSource(GeneratedClipPlan title) : IMotionSource
{
    public LayerTransform At(Time timeline) => title.TransformAt(timeline);
}
