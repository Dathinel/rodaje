using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Playback.Composition;

namespace Rodaje.Engine.Timeline;

/// <summary>Un archivo de origen: el medio del proyecto y la ruta resuelta. Si la ruta cambia, es otro origen.</summary>
internal readonly record struct SourceKey(string MediaId, string Path);

/// <summary>Instantánea inmutable de una secuencia para reproducirla; se reconstruye tras cada edición.</summary>
internal sealed class TimelinePlan
{
    public const int AudioSampleRate = 48_000;

    private TimelinePlan(Sequence sequence, IReadOnlyList<VideoLayerPlan> layers, IReadOnlyList<VideoLayerPlan> hiddenLayers, AudioPlan audio,
        int missingContent)
    {
        SequenceId = sequence.Id;
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

    /// <summary>Capas visibles de abajo hacia arriba.</summary>
    public IReadOnlyList<VideoLayerPlan> Layers { get; }

    /// <summary>Los carriles visibles: cada capa y, detrás, su carril compañero si tiene transiciones.</summary>
    public IReadOnlyList<VideoLayerPlan> Lanes { get; }

    /// <summary>Pistas ocultas: no se componen, pero una recién ocultada se mantiene decodificada por si vuelve.</summary>
    public IReadOnlyList<VideoLayerPlan> HiddenLayers { get; }

    public AudioPlan Audio { get; }

    /// <summary>Transiciones de paquete cuyo contenido falta: se ven como fundido cruzado.</summary>
    public int MissingContent { get; }

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
        Sequence sequence = project.GetSequence(sequenceId);
        var media = new Dictionary<string, MediaRef>();
        foreach (MediaRef m in project.Media)
        {
            media.TryAdd(m.Id, m);
        }

        int missing = 0;
        var layers = new List<VideoLayerPlan>();
        var hidden = new List<VideoLayerPlan>();
        for (int i = 0; i < sequence.VideoTracks.Count; i++)
        {
            Track track = sequence.VideoTracks[i];
            List<Clip> included = [.. Ordered(track).Where(c => c.Enabled && c.Duration > Time.Zero
                && media.TryGetValue(c.MediaId, out MediaRef? m) && m.HasVideo && m.FrameRate is not null)];
            List<Cut> cuts = Cuts(track, included);
            var clips = new List<VideoClipPlan>();
            var incoming = new List<VideoClipPlan>();
            for (int c = 0; c < included.Count; c++)
            {
                Clip clip = included[c];
                MediaRef m = media[clip.MediaId];
                Rational rate = m.FrameRate!.Value;
                SourceKey key = Key(m, projectDirectory);
                EffectPlan? effects = EffectPlan.From(clip.Effects);
                Cut? entering = cuts.Find(x => x.To == c);
                Cut? leaving = cuts.Find(x => x.From == c);

                // El que entra se ve en el carril principal recién al terminar la transición; el que sale sigue hasta
                // entonces con el material que sobra o, si no hay, con su último cuadro congelado.
                Time head = entering?.After ?? Time.Zero;
                Time tail = leaving?.After ?? Time.Zero;
                Time extra = Time.Max(Time.Zero, Time.Min(tail, Material(m) - clip.SourceOut));
                TransitionPlan? transition = leaving is { } cut ? Transition(cut, sequence, content, ref missing) : null;
                clips.Add(new VideoClipPlan(clip.Id, key, clip.Start + head, clip.SourceIn + head, clip.SourceOut + extra,
                    rate, effects, m.Width, m.Height, holdAfter: tail - extra) { Outgoing = transition });

                if (entering is { } e)
                {
                    Time available = Time.Min(e.Before, clip.SourceIn);
                    incoming.Add(new VideoClipPlan(clip.Id, key, clip.Start - available, clip.SourceIn - available,
                        clip.SourceIn + e.After, rate, effects, m.Width, m.Height, holdBefore: e.Before - available));
                }
            }

            var layer = new VideoLayerPlan(i, track.Id, clips)
            {
                Companion = incoming.Count > 0 && !track.Hidden
                    ? new VideoLayerPlan(i, track.Id + VideoLayerPlan.CompanionSuffix, incoming)
                    : null,
            };
            (track.Hidden ? hidden : layers).Add(layer);
        }

        var audioTracks = new List<AudioTrackPlan>();
        foreach (Track track in sequence.AudioTracks)
        {
            if (!track.Muted)
            {
                audioTracks.Add(AudioTrack(track, media, projectDirectory));
            }
        }

        return new TimelinePlan(sequence, layers, hidden, new AudioPlan(audioTracks), missing);
    }

