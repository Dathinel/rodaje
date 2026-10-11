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

    /// <summary>
    /// La capa de la pista: el clip del carril principal con sus efectos y, si está en una transición y el carril
    /// compañero ya tiene el clip que entra, la transición con ese clip. Sin principal, el que entra va solo.
    /// </summary>
    private LayerFrame? Compose(int trackIndex, LayerState main, LayerState? incoming, Time time)
    {
        if (main.Current is null || main.CurrentClip is not { } clip)
        {
            return incoming is { Current: not null, CurrentClip: not null } alone ? Build(trackIndex, alone, time, null, false, 0, null) : null;
        }

        if (clip.Outgoing is { } transition && transition.Covers(time)
            && incoming is { Current: not null, CurrentClip: { } entering } && entering.ClipId == transition.ToClipId)
        {
            float progress = transition.Progress(time);
            if (Build(trackIndex, incoming, time, transition, true, progress, null) is { } inFrame)
            {
                LayerFrame? outFrame = Build(trackIndex, main, time, transition, false, progress,
                    new LayerTransition(transition.Code, progress, transition.Values, inFrame));
                if (outFrame is null)
                {
                    inFrame.Dispose();
                }

                return outFrame;
            }
        }

        return Build(trackIndex, main, time, null, false, 0, null);
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
        LayerTransform transform = EffectPlan.Transform(plan, source);
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

        public bool Capture(LayerFrame layer)
        {
            if (!layer.Frame.TryRetain())
            {
                return false;
            }

            _frame = layer.Frame;
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
            if (_frame is not null)
            {
                released.Add(_frame);
                _frame = null;
            }

            if (_hasTransition)
            {
                _incoming!.Release(released);
                _hasTransition = false;
            }
        }
    }
}
