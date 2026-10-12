using Rodaje.Core.Timing;
using Rodaje.Playback;
using Rodaje.Playback.Composition;

namespace Rodaje.Engine.Timeline;

// Capas: qué muestra cada carril en un refresco y cómo se arma su LayerFrame con efectos y transición.
internal sealed partial class TimelineScheduler
{
    /// <summary>Elige el cuadro del carril para <paramref name="time"/> y cuenta lo que llegó tarde; no reserva la capa.</summary>
    private LayerState Step(VideoLayerPlan lane, Time time, bool running, List<DecodedFrame> released, ref bool settled)
    {
        LayerState layer = Layer(lane);
        VideoClipPlan? clip = lane.ClipAt(time);
        if (clip is null)
        {
            // En un hueco no hay cuadro exacto que esperar: el clip que llegue reproduciendo es un corte normal.
            layer.Release(released);
            layer.ActiveClip = null;
            layer.AwaitingCut = false;
            layer.Failure = null;
            layer.Resync = false;
            return layer;
        }

        MediaState media = Media(clip);
        if (media.Failure is not null)
        {
            // Sin su medio la capa queda vacía: mostrar el último cuadro del clip anterior sería mentir.
            layer.Release(released);
            layer.ActiveClip = clip;
            layer.AwaitingCut = false;
            layer.Failure = media.Failure;
            return layer;
        }

        layer.Failure = null;
        long due = Due(clip, media, time, running, out long exactFrame, out bool approximate);
        if (approximate && layer.CurrentMedia == media && layer.CurrentClip?.ClipId == clip.ClipId && layer.Current is { } current
            && current.Index > due && current.Index <= exactFrame)
        {
            // Lo que ya está en pantalla está más cerca del cuadro exacto que el keyframe: no se vuelve atrás.
            due = current.Index;
        }

        Present(layer, clip, media, due, running, approximate, released);
        bool exact = layer.IsShowing(media, due);
        if (approximate && layer.CurrentMedia == media && layer.Current is { } shown && shown.Index != exactFrame)
        {
            layer.Approximated++;
        }

        bool missing = media.Unavailable.Contains(due);
        if (missing && !exact && layer.CurrentClip is { } stale && stale.ClipId != clip.ClipId)
        {
            layer.Release(released);
        }

        settled &= exact || missing;
        if (running && !exact && !missing && !_seekPending)
        {
            layer.Late++;
            if (layer.AwaitingCut)
            {
                layer.LateAtCuts++;
            }
        }

        if (exact)
        {
            layer.AwaitingCut = false;
        }

        return layer;
    }

    /// <summary>La capa del carril en este refresco: su título, su compuesto o su clip (con la transición, si la hay).</summary>
    private LayerFrame? Assemble(VideoLayerPlan plan, Time time)
    {
        (_, LayerState main, LayerState? incoming) = _stepped[plan.Ordinal];
        return plan.GeneratedAt(time) is { } title
            ? title.Layer(plan.TrackIndex, time, TitleEffects(plan), _content)
            : Compose(plan, main, incoming, time);
    }

    /// <summary>
    /// La capa de la pista: el clip (o el compuesto) del carril principal con sus efectos y, si está en una transición y
    /// el carril compañero ya tiene lo que entra, la transición con eso. Sin principal, lo que entra va solo.
    /// </summary>
    private LayerFrame? Compose(VideoLayerPlan plan, LayerState main, LayerState? incoming, Time time)
    {
        int trackIndex = plan.TrackIndex;
        GroupClipPlan? group = plan.GroupAt(time);
        if (group is null && (main.Current is null || main.CurrentClip is null))
        {
            return plan.IncomingGroupAt(time) is { } enteringGroup ? BuildGroup(plan, enteringGroup, time, null, true, 0, null)
                : incoming is { Current: not null, CurrentClip: not null } alone ? Build(trackIndex, alone, time, null, false, 0, null)
                : null;
        }

        if ((group is not null ? group.Outgoing : main.CurrentClip!.Outgoing) is { } transition && transition.Covers(time))
        {
            float progress = transition.Progress(time);
            LayerFrame? inFrame = plan.IncomingGroupAt(time) is { } entering && entering.ClipId == transition.ToClipId
                ? BuildGroup(plan, entering, time, transition, true, progress, null)
                : incoming is { Current: not null, CurrentClip: { } clip } && clip.ClipId == transition.ToClipId
                    ? Build(trackIndex, incoming, time, transition, true, progress, null)
                    : null;
            if (inFrame is not null)
            {
                var mix = new LayerTransition(transition.Code, progress, transition.Values, inFrame);
                LayerFrame? outFrame = group is not null
                    ? BuildGroup(plan, group, time, transition, false, progress, mix)
                    : Build(trackIndex, main, time, transition, false, progress, mix);
                if (outFrame is null)
                {
                    inFrame.Dispose();
                }

                return outFrame;
            }
        }

        return group is not null ? BuildGroup(plan, group, time, null, false, 0, null) : Build(trackIndex, main, time, null, false, 0, null);
    }

