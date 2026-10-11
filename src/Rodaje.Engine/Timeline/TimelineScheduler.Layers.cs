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
        long due = clip.FrameAt(time);
        Present(layer, clip, media, due, running, released);
        bool exact = layer.IsShowing(media, due);
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

    private LayerFrame? Build(int trackIndex, LayerState layer, Time time, TransitionPlan? transition, bool entering, float progress,
        LayerTransition? withTransition)
    {
        if (layer.Current is not { } frame || layer.CurrentClip is not { } clip || !frame.TryRetain())
        {
            return null;
        }

        Time source = clip.ToSource(time);
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
}
