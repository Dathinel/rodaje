using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Interchange;

/// <summary>Un tramo de una pista recorrida, en tiempo de la secuencia.</summary>
public abstract record WalkItem(Time Start, Time Duration)
{
    public Time End => Start + Duration;
}

/// <summary>
/// Un clip con lo que los escritores necesitan resuelto: su medio (nulo en un generador o un compuesto), la secuencia del
/// compuesto y las transiciones de sus dos cortes.
/// </summary>
public sealed record WalkClip(Time Start, Time Duration, Clip Clip, MediaRef? Media, Sequence? Compound, TransitionInstance? In, TransitionInstance? Out)
    : WalkItem(Start, Duration);

public sealed record WalkGap(Time Start, Time Duration) : WalkItem(Start, Duration);

/// <summary>Una pista recorrida de principio a fin: clips y huecos seguidos, sin solapes.</summary>
public sealed record WalkTrack(TrackKind Kind, int Index, Track Track, IReadOnlyList<WalkItem> Items);

/// <summary>Vista plana de una secuencia para escribirla en otro formato.</summary>
public static class TimelineWalk
{
    /// <summary>
    /// Las pistas de <paramref name="sequence"/> como tramos seguidos desde el cero. Con <paramref name="flattenCompounds"/>,
    /// cada compuesto se cambia por los clips de su secuencia en el tramo que muestra, con el presupuesto de
    /// <see cref="InterchangeLimits.MaxFlattenedClips"/> y detección de ciclos.
    /// </summary>
    public static IReadOnlyList<WalkTrack> Of(Project project, Sequence sequence, bool flattenCompounds, InterchangeLimits limits, InterchangeReport report) =>
        throw new NotImplementedException("TimelineWalk: pendiente (fase F3 de D-062).");
}
