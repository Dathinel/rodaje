using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Playback.Composition;

namespace Rodaje.Engine.Timeline;

/// <summary>Un archivo de origen: el medio del proyecto y la ruta resuelta. Si la ruta cambia, es otro origen.</summary>
internal readonly record struct SourceKey(string MediaId, string Path);

/// <summary>Instantánea inmutable de una secuencia para reproducirla; se reconstruye tras cada edición.</summary>
internal sealed partial class TimelinePlan
{
    public const int AudioSampleRate = 48_000;

    private TimelinePlan(Sequence sequence, IReadOnlyList<VideoLayerPlan> layers, IReadOnlyList<VideoLayerPlan> hiddenLayers, AudioPlan audio,
        int missingContent, Sequence expanded)
    {
        SequenceId = sequence.Id;
        Expanded = expanded;
        for (int i = 0; i < layers.Count; i++)
        {
            layers[i].Ordinal = i;
        }

        Width = sequence.Width;
        Height = sequence.Height;
        FrameRate = sequence.FrameRate;
        Duration = sequence.Duration;
        Layers = layers;
        HiddenLayers = hiddenLayers;
        Audio = audio;
        MissingContent = missingContent;
        Lanes = [.. layers.SelectMany(l => l.Companion is { } companion ? new[] { l, companion } : [l])];
    }

    public string SequenceId { get; }

    public int Width { get; }

    public int Height { get; }

    public Rational FrameRate { get; }

    public Time Duration { get; }

    /// <summary>Cuadros de la secuencia que llegan a verse, contando el último aunque esté incompleto.</summary>
    public long FrameCount => Duration > Time.Zero ? (Duration - Time.FromTicks(1)).ToFrames(FrameRate) + 1 : 0;

    /// <summary>
    /// Capas visibles de abajo hacia arriba. Detrás de cada una van los carriles de los grupos de sus compuestos
    /// (<see cref="VideoLayerPlan.IsNested"/>), que se decodifican como los demás pero se componen dentro de su grupo.
    /// </summary>
    public IReadOnlyList<VideoLayerPlan> Layers { get; }

    /// <summary>
    /// La secuencia que se reproduce, con los compuestos desplegados y, en sus pistas de video, también las de los grupos
    /// (con los ids de los clips del plan): para buscar en el modelo lo que usa un clip del plan.
    /// </summary>
    public Sequence Expanded { get; }

    /// <summary>Los carriles visibles: cada capa y, detrás, su carril compañero si tiene transiciones.</summary>
    public IReadOnlyList<VideoLayerPlan> Lanes { get; }

    /// <summary>Pistas ocultas: no se componen, pero una recién ocultada se mantiene decodificada por si vuelve.</summary>
    public IReadOnlyList<VideoLayerPlan> HiddenLayers { get; }

    public AudioPlan Audio { get; }

    /// <summary>Transiciones de paquete cuyo contenido falta: se ven como fundido cruzado.</summary>
    public int MissingContent { get; }

    /// <summary>Los títulos de las pistas visibles que se ven en <c>[from, to)</c>, sin repetir contenido, de abajo arriba.</summary>
    public List<GeneratedContent> GeneratedContents(Time from, Time to)
    {
        var contents = new List<GeneratedContent>();
        var seen = new HashSet<GeneratedContent>();
        foreach (VideoLayerPlan layer in Layers)
        {
            for (int i = layer.FirstGeneratedEndingAfter(from); i < layer.Generated.Count && layer.Generated[i].Start < to; i++)
            {
                foreach (GeneratedContent content in layer.Generated[i].Contents)
                {
                    if (seen.Add(content))
                    {
                        contents.Add(content);
                    }
                }
            }
        }

        return contents;
    }

    /// <summary>Inicio del cuadro de la secuencia que se ve en <paramref name="time"/>, sin pasar del último.</summary>
    public Time FrameStart(Time time)
    {
        long frame = Math.Clamp(time.ToFrames(FrameRate), 0, Math.Max(0, FrameCount - 1));
        return Time.FromFrames(frame, FrameRate);
    }

    /// <summary>
    /// Copia lo que la reproducción necesita: se saltan pistas ocultas o mudas, clips desactivados y medios sin el flujo.
    /// Cada transición alarga el clip que sale media duración y adelanta el que entra otra media en el carril compañero
    /// de la pista; en audio, los dos suenan a la vez con fundidos complementarios.
    /// </summary>
    /// <param name="content">Curvas de las transiciones de paquete; sin él, se ven como fundido cruzado.</param>
    /// <exception cref="KeyNotFoundException">La secuencia no existe en el proyecto.</exception>
    public static TimelinePlan Build(Project project, string sequenceId, string projectDirectory, IEffectContent? content = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequenceId);
        // Cada clip compuesto se despliega en los clips que muestra (recursivo, sin ciclos), salvo los que tienen efectos o
        // transiciones propios, que se componen (o mezclan) aparte en su grupo.
        Func<Track, Clip, bool> keep = (track, clip) => track.Kind == TrackKind.Video ? VideoGroup(track, clip) : AudioGroup(project, track, clip);
        Sequence sequence = CompoundOps.Flatten(project, project.GetSequence(sequenceId), keep);
        var media = new Dictionary<string, MediaRef>();
        foreach (MediaRef m in project.Media)
        {
            media.TryAdd(m.Id, m);
        }