    private static AudioTrackPlan AudioTrack(Track track, Dictionary<string, MediaRef> media, string projectDirectory)
    {
        List<Clip> included = [.. Ordered(track).Where(c => c.Enabled && c.Duration > Time.Zero
            && media.TryGetValue(c.MediaId, out MediaRef? m) && m.HasAudio)];
        List<Cut> cuts = Cuts(track, included);
        Curve? trackDb = GainCurve(track.Effects);
        var clips = new List<AudioClipPlan>();
        for (int c = 0; c < included.Count; c++)
        {
            Clip clip = included[c];
            Cut? entering = cuts.Find(x => x.To == c);
            Cut? leaving = cuts.Find(x => x.From == c);
            long clipStart = clip.Start.ToSamples(AudioSampleRate);
            long start = (clip.Start - (entering?.Before ?? Time.Zero)).ToSamples(AudioSampleRate);
            long end = (clip.End + (leaving?.After ?? Time.Zero)).ToSamples(AudioSampleRate);
            long sourceIn = clip.SourceIn.ToSamples(AudioSampleRate) - (clipStart - start);
            if (end > start)
            {
                clips.Add(new AudioClipPlan(clip.Id, Key(media[clip.MediaId], projectDirectory), start, end, sourceIn,
                    Gain(clip, trackDb, entering, leaving)));
            }
        }

        return new AudioTrackPlan(track.Id, clips);
    }

    /// <summary>Volumen del clip: su <c>rodaje.audio</c>, la ganancia de la pista y los fundidos; nulo si suena tal cual.</summary>
    private static GainPlan? Gain(Clip clip, Curve? trackDb, Cut? entering, Cut? leaving)
    {
        EffectInstance? audio = clip.Effects.Find(e => e.Enabled && e.Type == BuiltInEffects.Audio);
        long clipStart = clip.Start.ToSamples(AudioSampleRate);
        long clipEnd = clip.End.ToSamples(AudioSampleRate);
        long length = clipEnd - clipStart;

        // Una transición reemplaza el fundido propio de ese lado: con los dos, el clip callaría antes de cruzarse.
        Fade fadeIn = default;
        Fade fadeOut = default;
        Fade crossIn = default;
        Fade crossOut = default;
        if (entering is { } e)
        {
            crossIn = Cross(e, fadeOut: false);
        }
        else
        {
            fadeIn = new Fade(clipStart, Math.Min(length, Samples(audio, BuiltInEffects.AudioParameters.FadeIn)), Out: false);
        }

        if (leaving is { } l)
        {
            crossOut = Cross(l, fadeOut: true);
        }
        else
        {
            long fade = Math.Min(length, Samples(audio, BuiltInEffects.AudioParameters.FadeOut));
            fadeOut = new Fade(clipEnd - fade, fade, Out: true);
        }

        var gain = new GainPlan(GainCurve(clip.Effects), trackDb, fadeIn, fadeOut, crossIn, crossOut);
        return gain.ClipDb is null && trackDb is null && !gain.HasFades ? null : gain;
    }

    private static Fade Cross(Cut cut, bool fadeOut)
    {
        long start = (cut.At - cut.Before).ToSamples(AudioSampleRate);
        return new Fade(start, (cut.At + cut.After).ToSamples(AudioSampleRate) - start, fadeOut);
    }

