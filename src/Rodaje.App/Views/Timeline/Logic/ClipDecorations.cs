using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Views.Timeline;

/// <summary>Lo que se dibuja sobre los clips además del bloque: keyframes, fundidos, envolvente y forma de onda.</summary>
public static class ClipDecorations
{
    /// <summary>Efecto de volumen que dibuja la herramienta de envolvente, con su parámetro de ganancia (1 = sin cambio).</summary>
    public const string VolumeEffect = "rodaje.volume";
    public const string GainParameter = "gain";

    /// <summary>Ganancia máxima que cabe en la altura del clip (unos +6 dB).</summary>
    public const double MaxGain = 2;

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
    public static double Veil(Clip clip, Track track)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(track);
        if (!clip.Enabled || track.Hidden || track.Muted)
        {
            return MutedVeil;
        }

        return track.Locked ? LockedVeil : 0;
    }

    /// <summary>Los instantes de la línea de tiempo con keyframes en algún parámetro del clip, sin repetir y en orden.</summary>
    public static List<Time> KeyframeTimes(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        var times = new SortedSet<long>();
        foreach (EffectInstance effect in clip.Effects)
        {
            foreach (Parameter parameter in effect.Parameters.Values)
            {
                foreach (Keyframe keyframe in parameter.Keyframes)
                {
                    if (keyframe.Time >= clip.SourceIn && keyframe.Time <= clip.SourceOut)
                    {
                        times.Add((clip.Start + (keyframe.Time - clip.SourceIn)).Ticks);
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

    /// <summary>La ganancia del clip en un instante de la línea de tiempo; sin efecto de volumen, 1.</summary>
    public static double GainAt(Clip clip, Time timelineTime)
    {
        ArgumentNullException.ThrowIfNull(clip);
        EffectInstance? volume = clip.Effects.Find(e => e.Type == VolumeEffect && e.Enabled);
        if (volume is null || !volume.Parameters.TryGetValue(GainParameter, out Parameter? gain))
        {
            return 1;
        }

        return Math.Clamp(gain.Evaluate(clip.ToSourceTime(timelineTime)), 0, MaxGain);
    }

    /// <summary>Si la ganancia del clip cambia en el tiempo (la envolvente necesita más de dos puntos).</summary>
    public static bool HasAnimatedGain(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return clip.Effects.Exists(e => e.Type == VolumeEffect && e.Enabled
            && e.Parameters.TryGetValue(GainParameter, out Parameter? p) && p.Keyframes.Count > 0);
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
