using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Playback.Composition;

namespace Rodaje.Engine.Timeline;

/// <summary>
/// Clip de video listo para programar: tiempos copiados del modelo y sus efectos. En una transición el clip puede verse
/// más allá de su material: <see cref="Start"/> y <see cref="End"/> son lo que se ve, <see cref="SourceStart"/> es dónde
/// suena <see cref="SourceIn"/> y fuera de <c>[SourceIn, SourceOut)</c> se congela el cuadro del borde.
/// </summary>
internal sealed class VideoClipPlan
{
    public VideoClipPlan(string clipId, SourceKey source, Time start, Time sourceIn, Time sourceOut, Rational frameRate, EffectPlan? effects,
        int displayWidth = 0, int displayHeight = 0, Time holdBefore = default, Time holdAfter = default)
    {
        DisplayWidth = displayWidth;
        DisplayHeight = displayHeight;
        ClipId = clipId;
        Source = source;
        SourceStart = start;
        Start = start - holdBefore;
        End = start + (sourceOut - sourceIn) + holdAfter;
        SourceIn = sourceIn;
        SourceOut = sourceOut;
        FrameRate = frameRate;
        Effects = effects;
    }

    public string ClipId { get; }

    public SourceKey Source { get; }

    /// <summary>Desde cuándo se ve, contando el cuadro congelado antes del material.</summary>
    public Time Start { get; }

    /// <summary>Hasta cuándo se ve (sin incluir), contando el cuadro congelado después del material.</summary>
    public Time End { get; }

    /// <summary>Instante de la secuencia en que se ve <see cref="SourceIn"/>.</summary>
    public Time SourceStart { get; }

    public Time SourceIn { get; }

    public Time SourceOut { get; }

    /// <summary>Tasa del medio de origen, que puede diferir de la de la secuencia.</summary>
    public Rational FrameRate { get; }

    public EffectPlan? Effects { get; }

    /// <summary>
    /// Mapeo del clip del modelo (sin los tramos de transición). Hoy los tiempos de arriba son los de 1×; el motor de la
    /// fase 5c los toma de aquí para la velocidad, la reversa y el congelado.
    /// </summary>
    public ClipTimeMap? TimeMap { get; init; }

    /// <summary>La transición del corte donde termina este tramo, si la hay: el clip sale mientras dura.</summary>
    public TransitionPlan? Outgoing { get; init; }

    /// <summary>Tamaño en pantalla del original según el proyecto (0 si no se conoce); un proxy se ajusta con este.</summary>
    public int DisplayWidth { get; }

    public int DisplayHeight { get; }

    public long FirstFrame => SourceIn.ToFrames(FrameRate);

    public long LastFrame => (SourceOut - Time.FromTicks(1)).ToFrames(FrameRate);

    public bool Covers(Time timeline) => timeline >= Start && timeline < End;

    /// <summary>Instante del origen para un instante de la línea de tiempo, acotado al material del clip.</summary>
    public Time ToSource(Time timeline)
    {
        Time source = SourceIn + (timeline - SourceStart);
        Time last = SourceOut - Time.FromTicks(1);
        return source < SourceIn ? SourceIn : source > last ? last : source;
    }

    /// <summary>Cuadro del origen que contiene el instante: el último que empieza en o antes de él.</summary>
    public long FrameAt(Time timeline) => ToSource(timeline).ToFrames(FrameRate);

    /// <summary>Primer instante de la secuencia en que se ve el cuadro <paramref name="index"/> del origen.</summary>
    public Time ShownAt(long index) =>
        index <= FirstFrame ? Start : Time.Max(Start, SourceStart + (Time.FromFrames(index, FrameRate) - SourceIn));

    /// <summary>Cuadros del origen entre <paramref name="after"/> y <paramref name="before"/>, sin contarlos, que alguna secuencia a <paramref name="sequenceRate"/> muestra.</summary>
    /// <remarks>Un origen más rápido que la secuencia salta cuadros por diseño; esos no son pérdidas.</remarks>
    public long ShownBetween(long after, long before, Rational sequenceRate)
    {
        long count = 0;
        long last = Math.Min(before - 1, LastFrame);
        for (long i = Math.Max(after + 1, FirstFrame); i <= last; i++)
        {
            if (IsShown(i, sequenceRate))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Algún cuadro de la secuencia empieza mientras el cuadro <paramref name="index"/> del origen está en pantalla.</summary>
    private bool IsShown(long index, Rational sequenceRate)
    {
        Time from = ShownAt(index);
        Time to = index >= LastFrame ? End : Time.Min(End, SourceStart + (Time.FromFrames(index + 1, FrameRate) - SourceIn));
        long k = from.ToFrames(sequenceRate);
        Time first = Time.FromFrames(k, sequenceRate);
        if (first < from)
        {
            first = Time.FromFrames(k + 1, sequenceRate);
        }

        return first < to;
    }

    /// <summary>Mismo origen y mismos tiempos: los cuadros que muestra son los mismos.</summary>
    public bool SameMapping(VideoClipPlan other) =>
        ClipId == other.ClipId && Source == other.Source && Start == other.Start && End == other.End && SourceStart == other.SourceStart
        && SourceIn == other.SourceIn && SourceOut == other.SourceOut && FrameRate == other.FrameRate && Equals(TimeMap, other.TimeMap);
}

/// <summary>Un carril de video: una pista con sus clips activos ordenados por posición, o el carril compañero de sus transiciones.</summary>
internal sealed class VideoLayerPlan(int trackIndex, string trackId, IReadOnlyList<VideoClipPlan> clips)
{
    /// <summary>Sufijo del carril compañero: tiene los clips que entran en cada transición, adelantados media duración.</summary>
    public const string CompanionSuffix = "#t";

    /// <summary>Índice en <see cref="Sequence.VideoTracks"/>; 0 es la capa de más abajo.</summary>
    public int TrackIndex { get; } = trackIndex;

    public string TrackId { get; } = trackId;

    public IReadOnlyList<VideoClipPlan> Clips { get; } = clips;

    /// <summary>Carril con los clips que entran en las transiciones de esta pista; nulo si no tiene transiciones.</summary>
    public VideoLayerPlan? Companion { get; init; }

    public VideoClipPlan? ClipAt(Time time)
    {
        int i = FirstEndingAfter(time);
        return i < Clips.Count && Clips[i].Start <= time ? Clips[i] : null;
    }

    /// <summary>El primer clip que empieza después de <paramref name="time"/>.</summary>
    public VideoClipPlan? NextAfter(Time time)
    {
        int i = FirstEndingAfter(time);
        while (i < Clips.Count && Clips[i].Start <= time)
        {
            i++;
        }

        return i < Clips.Count ? Clips[i] : null;
    }

    private int FirstEndingAfter(Time time)
    {
        int lo = 0;
        int hi = Clips.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (Clips[mid].End <= time)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}