    /// <summary>
    /// Un compuesto: las capas de sus carriles en una capa compuesta con su transformación, sus efectos y su transición.
    /// Sin nada que mostrar ni transición, no hay capa.
    /// </summary>
    private LayerFrame? BuildGroup(VideoLayerPlan plan, GroupClipPlan group, Time time, TransitionPlan? transition, bool entering, float progress,
        LayerTransition? withTransition)
    {
        var children = new List<LayerFrame>(group.Lanes.Count);
        foreach (VideoLayerPlan lane in group.Lanes)
        {
            if (Assemble(lane, time) is { } child)
            {
                children.Add(child);
            }
        }

        if (children.Count == 0 && withTransition is null && transition is null)
        {
            return null;
        }

        Time source = group.EffectTimeAt(time);
        EffectPlan? effects = group.Effects;
        LayerTransform transform = EffectPlan.Transform(effects, source);
        if (transition is not null)
        {
            transform = transition.Apply(transform, entering, progress);
        }

        IReadOnlyList<LayerEffect> list = [];
        if (effects is { EffectCount: > 0 })
        {
            EffectList next = GroupEffects(plan, entering).Next();
            effects.Evaluate(source, next.Reset(effects.EffectCount), _content);
            list = next;
        }

        return new LayerFrame(plan.TrackIndex, group.ClipId, children, transform)
        {
            Effects = list,
            Blend = effects?.Blend ?? BlendMode.Normal,
            Transition = withTransition,
        };
    }

    private EffectListRing GroupEffects(VideoLayerPlan lane, bool entering)
    {
        string key = entering ? lane.TrackId + VideoLayerPlan.CompanionSuffix : lane.TrackId;
        if (!_groupEffects.TryGetValue(key, out EffectListRing? ring))
        {
            ring = new EffectListRing();
            _groupEffects.Add(key, ring);
        }

        return ring;
    }

    /// <summary>
    /// Carril generado: pide a quien rasteriza los títulos que se verán en <see cref="_titleLookahead"/> y, si el visible
    /// no está, la composición no está completa (tras un seek se espera como a un cuadro) y reproduciendo cuenta tarde.
    /// </summary>
    private void StepTitles(VideoLayerPlan lane, Time time, bool running, ref bool settled)
    {
        if (_titles is null || lane.Generated.Count == 0)
        {
            return;
        }

        IReadOnlyList<GeneratedClipPlan> titles = lane.Generated;
        Time until = time + _titleLookahead;
        for (int i = lane.FirstGeneratedEndingAfter(time); i < titles.Count && titles[i].Start <= until; i++)
        {
            bool ready = _titles.Prepare(titles[i].Start <= time ? titles[i].ContentAt(time) : titles[i].Content);
            foreach (GeneratedContent other in titles[i].Contents)
            {
                _titles.Prepare(other);
            }

            if (titles[i].Start <= time && !ready)
            {
                settled = false;
                if (running && !_seekPending)
                {
                    _lateTitles++;
                }
            }
        }
    }

    private EffectListRing TitleEffects(VideoLayerPlan lane)
    {
        if (!_titleEffects.TryGetValue(lane.TrackId, out EffectListRing? ring))
        {
            ring = new EffectListRing();
            _titleEffects.Add(lane.TrackId, ring);
        }

        return ring;
    }

    /// <summary>Guarda la composición completa recién armada para volver a mostrarla mientras un seek espera cuadros.</summary>
    private void Hold(List<LayerFrame> layers, List<DecodedFrame> released)
    {
        ReleaseHeld(released);
        foreach (LayerFrame layer in layers)
        {
            if (_heldCount == _held.Count)
            {
                _held.Add(new HeldLayer());
            }

            if (_held[_heldCount].Capture(layer))
            {
                _heldCount++;
            }
        }
    }

    private void PresentHeld(List<LayerFrame> layers)
    {
        for (int i = 0; i < _heldCount; i++)
        {
            if (_held[i].Present() is { } layer)
            {
                layers.Add(layer);
            }
        }
    }

    private void ReleaseHeld(List<DecodedFrame> released)
    {
        for (int i = 0; i < _heldCount; i++)
        {
            _held[i].Release(released);
        }

        _heldCount = 0;
    }

