using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Playback.Composition;

namespace Rodaje.Engine.Timeline;

internal sealed partial class TimelinePlan
{
    /// <summary>Una pista de video: sus clips activos y, si tiene transiciones, el carril compañero con los que entran.</summary>
    private static VideoLayerPlan VideoLayer(Sequence sequence, int i, Dictionary<string, MediaRef> media, string projectDirectory,
        IEffectContent? content, ref int missing)
    {
        Track track = sequence.VideoTracks[i];
        List<Clip> included = [.. Ordered(track).Where(c => c.Enabled && c.Duration > Time.Zero && c.Generator is null
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
            EffectPlan? effects = EffectPlan.From(clip.Effects, content);
            ClipTimeMap map = clip.TimeMap;
            Cut? entering = cuts.Find(x => x.To == c);
            Cut? leaving = cuts.Find(x => x.From == c);

            // El que entra se ve en el carril principal recién al terminar la transición; el que sale sigue hasta
            // entonces con el material que sobra (a la velocidad del borde) o, si no hay, con su último cuadro congelado.
            Time head = entering?.After ?? Time.Zero;
            Time tail = leaving?.After ?? Time.Zero;
            Time material = Material(m);
            TransitionPlan? transition = leaving is { } cut ? Transition(cut, sequence, content, ref missing) : null;
            clips.Add(new VideoClipPlan(clip.Id, key, map, clip.Start + head, clip.End + tail, material, rate, sequence.FrameRate,
                effects, m.Width, m.Height) { Outgoing = transition });

            if (entering is { } e)
            {
                incoming.Add(new VideoClipPlan(clip.Id, key, map, clip.Start - e.Before, clip.Start + e.After, material, rate,
                    sequence.FrameRate, effects, m.Width, m.Height));
            }
        }

        return new VideoLayerPlan(i, track.Id, clips)
        {
            Companion = incoming.Count > 0 && !track.Hidden
                ? new VideoLayerPlan(i, track.Id + VideoLayerPlan.CompanionSuffix, incoming)
                : null,
            Generated = Generated(track, sequence),
        };
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

        // Una de paquete lleva los parámetros y opciones de su mezcla en el preajuste; los de la transición son los suyos.
        IReadOnlyDictionary<string, double> parameters = curves?.MixParameters ?? t.Parameters;
        EffectValues values = default;
        foreach (ParameterDefinition p in definition.Parameters)
        {
            double value = parameters.TryGetValue(p.Name, out double v) && double.IsFinite(v) ? v : p.DefaultValue;
            values[p.Slot] = (float)p.Clamp(value);
        }

        EffectPlan.WriteChoices(definition, curves?.MixOptions ?? t.Options, ref values);
        return new TransitionPlan(t.Id, t.From, t.To, cut.At - cut.Before, cut.At + cut.After, definition.Code, values, curves,
            sequence.Width, sequence.Height, fellBack);
    }
}
