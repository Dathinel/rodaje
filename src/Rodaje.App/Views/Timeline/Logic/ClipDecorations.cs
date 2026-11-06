using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Views.Timeline;

/// <summary>Lo que se dibuja sobre los clips además del bloque: keyframes, fundidos, envolvente y forma de onda.</summary>
public static class ClipDecorations
{
    /// <summary>Efecto de volumen que dibuja y edita la herramienta de envolvente, con su ganancia en dB.</summary>
    public const string VolumeEffect = BuiltInEffects.Audio;
    public const string GainParameter = BuiltInEffects.AudioParameters.GainDb;

    /// <summary>Ganancia del borde de arriba del clip; más arriba no se dibuja (el inspector llega a +24 dB).</summary>
    public const double MaxEnvelopeDb = 6;

    /// <summary>Ganancia del borde de abajo: silencio.</summary>
    public const double MinEnvelopeDb = EffectRegistry.SilenceDb;

    /// <summary>Duración de cada barra de la forma de onda provisional, en tiempo del origen.</summary>
    public static readonly Time WaveformBucket = Time.FromSeconds(0.04);

    /// <summary>Opacidad del velo de un clip desactivado o de una pista oculta o silenciada.</summary>
    public const double MutedVeil = 0.55;

    /// <summary>Opacidad del velo de un clip de una pista bloqueada (visible y con sonido).</summary>
    public const double LockedVeil = 0.3;

    /// <summary>
    /// Cuánto se vela el clip según su estado y el de su pista, siempre el actual: ocultar o silenciar pesa más que
    /// bloquear, y sin nada de eso no hay velo.
    /// </summary>
    public static double Veil(Clip clip, Track track) => Veil(clip, track, silencedBySolo: false);

    /// <summary>Como <see cref="Veil(Clip, Track)"/>; una pista callada por el solo de otra se vela como si estuviera silenciada.</summary>
    public static double Veil(Clip clip, Track track, bool silencedBySolo)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(track);
        if (!clip.Enabled || track.Hidden || track.Muted || silencedBySolo)
        {
            return MutedVeil;
        }

