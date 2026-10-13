using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Playback.Composition;

namespace Rodaje.Engine.Timeline;

internal sealed partial class TimelinePlan
{
    /// <summary>
    /// Un compuesto de video se compone aparte si tiene efectos de imagen, transformación o mezcla, o una transición; si no,
    /// se despliega en sus clips (sin coste extra).
    /// </summary>
    private static bool VideoGroup(Track track, Clip compound) =>
        EffectPlan.From(compound.Effects) is not null || TouchesTransition(track, compound);

    private static bool TouchesTransition(Track track, Clip clip) => track.Transitions.Exists(t => t.From == clip.Id || t.To == clip.Id);

    /// <summary>Arma los carriles de video: cada pista y, detrás, los de los grupos de sus compuestos (recursivo).</summary>
    private sealed class VideoBuilder(Project project, Sequence sequence, Dictionary<string, MediaRef> media, string projectDirectory,
        IEffectContent? content, Func<Track, Clip, bool> keep)
    {
        public int Missing;

        private readonly Dictionary<Clip, (Time Before, Time After)> _extend = [];

        public List<VideoLayerPlan> Layers { get; } = [];

        /// <summary>Las pistas del modelo de cada carril (las de los grupos, con los clips recortados y renombrados).</summary>
        public List<Track> Tracks { get; } = [];

        /// <summary>Una pista de video: sus clips activos y, si tiene transiciones, el carril compañero con los que entran.</summary>
        /// <param name="output">Dónde va el carril; los de sus grupos van siempre a <see cref="Layers"/>, justo detrás.</param>
        /// <param name="depth">Niveles de compuestos por encima; 0 en una pista de la secuencia.</param>
        public VideoLayerPlan Add(Track track, int trackIndex, List<VideoLayerPlan> output, bool groups, int depth)
        {
            int position = output.Count;
            output.Add(null!);
            Tracks.Add(track);
            List<Clip> included = [.. Ordered(track).Where(c => c.Enabled && c.Duration > Time.Zero && (c.IsCompound
                ? groups && depth < ModelValidator.MaxCompoundDepth && project.FindSequence(c.SequenceId!) is not null
                : c.Generator is null && media.TryGetValue(c.MediaId, out MediaRef? m) && m.HasVideo && m.FrameRate is not null))];
            List<Cut> cuts = Cuts(track, included);
            var clips = new List<VideoClipPlan>();
            var incoming = new List<VideoClipPlan>();
            var instances = new List<Instance>();
            for (int c = 0; c < included.Count; c++)
            {
                Clip clip = included[c];
                Cut? entering = cuts.Find(x => x.To == c);
                Cut? leaving = cuts.Find(x => x.From == c);

                // El que entra se ve en el carril principal recién al terminar la transición; el que sale sigue hasta
                // entonces con el material que sobra (a la velocidad del borde) o, si no hay, con su último cuadro congelado.
                (Time before, Time after) = _extend.GetValueOrDefault(clip);
                Time head = (entering?.After ?? Time.Zero) - before;
                Time tail = (leaving?.After ?? Time.Zero) + after;
                TransitionPlan? transition = leaving is { } cut ? Transition(cut, sequence, content, ref Missing) : null;
                if (clip.IsCompound)
                {
                    instances.Add(new Instance(clip, clip.Start + head, clip.End + tail, Entering: false, transition));
                    if (entering is { } e)
                    {
                        instances.Add(new Instance(clip, clip.Start - e.Before, clip.Start + e.After, Entering: true, null));
                    }

                    continue;
                }

                MediaRef m = media[clip.MediaId];
                Rational rate = m.FrameRate!.Value;
                SourceKey key = Key(m, projectDirectory);
                EffectPlan? effects = EffectPlan.From(clip.Effects, content);
                ClipTimeMap map = clip.TimeMap;
                Time material = Material(m);
                clips.Add(new VideoClipPlan(clip.Id, key, map, clip.Start + head, clip.End + tail, material, rate, sequence.FrameRate,
                    effects, m.Width, m.Height) { Outgoing = transition });

                if (entering is { } e2)
                {
                    incoming.Add(new VideoClipPlan(clip.Id, key, map, clip.Start - e2.Before, clip.Start + e2.After, material, rate,
                        sequence.FrameRate, effects, m.Width, m.Height));
                }
            }

            (List<GroupClipPlan> main, List<GroupClipPlan> entering2) = Groups(track, trackIndex, instances, depth);
            var plan = new VideoLayerPlan(trackIndex, track.Id, clips)
            {
                Companion = incoming.Count > 0 && !track.Hidden
                    ? new VideoLayerPlan(trackIndex, track.Id + VideoLayerPlan.CompanionSuffix, incoming) { IsNested = depth > 0 }
                    : null,
                Generated = Generated(track, sequence),
                Groups = main,
                IncomingGroups = entering2,
                IsNested = depth > 0,
            };
            output[position] = plan;
            return plan;
        }

        /// <summary>
        /// Reparte los tramos de los compuestos en grupos que no se pisan (el que sale y el que entra en una transición van en
        /// grupos distintos) y arma los carriles de cada grupo con lo que muestra cada tramo.
        /// </summary>
        private (List<GroupClipPlan> Main, List<GroupClipPlan> Incoming) Groups(Track track, int trackIndex, List<Instance> instances, int depth)
        {
            var main = new List<GroupClipPlan>();
            var entering = new List<GroupClipPlan>();
            if (instances.Count == 0)
            {
                return (main, entering);
            }

            instances.Sort((a, b) => a.Start.CompareTo(b.Start));
            var slots = new List<List<Instance>>();
            var ends = new List<Time>();
            foreach (Instance instance in instances)
            {
                int slot = ends.FindIndex(end => end <= instance.Start);
                if (slot < 0)
                {
                    slot = slots.Count;
                    slots.Add([]);
                    ends.Add(instance.End);
                }

                slots[slot].Add(instance);
                ends[slot] = instance.End;
            }

            for (int s = 0; s < slots.Count; s++)
            {
                var slotTracks = new List<Track>();
                foreach (Instance instance in slots[s])
                {
                    Sequence nested = project.FindSequence(instance.Clip.SequenceId!)!;
                    Sequence flat = CompoundOps.Flatten(project, nested, keep, [nested.Id]);
                    List<Track> inner = flat.VideoTracks.FindAll(t => !t.Hidden);
                    Clip compound = instance.Clip;
                    var window = new Clip
                    {
                        Start = instance.Start,
                        SourceIn = compound.SourceIn + (instance.Start - compound.Start),
                        SourceOut = compound.SourceIn + (instance.End - compound.Start),
                    };
                    string prefix = compound.Id + (instance.Entering ? VideoLayerPlan.CompanionSuffix : "") + CompoundOps.IdSeparator;
                    for (int k = 0; k < inner.Count; k++)
                    {
                        if (k == slotTracks.Count)
                        {
                            slotTracks.Add(new Track { Id = $"{track.Id}{CompoundOps.IdSeparator}g{s}{CompoundOps.IdSeparator}{k}", Kind = TrackKind.Video });
                        }

                        ExtendEdges(CompoundOps.Window(inner[k], window, slotTracks[k], id => prefix + id), instance.Start, instance.End,
                            compound.Start, compound.End, _extend);
                    }
                }

                var lanes = new List<VideoLayerPlan>(slotTracks.Count);
                foreach (Track slotTrack in slotTracks)
                {
                    slotTrack.SortClips();
                    lanes.Add(Add(slotTrack, trackIndex, Layers, groups: true, depth + 1));
                }

                foreach (Instance instance in slots[s])
                {
                    Clip compound = instance.Clip;
                    var group = new GroupClipPlan(compound.Id, compound.Start, compound.SourceIn, instance.Start, instance.End,
                        EffectPlan.From(compound.Effects, content), lanes) { Outgoing = instance.Outgoing };
                    (instance.Entering ? entering : main).Add(group);
                }
            }

            main.Sort((a, b) => a.Start.CompareTo(b.Start));
            entering.Sort((a, b) => a.Start.CompareTo(b.Start));
            return (main, entering);
        }

        /// <summary>Un tramo de un compuesto que se compone aparte: el del carril principal o el que entra en una transición.</summary>
        private sealed record Instance(Clip Clip, Time Start, Time End, bool Entering, TransitionPlan? Outgoing);
    }

    /// <summary>
    /// Los títulos activos de la pista. No entran en <c>included</c>: no tienen decodificador ni transiciones (una
    /// transición contra un título se ve como corte) y no cuentan para la demanda.
    /// </summary>
    private static List<GeneratedClipPlan> Generated(Track track, Sequence sequence)
    {
        var generated = new List<GeneratedClipPlan>();
        foreach (Clip clip in Ordered(track))
        {
            if (clip is { Enabled: true, Generator: not null } && clip.Duration > Time.Zero
                && GeneratedClipPlan.From(clip, sequence.Width, sequence.Height) is { } plan)
            {
                generated.Add(plan);
            }
        }

        return generated;
    }

    private static TransitionPlan Transition(Cut cut, Sequence sequence, IEffectContent? content, ref int missing)
    {
        TransitionPlan plan = TransitionPlan.From(cut.Transition, cut.At - cut.Before, cut.At + cut.After, sequence.Width, sequence.Height,
            sequence.FrameRate, content);
        missing += plan.FellBack ? 1 : 0;
        return plan;
    }
}
