using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Engine.Timeline;

/// <summary>Un archivo de origen: el medio del proyecto y la ruta resuelta. Si la ruta cambia, es otro origen.</summary>
internal readonly record struct SourceKey(string MediaId, string Path);

/// <summary>Instantánea inmutable de una secuencia para reproducirla; se reconstruye tras cada edición.</summary>
internal sealed class TimelinePlan
{
    public const int AudioSampleRate = 48_000;

    private TimelinePlan(Sequence sequence, IReadOnlyList<VideoLayerPlan> layers, IReadOnlyList<VideoLayerPlan> hiddenLayers, AudioPlan audio)
    {
        SequenceId = sequence.Id;
        Width = sequence.Width;
        Height = sequence.Height;
        FrameRate = sequence.FrameRate;
        Duration = sequence.Duration;
        Layers = layers;
        HiddenLayers = hiddenLayers;
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

    /// <summary>Pistas ocultas: no se componen, pero una recién ocultada se mantiene decodificada por si vuelve.</summary>
    public IReadOnlyList<VideoLayerPlan> HiddenLayers { get; }

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
        var hidden = new List<VideoLayerPlan>();
        for (int i = 0; i < sequence.VideoTracks.Count; i++)
        {
            Track track = sequence.VideoTracks[i];
            var clips = new List<VideoClipPlan>();
            foreach (Clip clip in Ordered(track))
            {
                if (clip.Enabled && clip.Duration > Time.Zero && media.TryGetValue(clip.MediaId, out MediaRef? m)
                    && m.HasVideo && m.FrameRate is { } rate)
                {
                    clips.Add(new VideoClipPlan(clip.Id, Key(m, projectDirectory), clip.Start, clip.SourceIn, clip.SourceOut, rate,
                        TransformPlan.From(clip), m.Width, m.Height));
                }
            }

            (track.Hidden ? hidden : layers).Add(new VideoLayerPlan(i, track.Id, clips));
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

        return new TimelinePlan(sequence, layers, hidden, new AudioPlan(audioTracks));
    }

    private static IEnumerable<Clip> Ordered(Track track) => track.Clips.OrderBy(c => c.Start);

    private static SourceKey Key(MediaRef media, string projectDirectory) => new(media.Id, media.ResolvePath(projectDirectory));
}
