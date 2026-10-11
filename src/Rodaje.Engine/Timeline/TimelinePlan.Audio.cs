using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Playback.Composition;

namespace Rodaje.Engine.Timeline;

internal sealed partial class TimelinePlan
{
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
                    Gain(clip, trackDb, entering, leaving), Retime(clip)));
            }
        }

        return new AudioTrackPlan(track.Id, clips);
    }

    /// <summary>Velocidad del clip; nula sin ella o si deja el origen tal cual (1× sin reversa ni congelado).</summary>
    private static AudioRetime? Retime(Clip clip)
    {
        if (clip.Speed is not { } speed)
        {
            return null;
        }

        ClipTimeMap map = clip.TimeMap;
        return map.IsIdentity ? null : new AudioRetime(map, speed.PreservePitch);
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
}