        return track.Locked ? LockedVeil : 0;
    }

    /// <summary>Hay algún solo puesto en la secuencia (en una pista de audio o en un bus).</summary>
    public static bool IsSoloing(Sequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        return sequence.AudioTracks.Exists(t => t.Solo) || sequence.AudioBuses.Exists(b => b.Solo);
    }

    /// <summary>
    /// La pista de audio no suena porque otra (o un bus) tiene solo: con algún solo puesto solo suenan las pistas con
    /// solo y las que salen a un bus con solo, igual que las mezcla el motor.
    /// </summary>
    public static bool SilencedBySolo(Sequence sequence, Track track)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(track);
        if (track.Kind != TrackKind.Audio || track.Solo || !IsSoloing(sequence))
        {
            return false;
        }

        return !(track.Output is { } output && sequence.AudioBuses.Exists(b => b.Id == output && b.Solo));
    }

    /// <summary>Los instantes de la línea de tiempo con keyframes en algún parámetro del clip, sin repetir y en orden.</summary>
    public static List<Time> KeyframeTimes(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var times = new SortedSet<long>();
        ClipTimeMap? map = null;
        foreach (EffectInstance effect in clip.Effects)
        {
            foreach (Parameter parameter in effect.Parameters.Values)
            {
                foreach (Keyframe keyframe in parameter.Keyframes)
                {
                    map ??= clip.TimeMap;
                    if (ClipMapping.TimelineOfEffect(map, keyframe.Time) is { } at)
                    {
                        times.Add(at.Ticks);
                    }
                }
            }
        }

        return [.. times.Select(Time.FromTicks)];
    }

    /// <summary>Los cortes donde un clip termina justo donde empieza el siguiente: ahí va el fundido cruzado automático.</summary>
    public static List<Time> AdjacentCuts(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        var cuts = new List<Time>();
        for (int i = 1; i < track.Clips.Count; i++)
        {
            if (track.Clips[i - 1].End == track.Clips[i].Start)
            {
                cuts.Add(track.Clips[i].Start);
            }
        }

        return cuts;
    }

    /// <summary>La ganancia en dB del clip en un instante de la línea de tiempo; sin efecto de volumen, 0.</summary>
    public static double GainDbAt(Clip clip, Time timelineTime)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return GainDbAt(clip, clip.TimeMap, timelineTime);
    }

    /// <summary>Como <see cref="GainDbAt(Clip, Time)"/> con el mapeo ya calculado (se dibuja punto por punto).</summary>
    public static double GainDbAt(Clip clip, ClipTimeMap map, Time timelineTime)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(map);
        return VolumeOf(clip) is { } volume && volume.Parameters.TryGetValue(GainParameter, out Parameter? gain)
            ? gain.Evaluate(map.EffectTime(timelineTime))
            : 0;
    }

    /// <summary>Si la ganancia del clip cambia en el tiempo (la envolvente necesita más de dos puntos).</summary>
    public static bool HasAnimatedGain(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return VolumeOf(clip) is { } volume && volume.Parameters.TryGetValue(GainParameter, out Parameter? p) && p.Keyframes.Count > 0;
    }

    /// <summary>El primer efecto de volumen activo del clip: el que suena y el que edita la envolvente.</summary>
    public static EffectInstance? VolumeOf(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return clip.Effects.Find(e => e.Type == VolumeEffect && e.Enabled);
    }

    /// <summary>Los puntos de la envolvente: instante de la línea de tiempo y ganancia de cada keyframe dentro del clip.</summary>
    public static List<(Time Time, double GainDb)> GainPoints(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var points = new List<(Time, double)>();
        if (VolumeOf(clip) is { } volume && volume.Parameters.TryGetValue(GainParameter, out Parameter? gain) && gain.Keyframes.Count > 0)
        {
            ClipTimeMap map = clip.TimeMap;
            foreach (Keyframe keyframe in gain.Keyframes)
            {
                if (ClipMapping.TimelineOfEffect(map, keyframe.Time) is { } at)
                {
                    points.Add((at, keyframe.Value));
                }
            }

            // En reversa los keyframes van al revés en la línea de tiempo.
            if (map.Reverse)
            {
                points.Reverse();
            }
        }

        return points;
    }

    /// <summary>
    /// Altura de la envolvente (0 abajo, 1 arriba) para una ganancia: lineal en amplitud, como se oye un fader, con
    /// 0 dB a media altura y <see cref="MaxEnvelopeDb"/> arriba.
    /// </summary>
    public static double EnvelopeFraction(double gainDb)
    {
        if (gainDb <= MinEnvelopeDb)
        {
            return 0;
        }

        return Math.Clamp(Math.Pow(10, (gainDb - MaxEnvelopeDb) / 20), 0, 1);
    }

    /// <summary>La ganancia de una altura de la envolvente; la inversa de <see cref="EnvelopeFraction"/>, en décimas de dB.</summary>
    public static double GainDbFromFraction(double fraction)
    {
        if (!double.IsFinite(fraction) || fraction <= 0)
        {
            return MinEnvelopeDb;
        }

        double db = MaxEnvelopeDb + (20 * Math.Log10(Math.Min(fraction, 1)));
        return Math.Round(Math.Max(db, MinEnvelopeDb), 1);
    }

    /// <summary>
    /// Altura (0 a 1) de la forma de onda provisional en un instante del origen: un ruido estable por medio y por
    /// tramo, para que el dibujo acompañe al material al recortar o hacer slip hasta que haya picos reales.
    /// </summary>
    public static float PlaceholderPeak(string mediaId, Time sourceTime)
    {
        ArgumentNullException.ThrowIfNull(mediaId);
        long bucket = sourceTime.Ticks / WaveformBucket.Ticks;
        ulong h = 1469598103934665603UL;
        foreach (char c in mediaId)
        {
            h = (h ^ c) * 1099511628211UL;
        }

        h ^= (ulong)bucket * 0x9E3779B97F4A7C15UL;
        h ^= h >> 31;
        h *= 0xBF58476D1CE4E5B9UL;
        h ^= h >> 29;
        double noise = (h & 0xFFFF) / 65535.0;
        double envelope = 0.55 + (0.35 * Math.Sin(bucket * 0.11)) + (0.1 * Math.Sin(bucket * 0.023));
        return (float)Math.Clamp((0.25 + (0.75 * noise)) * envelope, 0.05, 1);
    }
}
