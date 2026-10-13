using Rodaje.Core.Editing;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Model;

/// <summary>
/// La v1.0.0 guardaba cualquier tasa de secuencia positiva y clips que acababan en cualquier instante que cupiera en un
/// <c>long</c>; hoy el modelo los limita (<see cref="Sequence.IsSupportedFrameRate"/>, <see cref="Sequence.MaxTime"/>)
/// porque editar con valores extremos desbordaba. Un proyecto de entonces se abre igual: al leerlo, la tasa se acota al
/// rango y el clip que pasa del final de la línea de tiempo se recorta hasta él; se quita si empieza después del final o
/// si tiene velocidad (recortar uno con velocidad pediría recalcular su mapeo; provisional). Se cuenta igual que
/// <see cref="Rodaje.Core.Effects.ParameterLimits.Normalize(Project)"/>. Lo que entra por edición lo sigue rechazando el validador.
/// </summary>
public static class TimelineLimits
{
    /// <summary>Acota en el sitio; devuelve cuántos valores cambió (una tasa, un clip recortado o quitado).</summary>
    public static int Normalize(Project project)
    {
        (int rates, int trimmed, List<Clip> removed) = NormalizeByKind(project);
        return rates + trimmed + removed.Count;
    }

    /// <summary>
    /// Igual que <see cref="Normalize(Project)"/>, pero dice cuántas tasas acotó, cuántos clips recortó y cuáles quitó, para
    /// <see cref="ClampSummary"/>.
    /// </summary>
    public static (int FrameRates, int TrimmedClips, List<Clip> RemovedClips) NormalizeByKind(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        int rates = 0;
        int trimmedClips = 0;
        var removed = new List<Clip>();
        foreach (Sequence sequence in project.Sequences)
        {
            Rational rate = sequence.FrameRate;
            if (rate.Num > 0 && rate.Den > 0 && !Sequence.IsSupportedFrameRate(rate))
            {
                sequence.FrameRate = rate.Num < rate.Den ? Sequence.MinFrameRate : new Rational(Sequence.MaxFrameRate, 1);
                rates++;
            }

            int trimmed = 0;
            int removedBefore = removed.Count;
            foreach (Track track in sequence.AllTracks)
            {
                trimmed += TrimPastEnd(track, removed);
            }

            trimmedClips += trimmed;
            if (trimmed > 0 || removed.Count > removedBefore)
            {
                TransitionOps.Reconcile(sequence);
            }
        }

        return (rates, trimmedClips, removed);
    }

    /// <summary>Recorta o quita lo que pasa del final; devuelve cuántos recortó y deja en <paramref name="removed"/> los quitados.</summary>
    private static int TrimPastEnd(Track track, List<Clip> removed)
    {
        long max = Sequence.MaxTime.Ticks;
        int trimmed = 0;
        int firstRemoved = removed.Count;
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

            if (clip.Speed is null && clip.Start.Ticks < max)
            {
                clip.SourceOut = clip.SourceIn + (Sequence.MaxTime - clip.Start);
                trimmed++;
            }
            else
            {
                track.Clips.RemoveAt(i);
                removed.Add(clip);
            }
        }

        // Se recorre de atrás hacia adelante para quitar; en el aviso, en el orden de la pista.
        removed.Reverse(firstRemoved, removed.Count - firstRemoved);
        return trimmed;
    }
}