        var video = new VideoBuilder(project, sequence, media, projectDirectory, content, keep);
        var hidden = new List<VideoLayerPlan>();
        for (int i = 0; i < sequence.VideoTracks.Count; i++)
        {
            Track track = sequence.VideoTracks[i];
            video.Add(track, i, track.Hidden ? hidden : video.Layers, groups: !track.Hidden, depth: 0);
        }

        var audio = new AudioBuilder(project, media, projectDirectory, keep);
        var audioTracks = new List<AudioTrackPlan>();
        foreach (Track track in sequence.AudioTracks)
        {
            if (!track.Muted)
            {
                audioTracks.Add(audio.Track(track, Time.Zero, depth: 0));
            }
        }

        var expanded = new Sequence
        {
            Id = sequence.Id,
            Name = sequence.Name,
            FrameRate = sequence.FrameRate,
            Width = sequence.Width,
            Height = sequence.Height,
            SampleRate = sequence.SampleRate,
            VideoTracks = video.Tracks,
            AudioTracks = sequence.AudioTracks,
        };
        // La mezcla no admite sidechain externo: su llave tendría que ir por delante de todo lo que la forma.
        var audioPlan = new AudioPlan(AssignKeys(audioTracks), sequence.MasterGainDb)
        {
            MasterEffects = AudioEffects(sequence.MasterEffects) is { } master ? master with { SidechainTrackId = null } : null,
            MasterAutomation = Automation(sequence.MasterEffects, Time.Zero),
        };
        return new TimelinePlan(sequence, video.Layers, hidden, audioPlan, video.Missing, expanded);
    }

    /// <summary>
    /// Las transiciones de la pista entre clips incluidos y contiguos, con cada mitad acotada a la mitad de su clip (el
    /// núcleo ya las acota; esto protege de un proyecto escrito a mano).
    /// </summary>
    private static List<Cut> Cuts(Track track, List<Clip> included)
    {
        var cuts = new List<Cut>();
        foreach (TransitionInstance transition in track.Transitions)
        {
            int from = included.FindIndex(c => c.Id == transition.From);
            if (from < 0 || from + 1 >= included.Count || included[from + 1].Id != transition.To
                || included[from].End != included[from + 1].Start || transition.Duration <= Time.Zero
                || cuts.Exists(c => c.From == from))
            {
                continue;
            }

            Time before = Time.Min(transition.Before, Time.FromTicks(included[from].Duration.Ticks / 2));
            Time after = Time.Min(transition.After, Time.FromTicks(included[from + 1].Duration.Ticks / 2));
            if (before + after > Time.Zero)
            {
                cuts.Add(new Cut(transition, from, from + 1, included[from].End, before, after));
            }
        }

        return cuts;
    }

    /// <summary>
    /// En el tramo de un compuesto alargado por una transición (<c>[from, to)</c>, el clip ocupa <c>[clipStart, clipEnd)</c>),
    /// lo de dentro que toca un borde del compuesto y no tiene nada más allá se alarga hasta el borde del tramo con su
    /// propio material (o su cuadro congelado), como un clip suelto en una transición.
    /// </summary>
    private static void ExtendEdges(List<Clip> copied, Time from, Time to, Time clipStart, Time clipEnd, Dictionary<Clip, (Time Before, Time After)> extend)
    {
        List<Clip> media = copied.FindAll(c => !c.IsCompound && c.Generator is null);
        if (media.Count == 0)
        {
            return;
        }

        Clip first = media.MinBy(c => c.Start)!;
        Clip last = media.MaxBy(c => c.End)!;
        if (first.Start > from && first.Start <= clipStart)
        {
            extend[first] = (first.Start - from, extend.GetValueOrDefault(first).After);
        }

        if (last.End < to && last.End >= clipEnd)
        {
            extend[last] = (extend.GetValueOrDefault(last).Before, to - last.End);
        }
    }

    /// <summary>Hasta dónde llega el material del medio; sin duración conocida, no hay material extra.</summary>
    private static Time Material(MediaRef media) => media.Duration > Time.Zero ? media.Duration : Time.Zero;

    private static IEnumerable<Clip> Ordered(Track track) => track.Clips.OrderBy(c => c.Start);

    private static SourceKey Key(MediaRef media, string projectDirectory) => new(media.Id, media.ResolvePath(projectDirectory));

    /// <summary>Una transición aplicable: índices de sus clips en la lista incluida, el corte y sus dos mitades acotadas.</summary>
    private sealed record Cut(TransitionInstance Transition, int From, int To, Time At, Time Before, Time After);
}
