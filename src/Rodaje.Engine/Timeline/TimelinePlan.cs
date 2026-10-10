using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Playback.Composition;

namespace Rodaje.Engine.Timeline;

/// <summary>Un archivo de origen: el medio del proyecto y la ruta resuelta. Si la ruta cambia, es otro origen.</summary>
internal readonly record struct SourceKey(string MediaId, string Path);

/// <summary>Clip de video listo para programar: tiempos copiados del modelo y su transformación, si tiene.</summary>
internal sealed class VideoClipPlan
{
    public VideoClipPlan(string clipId, SourceKey source, Time start, Time sourceIn, Time sourceOut, Rational frameRate, TransformPlan? transform)
    {
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

/// <summary>Clip de audio en muestras de 48 kHz: suena en <c>[StartSample, EndSample)</c> de la línea de tiempo.</summary>
internal sealed record AudioClipPlan(string ClipId, SourceKey Source, long StartSample, long EndSample, long SourceInSample);

internal sealed record AudioTrackPlan(string TrackId, IReadOnlyList<AudioClipPlan> Clips);

internal sealed record AudioPlan(IReadOnlyList<AudioTrackPlan> Tracks)
{
    public static AudioPlan Empty { get; } = new([]);
}

/// <summary>Instantánea inmutable de una secuencia para reproducirla; se reconstruye tras cada edición.</summary>
internal sealed class TimelinePlan
{
    public const int AudioSampleRate = 48_000;

    private TimelinePlan(Sequence sequence, IReadOnlyList<VideoLayerPlan> layers, AudioPlan audio)
    {
        SequenceId = sequence.Id;
        Width = sequence.Width;
        Height = sequence.Height;
        FrameRate = sequence.FrameRate;
        Duration = sequence.Duration;
        Layers = layers;
        Audio = audio;
    }

    public string SequenceId { get; }

    public int Width { get; }

    public int Height { get; }

    public Rational FrameRate { get; }

    public Time Duration { get; }

    /// <summary>Cuadros de la secuencia que llegan a verse, contando el último aunque esté incompleto.</summary>
    public long FrameCount => Duration > Time.Zero ? (Duration - Time.FromTicks(1)).ToFrames(FrameRate) + 1 : 0;

    /// <summary>Capas visibles de abajo hacia arriba.</summary>
    public IReadOnlyList<VideoLayerPlan> Layers { get; }

    public AudioPlan Audio { get; }

    /// <summary>Inicio del cuadro de la secuencia que se ve en <paramref name="time"/>, sin pasar del último.</summary>
    public Time FrameStart(Time time)
    {
        long frame = Math.Clamp(time.ToFrames(FrameRate), 0, Math.Max(0, FrameCount - 1));
        return Time.FromFrames(frame, FrameRate);
    }

    /// <summary>Copia lo que la reproducción necesita: se saltan pistas ocultas o mudas, clips desactivados y medios sin el flujo.</summary>
    /// <exception cref="KeyNotFoundException">La secuencia no existe en el proyecto.</exception>
    public static TimelinePlan Build(Project project, string sequenceId, string projectDirectory)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequenceId);
        Sequence sequence = project.GetSequence(sequenceId);
        var media = new Dictionary<string, MediaRef>();
        foreach (MediaRef m in project.Media)
        {
            media.TryAdd(m.Id, m);
        }

        var layers = new List<VideoLayerPlan>();
        for (int i = 0; i < sequence.VideoTracks.Count; i++)
        {
            Track track = sequence.VideoTracks[i];
            if (track.Hidden)
            {
                continue;
            }

            var clips = new List<VideoClipPlan>();
            foreach (Clip clip in Ordered(track))
            {
                if (clip.Enabled && clip.Duration > Time.Zero && media.TryGetValue(clip.MediaId, out MediaRef? m)
                    && m.HasVideo && m.FrameRate is { } rate)
                {
                    clips.Add(new VideoClipPlan(clip.Id, Key(m, projectDirectory), clip.Start, clip.SourceIn, clip.SourceOut, rate,
                        TransformPlan.From(clip)));
                }
            }

            layers.Add(new VideoLayerPlan(i, track.Id, clips));
        }

        var audioTracks = new List<AudioTrackPlan>();
        foreach (Track track in sequence.AudioTracks)
        {
            if (track.Muted)
            {
                continue;
            }

            var clips = new List<AudioClipPlan>();
            foreach (Clip clip in Ordered(track))
            {
                if (!clip.Enabled || !media.TryGetValue(clip.MediaId, out MediaRef? m) || !m.HasAudio)
                {
                    continue;
                }

                long start = clip.Start.ToSamples(AudioSampleRate);
                long end = clip.End.ToSamples(AudioSampleRate);
                if (end > start)
                {
                    clips.Add(new AudioClipPlan(clip.Id, Key(m, projectDirectory), start, end, clip.SourceIn.ToSamples(AudioSampleRate)));
                }
            }

            audioTracks.Add(new AudioTrackPlan(track.Id, clips));
        }

        return new TimelinePlan(sequence, layers, new AudioPlan(audioTracks));
    }

    private static IEnumerable<Clip> Ordered(Track track) => track.Clips.OrderBy(c => c.Start);

    private static SourceKey Key(MediaRef media, string projectDirectory) => new(media.Id, media.ResolvePath(projectDirectory));
}
