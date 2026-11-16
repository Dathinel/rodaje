using Rodaje.Core.Timing;
using Rodaje.Playback;
using Rodaje.Playback.Composition;

namespace Rodaje.Engine.Timeline;

/// <summary>De dónde saca <see cref="FrameEvaluator"/> el cuadro decodificado de un clip.</summary>
internal interface IEvaluatorFrames
{
    /// <summary>
    /// El cuadro <paramref name="index"/> del clip del carril <paramref name="ordinal"/> (el principal o, con
    /// <paramref name="incoming"/>, el que entra), con la forma del píxel y el giro de su medio. La capa que lo recibe es
    /// su dueña.
    /// </summary>
    (DecodedFrame Frame, Rational SampleAspectRatio, int Rotation) Get(int ordinal, VideoClipPlan clip, bool incoming, long index, long sequenceFrame,
        CancellationToken cancellationToken);
}

/// <summary>
/// Las capas de un instante de la secuencia sobre <see cref="TimelinePlan"/>, con la composición ya completa: qué clip,
/// transición, compuesto o título se ve en cada carril, y su transformación, efectos y desenfoque de movimiento evaluados.
/// Lo comparten la exportación (<c>OfflineTimeline</c>) y la receta del render en caché (D-060), para que la huella
/// describa exactamente lo que se exporta.
/// </summary>
/// <remarks>Se usa desde un solo hilo. Las listas de efectos valen hasta dos llamadas más (como en la vista previa).</remarks>
internal sealed class FrameEvaluator
{
    private readonly Time _frameDuration;