    private LayerFrame? Build(int trackIndex, LayerState layer, Time time, TransitionPlan? transition, bool entering, float progress,
        LayerTransition? withTransition)
    {
        if (layer.Current is not { } frame || layer.CurrentClip is not { } clip || !frame.TryRetain())
        {
            return null;
        }

        Time source = clip.EffectTimeAt(time);
        EffectPlan? plan = clip.Effects;
        LayerTransform transform = EffectPlan.Stabilized(plan, EffectPlan.Transform(plan, source), frame.Index);
        if (transition is not null)
        {
            transform = transition.Apply(transform, entering, progress);
        }

        IReadOnlyList<LayerEffect> effects = [];
        if (plan is { EffectCount: > 0 })
        {
            EffectList list = layer.Effects.Next();
            plan.Evaluate(source, list.Reset(plan.EffectCount), _content);
            effects = list;
        }

        Look look = layer.CurrentLook;
        return new LayerFrame(trackIndex, clip.ClipId, frame, transform, look.SampleAspectRatio, look.Rotation)
        {
            DisplayWidth = look.DisplayWidth,
            DisplayHeight = look.DisplayHeight,
            IsProxy = look.IsProxy,
            Effects = effects,
            Blend = plan?.Blend ?? BlendMode.Normal,
            Transition = withTransition,
        };
    }

    /// <summary>Una capa de la última composición completa, con su propia referencia al cuadro y su copia de los efectos.</summary>
    private sealed class HeldLayer
    {
        private readonly EffectList _effects = new();
        private DecodedFrame? _frame;
        private int _trackIndex;
        private string _clipId = "";
        private LayerTransform _transform;
        private Rational _sampleAspectRatio;
        private int _rotation;
        private int _displayWidth;
        private int _displayHeight;
        private bool _isProxy;
        private BlendMode _blend;
        private bool _hasTransition;
        private int _transitionCode;
        private float _progress;
        private EffectValues _transitionValues;
        private HeldLayer? _incoming;
        private GeneratedContent? _generated;
        private readonly List<HeldLayer> _nested = [];
        private int _nestedCount = -1;

        public bool Capture(LayerFrame layer)
        {
            if (layer.Nested is { } nested)
            {
                _nestedCount = 0;
                foreach (LayerFrame child in nested)
                {
                    if (_nestedCount == _nested.Count)
                    {
                        _nested.Add(new HeldLayer());
                    }

                    if (_nested[_nestedCount].Capture(child))
                    {
                        _nestedCount++;
                    }
                }
            }
            else if (!layer.Frame.TryRetain())
            {
                return false;
            }
            else
            {
                _frame = layer.Frame;
            }

            _generated = layer.Generated;
            _trackIndex = layer.TrackIndex;
            _clipId = layer.ClipId;
            _transform = layer.Transform;
            _sampleAspectRatio = layer.SampleAspectRatio;
            _rotation = layer.Rotation;
            _displayWidth = layer.DisplayWidth;
            _displayHeight = layer.DisplayHeight;
            _isProxy = layer.IsProxy;
            _blend = layer.Blend;
            Span<LayerEffect> effects = _effects.Reset(layer.Effects.Count);
            for (int i = 0; i < effects.Length; i++)
            {
                effects[i] = layer.Effects[i];
            }

            _hasTransition = false;
            if (layer.Transition is { } transition)
            {
                _incoming ??= new HeldLayer();
                if (_incoming.Capture(transition.Incoming))
                {
                    _hasTransition = true;
                    _transitionCode = transition.Code;
                    _progress = transition.Progress;
                    _transitionValues = transition.Values;
                }
            }

            return true;
        }

        public LayerFrame? Present()
        {
            if (_nestedCount >= 0)
            {
                var children = new List<LayerFrame>(_nestedCount);
                for (int i = 0; i < _nestedCount; i++)
                {
                    if (_nested[i].Present() is { } child)
                    {
                        children.Add(child);
                    }
                }

                return new LayerFrame(_trackIndex, _clipId, children, _transform)
                {
                    Effects = _effects.Count > 0 ? _effects : [],
                    Blend = _blend,
                    Transition = _hasTransition && _incoming!.Present() is { } entering
                        ? new LayerTransition(_transitionCode, _progress, _transitionValues, entering)
                        : null,
                };
            }

            if (_generated is not null)
            {
                return new LayerFrame(_trackIndex, _clipId, _generated, _transform)
                {
                    Effects = _effects.Count > 0 ? _effects : [],
                    Blend = _blend,
                };
            }

            if (_frame is null || !_frame.TryRetain())
            {
                return null;
            }

            LayerTransition? transition = null;
            if (_hasTransition && _incoming!.Present() is { } incoming)
            {
                transition = new LayerTransition(_transitionCode, _progress, _transitionValues, incoming);
            }

            return new LayerFrame(_trackIndex, _clipId, _frame, _transform, _sampleAspectRatio, _rotation)
            {
                DisplayWidth = _displayWidth,
                DisplayHeight = _displayHeight,
                IsProxy = _isProxy,
                Effects = _effects.Count > 0 ? _effects : [],
                Blend = _blend,
                Transition = transition,
            };
        }

        public void Release(List<DecodedFrame> released)
        {
            for (int i = 0; i < _nestedCount; i++)
            {
                _nested[i].Release(released);
            }

            _nestedCount = -1;
            if (_frame is not null)
            {
                released.Add(_frame);
                _frame = null;
            }

            _generated = null;

            if (_hasTransition)
            {
                _incoming!.Release(released);
                _hasTransition = false;
            }
        }
    }
}
