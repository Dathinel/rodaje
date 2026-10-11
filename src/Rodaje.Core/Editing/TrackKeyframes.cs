using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

/// <summary>
/// Mantiene los keyframes de los efectos de pista (en tiempo de la secuencia) pegados al contenido cuando una edición lo
/// corre: ripple, insertar con ripple, cerrar huecos y reordenar la principal.
/// </summary>
/// <remarks>
/// Cada tramo que se mueve se lleva sus keyframes y gana uno en cada borde con el valor que tenía ahí, así la curva no
/// cambia dentro de él aunque cambien sus vecinos; lo que cae en un tramo borrado o tapado se pierde. Los bordes que no
/// hacen falta (mismo valor que sus vecinos) se quitan. Los valores en los bordes son exactos; un tramo con suavizado
/// cortado por un borde se aproxima, porque la curva vuelve a empezar ahí.
/// </remarks>
public static class TrackKeyframes
{
    private const long Open = long.MaxValue;
    private const long Unbounded = long.MinValue;

    /// <summary>Fin de un tramo que llega hasta el final de la secuencia.</summary>
    public static Time ToTheEnd { get; } = Time.FromTicks(Open);

    /// <summary>Tramo <c>[Start, End)</c> de la secuencia que se corre <c>Delta</c> ticks; <c>End</c> puede ser <see cref="ToTheEnd"/>.</summary>
    public readonly record struct Piece(Time Start, Time End, Time Delta);

    /// <summary>Corre en <paramref name="delta"/> lo que hay desde <paramref name="from"/>; si se cierra, lo que había entre medio se borra.</summary>
    public static void Shift(Track track, Time from, Time delta)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (delta != Time.Zero && HasKeyframes(track))
        {
            Remap(track, [(from.Ticks, Open, delta.Ticks)]);
        }
    }

    /// <summary>Mueve cada tramo con sus keyframes; lo que no se mueve ni recibe un tramo queda igual y lo tapado se pierde.</summary>
    /// <remarks>Los destinos no pueden solaparse entre sí.</remarks>
    public static void Move(Track track, IEnumerable<Piece> pieces)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(pieces);
        if (HasKeyframes(track))
        {
            Remap(track, [.. pieces.Select(p => (p.Start.Ticks, p.End.Ticks, p.Delta.Ticks))]);
        }
    }

    private static bool HasKeyframes(Track track) =>
        track.Effects.Exists(e => e.Parameters.Values.Any(p => p.Keyframes.Count > 0));

    private static void Remap(Track track, List<(long Start, long End, long Delta)> moved)
    {
        List<(long Start, long End, long Delta)> pieces = [.. moved, .. Untouched(moved)];
        foreach (Parameter parameter in track.Effects.SelectMany(e => e.Parameters.Values))
        {
            if (parameter.Keyframes.Count > 0)
            {
                Remap(parameter, pieces);
            }
        }
    }

    /// <summary>Lo que no es origen ni destino de ningún tramo: se queda donde está.</summary>
    private static IEnumerable<(long Start, long End, long Delta)> Untouched(List<(long Start, long End, long Delta)> moved)
    {
        List<(long Start, long End)> covered =
        [
            .. moved.Select(p => (p.Start, p.End)),
            .. moved.Select(p => (p.Start + p.Delta, p.End == Open ? Open : p.End + p.Delta)),
        ];
        covered.Sort();
        long cursor = Unbounded;
        foreach ((long start, long end) in covered)
        {
            if (start > cursor)
            {
                yield return (cursor, start, 0);
            }

            cursor = Math.Max(cursor, end);
        }

        if (cursor != Open)
        {
            yield return (cursor, Open, 0);
        }
    }

    private static void Remap(Parameter parameter, List<(long Start, long End, long Delta)> pieces)
    {
        List<Keyframe> old = parameter.Keyframes;
        var result = new List<(Keyframe Key, bool Added)>();
        foreach ((long start, long end, long delta) in pieces)
        {
            if (start != Unbounded && start + delta >= 0 && !old.Exists(k => k.Time.Ticks == start))
            {
                result.Add((Boundary(parameter, start, delta, CurveFrom(old, start)), true));
            }

            foreach (Keyframe key in old)
            {
                if (key.Time.Ticks >= start && (end == Open || key.Time.Ticks < end))
                {
                    result.Add((key with { Time = key.Time + Time.FromTicks(delta) }, false));
                }
            }

            long last = end == Open ? Unbounded : end - 1;
            if (last != Unbounded && last > start && last + delta >= 0 && !old.Exists(k => k.Time.Ticks == last))
            {
                result.Add((Boundary(parameter, last, delta, Interpolation.Linear), true));
            }
        }

        result.Sort((a, b) => a.Key.Time.CompareTo(b.Key.Time));
        // Nunca queda vacía: el tramo que empieza donde termina lo borrado siempre lleva su borde.
        Prune(result);
        old.Clear();
        old.AddRange(result.Select(r => r.Key));
    }

    private static Keyframe Boundary(Parameter parameter, long at, long delta, Interpolation interpolation) =>
        new(Time.FromTicks(at + delta), parameter.Evaluate(Time.FromTicks(at)), interpolation);

    /// <summary>Curva del tramo que pasa por <paramref name="at"/>: la del último keyframe hasta ahí.</summary>
    private static Interpolation CurveFrom(List<Keyframe> keys, long at)
    {
        int index = keys.FindLastIndex(k => k.Time.Ticks <= at);
        return index < 0 ? Interpolation.Linear : keys[index].Interpolation;
    }

    /// <summary>Quita los bordes añadidos que no cambian la curva: los que valen lo mismo que sus vecinos.</summary>
    private static void Prune(List<(Keyframe Key, bool Added)> keys)
    {
        for (int i = keys.Count - 1; i >= 0 && keys.Count > 1; i--)
        {
            if (!keys[i].Added)
            {
                continue;
            }

            double value = keys[i].Key.Value;
            bool samePrevious = i == 0 || keys[i - 1].Key.Value.Equals(value);
            bool sameNext = i == keys.Count - 1 || keys[i + 1].Key.Value.Equals(value);
            if (samePrevious && sameNext)
            {
                keys.RemoveAt(i);
                i = Math.Min(i + 1, keys.Count);
            }
        }
    }
}