    public FrameEvaluator(TimelinePlan plan, IEffectResources? content)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Plan = plan;
        _frameDuration = Time.FromFrames(1, plan.FrameRate);
        Lanes = [.. plan.Layers.Select(l => new Lane(l) { Content = content })];
    }

    public TimelinePlan Plan { get; }

    /// <summary>Un carril por capa del plan, en el orden de <see cref="TimelinePlan.Layers"/> (su <see cref="VideoLayerPlan.Ordinal"/>).</summary>
    public Lane[] Lanes { get; }

    /// <summary>Elige en cada carril lo que se ve en <paramref name="time"/>; va antes de pedir los cuadros y de <see cref="Build"/>.</summary>
    public void Pick(Time time)
    {
        foreach (Lane lane in Lanes)
        {
            lane.Pick(time);
        }
    }

    /// <summary>Las capas de <paramref name="time"/>, de abajo arriba, con lo elegido en el último <see cref="Pick"/>.</summary>
    public CompositionFrame Build(Time time, long frame, IEvaluatorFrames frames, CancellationToken cancellationToken)
    {
        var layers = new List<LayerFrame>(Lanes.Length);
        try
        {
            foreach (Lane lane in Lanes)
            {
                if (!lane.Layer.IsNested && Assemble(lane, time, frame, frames, cancellationToken) is { } layer)
                {
                    layers.Add(layer);
                }
            }

            return new CompositionFrame(time, Plan.Width, Plan.Height, layers);
        }
        catch
        {
            layers.ForEach(l => l.Dispose());
            throw;
        }
    }

    /// <summary>Como <c>TimelineScheduler.Assemble</c>: el título, el compuesto o el clip del carril, con su transición.</summary>
    private LayerFrame? Assemble(Lane lane, Time time, long frame, IEvaluatorFrames frames, CancellationToken cancellationToken)
    {
        if (lane.Layer.GeneratedAt(time) is { } title)
        {
            return title.Layer(lane.Layer.TrackIndex, time, lane.TitleEffects, lane.Content, _frameDuration.Ticks);
        }

        GroupClipPlan? group = lane.Layer.GroupAt(time);
        if (group is null && lane.Main is null)
        {
            return lane.Layer.IncomingGroupAt(time) is { } alone
                ? Group(lane, alone, time, frame, frames, null, entering: true, 0, null, cancellationToken)
                : null;
        }

        LayerFrame? incoming = null;
        try
        {
            if (lane.Transition is { } transition)
            {
                float progress = transition.Progress(time);
                incoming = lane.Incoming is { } entering
                    ? Layer(lane, entering, frames, incoming: true, lane.IncomingEffects, time, frame, transition, progress, null, cancellationToken)
                    : lane.Layer.IncomingGroupAt(time) is { } enteringGroup
                        ? Group(lane, enteringGroup, time, frame, frames, transition, entering: true, progress, null, cancellationToken)
                        : null;
                if (incoming is not null)
                {
                    LayerTransition mix = transition.Layer(time, progress, incoming);
                    return group is not null
                        ? Group(lane, group, time, frame, frames, transition, entering: false, progress, mix, cancellationToken)
                        : Layer(lane, lane.Main!, frames, incoming: false, lane.MainEffects, time, frame, transition, progress, mix, cancellationToken);
                }
            }

            return group is not null
                ? Group(lane, group, time, frame, frames, null, entering: false, 0, null, cancellationToken)
                : Layer(lane, lane.Main!, frames, incoming: false, lane.MainEffects, time, frame, null, 0, null, cancellationToken);
        }
        catch
        {
            incoming?.Dispose();
            throw;
        }
    }

    /// <summary>Como <c>TimelineScheduler.BuildGroup</c>: las capas de los carriles del compuesto en una capa compuesta.</summary>
    private LayerFrame? Group(Lane lane, GroupClipPlan group, Time time, long frame, IEvaluatorFrames frames, TransitionPlan? transition,
        bool entering, float progress, LayerTransition? withTransition, CancellationToken cancellationToken)
    {
        var children = new List<LayerFrame>(group.Lanes.Count);
        try
        {
            foreach (VideoLayerPlan inner in group.Lanes)
            {
                if (Assemble(Lanes[inner.Ordinal], time, frame, frames, cancellationToken) is { } child)
                {
                    children.Add(child);
                }
            }
        }
        catch
        {
            children.ForEach(c => c.Dispose());
            throw;
        }

        if (children.Count == 0 && transition is null)
        {
            return null;
        }

        Time source = group.EffectTimeAt(time);
        EffectPlan? plan = group.Effects;
        LayerTransform transform = EffectPlan.Transform(plan, source);
        if (transition is not null)
        {
            transform = transition.Apply(transform, entering, progress);
        }

        IReadOnlyList<LayerEffect> effects = [];
        EffectListRing ring = entering ? lane.IncomingGroupEffects : lane.GroupEffects;
        EffectList? lists = null;
        if (plan is { EffectCount: > 0 })
        {
            lists = ring.Next();
            plan.Evaluate(source, lists.Reset(plan.EffectCount), lane.Content);
            effects = lists;
        }

        IReadOnlyList<LayerTransform>? motion = MotionSampler.Sample(plan?.Motion, new GroupMotionSource(group, transition, entering, progress), time,
            _frameDuration.Ticks, ring, ref lists);
        return new LayerFrame(lane.Layer.TrackIndex, group.ClipId, children, transform)
        {
            Effects = effects,
            Blend = plan?.Blend ?? BlendMode.Normal,
            Transition = withTransition,
            MotionSamples = motion,
        };
    }

    /// <summary>Como <c>TimelineScheduler.Build</c>: transformación, transición, efectos en tiempo del origen y mezcla.</summary>
    private LayerFrame Layer(Lane lane, VideoClipPlan clip, IEvaluatorFrames frames, bool incoming, EffectListRing ring, Time time, long sequenceFrame,
        TransitionPlan? transition, float progress, LayerTransition? withTransition, CancellationToken cancellationToken)
    {
        (DecodedFrame frame, Rational sampleAspectRatio, int rotation) = frames.Get(lane.Layer.Ordinal, clip, incoming, clip.FrameAt(time), sequenceFrame, cancellationToken);
        Time source = clip.EffectTimeAt(time);
        EffectPlan? plan = clip.Effects;
        LayerTransform transform = EffectPlan.Stabilized(plan, EffectPlan.Transform(plan, source), frame.Index);
        if (transition is not null)
        {
            transform = transition.Apply(transform, incoming, progress);
        }

        IReadOnlyList<LayerEffect> effects = [];
        EffectList? lists = null;
        if (plan is { EffectCount: > 0 })
        {
            lists = ring.Next();
            plan.Evaluate(source, lists.Reset(plan.EffectCount), lane.Content);
            effects = lists;
        }

        IReadOnlyList<LayerTransform>? motion = MotionSampler.Sample(plan?.Motion,
            new ClipMotionSource(plan, clip, frame.Index, transition, incoming, progress), time, _frameDuration.Ticks, ring, ref lists);
        return new LayerFrame(lane.Layer.TrackIndex, clip.ClipId, frame, transform, sampleAspectRatio, rotation)
        {
            Effects = effects,
            Blend = plan?.Blend ?? BlendMode.Normal,
            Transition = withTransition,
            MotionSamples = motion,
        };
    }

    /// <summary>Una pista visible: su carril principal y el compañero, y lo que muestra cada uno en el instante pedido.</summary>
    internal sealed class Lane(VideoLayerPlan layer)
    {
        public VideoLayerPlan Layer { get; } = layer;

        public IEffectResources? Content { get; init; }

        public EffectListRing MainEffects { get; } = new();

        public EffectListRing IncomingEffects { get; } = new();

        public EffectListRing TitleEffects { get; } = new();

        public EffectListRing GroupEffects { get; } = new();

        public EffectListRing IncomingGroupEffects { get; } = new();

        public VideoClipPlan? Main { get; private set; }

        public VideoClipPlan? Incoming { get; private set; }

        public TransitionPlan? Transition { get; private set; }

        public VideoClipPlan? AheadMain { get; private set; }

        public VideoClipPlan? AheadIncoming { get; private set; }

        public void Pick(Time time) => (Main, Incoming, Transition) = Choose(time);

        public void PickAhead(Time time) => (AheadMain, AheadIncoming, _) = Choose(time);

        /// <summary>
        /// Como <c>TimelineScheduler.Compose</c> con todas las capas listas: el clip del carril principal y, si está en su
        /// transición, el que entra por el compañero; sin principal, el que entra va solo y sin transición.
        /// </summary>
        /// <remarks>Con un compuesto en el principal no hay clip principal; uno que entra va en <see cref="Incoming"/> solo con su transición.</remarks>
        private (VideoClipPlan?, VideoClipPlan?, TransitionPlan?) Choose(Time time)
        {
            VideoClipPlan? main = Layer.ClipAt(time);
            VideoClipPlan? incoming = Layer.Companion?.ClipAt(time);
            GroupClipPlan? group = Layer.GroupAt(time);
            GroupClipPlan? enteringGroup = Layer.IncomingGroupAt(time);
            if (group is null && main is null)
            {
                return (enteringGroup is null ? incoming : null, null, null);
            }

            if ((group is not null ? group.Outgoing : main!.Outgoing) is not { } transition || !transition.Covers(time))
            {
                return (main, null, null);
            }

            if (incoming is not null && incoming.ClipId == transition.ToClipId)
            {
                return (main, incoming, transition);
            }

            return (main, null, enteringGroup?.ClipId == transition.ToClipId ? transition : null);
        }
    }
}
