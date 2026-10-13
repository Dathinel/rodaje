using Rodaje.Core.Editing;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Model;

/// <summary>
/// La v1.0.0 guardaba cualquier tasa de secuencia positiva y clips que acababan en cualquier instante que cupiera en un
/// <c>long</c>; hoy el modelo los limita (<see cref="Sequence.IsSupportedFrameRate"/>, <see cref="Sequence.MaxTime"/>)
/// porque editar con valores extremos desbordaba. Un proyecto de entonces se abre igual: al leerlo, la tasa se acota al
/// rango y lo que pasa del final de la línea de tiempo se recorta (o se quita, si empieza después). Se cuenta igual que
/// <see cref="Rodaje.Core.Effects.ParameterLimits.Normalize(Project)"/>. Lo que entra por edición lo sigue rechazando el validador.
/// </summary>
public static class TimelineLimits
{
    /// <summary>Acota en el sitio; devuelve cuántos valores cambió (una tasa, un clip recortado o quitado).</summary>
    public static int Normalize(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        int changed = 0;
        foreach (Sequence sequence in project.Sequences)
        {
            Rational rate = sequence.FrameRate;
            if (rate.Num > 0 && rate.Den > 0 && !Sequence.IsSupportedFrameRate(rate))
            {
                sequence.FrameRate = rate.Num < rate.Den ? Sequence.MinFrameRate : new Rational(Sequence.MaxFrameRate, 1);
                changed++;
            }

            int trimmed = 0;
            foreach (Track track in sequence.AllTracks)
            {
                trimmed += TrimPastEnd(track);
            }

            changed += trimmed;
            if (trimmed > 0)
            {
                TransitionOps.Reconcile(sequence);
            }
        }

        return changed;
    }

    private static int TrimPastEnd(Track track)
    {
        long max = Sequence.MaxTime.Ticks;
        int changed = 0;
        for (int i = track.Clips.Count - 1; i >= 0; i--)
        {
            Clip clip = track.Clips[i];
            // En 128 bits: lo que no cabe en un long ya era inválido en la v1.0.0 y lo sigue informando el validador.
            Int128 length = clip.Speed is null ? (Int128)clip.SourceOut.Ticks - clip.SourceIn.Ticks : clip.Duration.Ticks;
            Int128 end = clip.Start.Ticks + length;
            if (length <= 0 || end <= max || end > long.MaxValue || clip.Start.Ticks < 0)
            {
                continue;
            }

            changed++;
            if (clip.Speed is null && clip.Start.Ticks < max)
            {
                clip.SourceOut = clip.SourceIn + (Sequence.MaxTime - clip.Start);
            }
            else
            {
                track.Clips.RemoveAt(i);
            }
        }

        return changed;
    }
}