    private static long Samples(EffectInstance? audio, string parameter)
    {
        if (audio?.Parameters.GetValueOrDefault(parameter) is not { } p || !double.IsFinite(p.Value) || p.Value <= 0)
        {
            return 0;
        }

        return Time.FromSeconds(Math.Min(p.Value, 24 * 3600)).ToSamples(AudioSampleRate);
    }

    /// <summary>
    /// Ganancia del primer <c>rodaje.audio</c> activo en muestras (del origen en un clip, de la secuencia en una pista);
    /// nula si no lo hay o si vale 0 dB sin keyframes.
    /// </summary>
    private static Curve? GainCurve(IReadOnlyList<EffectInstance> effects)
    {
        EffectInstance? audio = effects.FirstOrDefault(e => e.Enabled && e.Type == BuiltInEffects.Audio);
        if (audio is null || EffectRegistry.Find(BuiltInEffects.Audio)?.FindParameter(BuiltInEffects.AudioParameters.GainDb) is not { } definition)
        {
            return null;
        }

        Curve curve = Curve.From(audio.Parameters.GetValueOrDefault(definition.Name), definition.DefaultValue, definition.Clamp,
            static ticks => Time.FromTicks(ticks).ToSamples(AudioSampleRate));
        return curve.IsConstant && curve.First == 0 ? null : curve;
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

    private static TransitionPlan Transition(Cut cut, Sequence sequence, IEffectContent? content, ref int missing)
    {
        TransitionInstance t = cut.Transition;
        EffectDefinition crossfade = EffectRegistry.Find(BuiltInTransitions.Crossfade)!;
        EffectDefinition definition = EffectRegistry.Find(t.Type) is { IsTransition: true } known ? known : crossfade;
        TransitionCurves? curves = null;
        bool fellBack = false;
        if (definition.Type == BuiltInTransitions.Preset)
        {
            string reference = t.Options.GetValueOrDefault(BuiltInTransitions.PresetOptions.Preset) ?? "";
            curves = reference.Length > 0 ? content?.FindTransition(reference) : null;
            fellBack = curves is null;
            missing += fellBack ? 1 : 0;
            definition = curves is not null && EffectRegistry.Find(curves.MixCode) is { IsTransition: true } mix
                && mix.Type != BuiltInTransitions.Preset
                ? mix
                : crossfade;
        }

        // Una de paquete usa los valores por defecto de su mezcla: sus parámetros son los del preajuste, no los de la mezcla.
        EffectValues values = default;
        foreach (ParameterDefinition p in definition.Parameters)
        {
            double value = curves is null && t.Parameters.TryGetValue(p.Name, out double v) && double.IsFinite(v) ? v : p.DefaultValue;
            values[p.Slot] = (float)p.Clamp(value);
        }

        EffectPlan.WriteChoices(definition, curves is null ? t.Options : EmptyOptions, ref values);
        return new TransitionPlan(t.Id, t.From, t.To, cut.At - cut.Before, cut.At + cut.After, definition.Code, values, curves,
            sequence.Width, sequence.Height, fellBack);
    }

    private static readonly Dictionary<string, string> EmptyOptions = [];

    /// <summary>Hasta dónde llega el material del medio; sin duración conocida, no hay material extra.</summary>
    private static Time Material(MediaRef media) => media.Duration > Time.Zero ? media.Duration : Time.Zero;

    private static IEnumerable<Clip> Ordered(Track track) => track.Clips.OrderBy(c => c.Start);

    private static SourceKey Key(MediaRef media, string projectDirectory) => new(media.Id, media.ResolvePath(projectDirectory));

    /// <summary>Una transición aplicable: índices de sus clips en la lista incluida, el corte y sus dos mitades acotadas.</summary>
    private sealed record Cut(TransitionInstance Transition, int From, int To, Time At, Time Before, Time After);
}
