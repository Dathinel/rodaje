using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Playback.Composition;

namespace Rodaje.Engine.Timeline;

/// <summary>Clip de video listo para programar: tiempos copiados del modelo y su transformación, si tiene.</summary>
internal sealed class VideoClipPlan
{
    public VideoClipPlan(string clipId, SourceKey source, Time start, Time sourceIn, Time sourceOut, Rational frameRate, TransformPlan? transform,
        int displayWidth = 0, int displayHeight = 0)
    {
        DisplayWidth = displayWidth;
        DisplayHeight = displayHeight;
        ClipId = clipId;
        Source = source;
        Start = start;
        SourceIn = sourceIn;
        SourceOut = sourceOut;
        FrameRate = frameRate;
        Transform = transform;
    }

    public string ClipId { get; }

    public SourceKey Source { get; }

    public Time Start { get; }

    public Time SourceIn { get; }

    public Time SourceOut { get; }

    /// <summary>Tasa del medio de origen, que puede diferir de la de la secuencia.</summary>
    public Rational FrameRate { get; }

    public TransformPlan? Transform { get; }

    /// <summary>Tamaño en pantalla del original según el proyecto (0 si no se conoce); un proxy se ajusta con este.</summary>
    public int DisplayWidth { get; }

    public int DisplayHeight { get; }

    public Time End => Start + (SourceOut - SourceIn);

    public long FirstFrame => SourceIn.ToFrames(FrameRate);

    public long LastFrame => (SourceOut - Time.FromTicks(1)).ToFrames(FrameRate);

    public bool Covers(Time timeline) => timeline >= Start && timeline < End;

    /// <summary>Instante del origen para un instante de la línea de tiempo, acotado al clip.</summary>
    public Time ToSource(Time timeline)
    {
        Time clamped = timeline < Start ? Start : timeline >= End ? End - Time.FromTicks(1) : timeline;
        return SourceIn + (clamped - Start);
    }

    /// <summary>Cuadro del origen que contiene el instante: el último que empieza en o antes de él.</summary>
    public long FrameAt(Time timeline) => ToSource(timeline).ToFrames(FrameRate);

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
        Time from = Time.Max(Start, Start + (Time.FromFrames(index, FrameRate) - SourceIn));
        Time to = Time.Min(End, Start + (Time.FromFrames(index + 1, FrameRate) - SourceIn));
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
        ClipId == other.ClipId && Source == other.Source && Start == other.Start
        && SourceIn == other.SourceIn && SourceOut == other.SourceOut && FrameRate == other.FrameRate;
}

/// <summary>Parámetros de <see cref="BuiltInEffects.Transform"/> copiados del modelo; se evalúan en tiempo del origen.</summary>
internal sealed class TransformPlan
{
    private readonly Parameter? _x;
    private readonly Parameter? _y;
    private readonly Parameter? _scale;
    private readonly Parameter? _rotation;
    private readonly Parameter? _opacity;

    private TransformPlan(EffectInstance effect)
    {
        _x = Copy(effect, BuiltInEffects.TransformParameters.X);
        _y = Copy(effect, BuiltInEffects.TransformParameters.Y);
        _scale = Copy(effect, BuiltInEffects.TransformParameters.Scale);
        _rotation = Copy(effect, BuiltInEffects.TransformParameters.Rotation);
        _opacity = Copy(effect, BuiltInEffects.TransformParameters.Opacity);
    }

    /// <summary>El primer efecto de transformación activo del clip; sin él, la capa llena el cuadro.</summary>
    public static TransformPlan? From(Clip clip)
    {
        EffectInstance? effect = clip.Effects.Find(e => e.Enabled && e.Type == BuiltInEffects.Transform);
        return effect is null ? null : new TransformPlan(effect);
    }

    public static LayerTransform Evaluate(TransformPlan? plan, Time sourceTime) =>
        plan is null
            ? LayerTransform.Identity
            : new LayerTransform(
                Value(plan._x, sourceTime, 0),
                Value(plan._y, sourceTime, 0),
                Value(plan._scale, sourceTime, 1),
                Value(plan._rotation, sourceTime, 0),
                Math.Clamp(Value(plan._opacity, sourceTime, 1), 0, 1));

    private static Parameter? Copy(EffectInstance effect, string name) =>
        effect.Parameters.TryGetValue(name, out Parameter? parameter) ? parameter.Clone() : null;

    private static double Value(Parameter? parameter, Time sourceTime, double fallback)
    {
        if (parameter is null)
        {
            return fallback;
        }

        double value = parameter.Evaluate(sourceTime);
        return double.IsFinite(value) ? value : fallback;
    }
}

/// <summary>Una pista de video visible, con sus clips activos ordenados por posición.</summary>
internal sealed class VideoLayerPlan(int trackIndex, string trackId, IReadOnlyList<VideoClipPlan> clips)
{
    /// <summary>Índice en <see cref="Sequence.VideoTracks"/>; 0 es la capa de más abajo.</summary>
    public int TrackIndex { get; } = trackIndex;

    public string TrackId { get; } = trackId;

    public IReadOnlyList<VideoClipPlan> Clips { get; } = clips;

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
