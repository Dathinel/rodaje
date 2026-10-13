using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

/// <summary>Mantiene las transiciones pegadas a sus cortes después de cualquier edición.</summary>
public static class TransitionOps
{
    /// <summary>
    /// Lo más que puede durar una transición entre <paramref name="from"/> y <paramref name="to"/>: el más corto de los dos.
    /// </summary>
    /// <remarks>
    /// Con ese tope, la mitad de cada transición cabe en su clip y las dos de un mismo clip no se pisan:
    /// ⌈a/2⌉ + ⌊b/2⌋ ≤ ⌈c/2⌉ + ⌊c/2⌋ = c si a, b ≤ c. El material de origen no limita: lo que falte se congela.
    /// </remarks>
    public static Time MaxDuration(Clip from, Clip to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        return Time.Min(from.Duration, to.Duration);
    }

    /// <summary>
    /// Lo más que puede durar <paramref name="transition"/> con la alineación <paramref name="alignment"/> (la suya si es
    /// nula) sin pisar las otras transiciones de sus dos clips: la parte de antes del corte cabe en lo que deja del clip que
    /// sale la transición que entra en él, y la de después en lo que deja del que entra la que sale de él. Centrada no
    /// pasa además del clip más corto, como siempre.
    /// </summary>
    public static Time MaxDuration(Track track, TransitionInstance transition, TransitionAlignment? alignment = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(transition);
        Clip? from = track.Clips.Find(c => c.Id == transition.From);
        Clip? to = track.Clips.Find(c => c.Id == transition.To);
        if (from is null || to is null)
        {
            return transition.Duration;
        }

        Time roomBefore = from.Duration - (track.TransitionTo(from.Id) is { } entering && entering != transition ? entering.After : Time.Zero);
        Time roomAfter = to.Duration - (track.TransitionFrom(to.Id) is { } leaving && leaving != transition ? leaving.Before : Time.Zero);
        return Limit(alignment ?? transition.Alignment, from, to, roomBefore, roomAfter);
    }

    /// <summary>El tope con lo que queda libre antes y después del corte.</summary>
    private static Time Limit(TransitionAlignment alignment, Clip from, Clip to, Time roomBefore, Time roomAfter)
    {
        roomBefore = Time.Max(Time.Zero, roomBefore);
        roomAfter = Time.Max(Time.Zero, roomAfter);
        return alignment switch
        {
            TransitionAlignment.Before => roomBefore,
            TransitionAlignment.After => roomAfter,

            // ⌊d/2⌋ ≤ antes y ⌈d/2⌉ ≤ después.
            _ => Time.Min(MaxDuration(from, to), Time.Min(Time.FromTicks((roomBefore.Ticks * 2) + 1), Time.FromTicks(roomAfter.Ticks * 2))),
        };
    }

    /// <summary>
    /// Para cada transición busca su corte y la acota a <see cref="MaxDuration(Clip, Clip)"/>, así se acorta si un clip se acortó (al
    /// recortarlo o al acelerarlo). Si el corte dejó de existir (se abrió un hueco, se borró o se movió uno de los clips)
    /// la transición se quita; si quedan dos en el mismo corte, se queda la primera.
    /// </summary>
    /// <remarks>
    /// Al dividir el clip que sale, el corte lo forma su mitad derecha, que tiene otro identificador: se reconoce porque
    /// sigue al de origen con el mismo medio, la misma velocidad y sin salto en el material. Corre dentro de la edición,
    /// así que el deshacer la incluye en la misma instantánea.
    /// </remarks>
    public static void Reconcile(Sequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        foreach (Track track in sequence.AllTracks)
        {
            if (track.Transitions.Count > 0)
            {
                ReconcileTrack(track);
            }
        }
    }

    private static void ReconcileTrack(Track track)
    {
        var index = new Dictionary<string, int>(track.Clips.Count, StringComparer.Ordinal);
        for (int i = 0; i < track.Clips.Count; i++)
        {
            index[track.Clips[i].Id] = i;
        }

        var cuts = new HashSet<int>();
        track.Transitions.RemoveAll(transition =>
        {
            int cut = FindCut(track, index, transition);
            if (cut < 0 || !cuts.Add(cut))
            {
                return true;
            }

            transition.From = track.Clips[cut].Id;
            transition.To = track.Clips[cut + 1].Id;
            return false;
        });
        track.Transitions.Sort((a, b) => index[a.From].CompareTo(index[b.From]));

        // De izquierda a derecha: cada una cabe en lo que dejó la anterior en su clip y le guarda a la siguiente lo que usa,
        // hasta la mitad del clip, así ninguna de las dos se queda en cero.
        TransitionInstance? previous = null;
        for (int i = 0; i < track.Transitions.Count; i++)
        {
            TransitionInstance transition = track.Transitions[i];
            Clip from = track.Clips[index[transition.From]];
            Clip to = track.Clips[index[transition.To]];
            Time roomBefore = from.Duration - (previous is not null && previous.To == from.Id ? previous.After : Time.Zero);
            TransitionInstance? next = i + 1 < track.Transitions.Count && track.Transitions[i + 1].From == to.Id ? track.Transitions[i + 1] : null;
            Time roomAfter = to.Duration - Time.Min(next?.Before ?? Time.Zero, Time.FromTicks(to.Duration.Ticks / 2));
            Time max = Limit(transition.Alignment, from, to, roomBefore, roomAfter);
            if (transition.Duration > max)
            {
                transition.Duration = max;
            }

            previous = transition;
        }

        track.Transitions.RemoveAll(t => t.Duration <= Time.Zero);
    }

    /// <summary>Índice del clip que sale en el corte de la transición, o −1 si el corte ya no existe.</summary>
    private static int FindCut(Track track, Dictionary<string, int> index, TransitionInstance transition)
    {
        if (!index.TryGetValue(transition.To, out int to) || to == 0 || !Touch(track.Clips[to - 1], track.Clips[to]))
        {
            return -1;
        }

        Clip previous = track.Clips[to - 1];
        if (previous.Id == transition.From)
        {
            return to - 1;
        }

        return index.TryGetValue(transition.From, out int from) && from == to - 2 && Continues(track.Clips[from], previous)
            ? to - 1
            : -1;
    }

    private static bool Touch(Clip left, Clip right) => left.End == right.Start;

    /// <summary>
    /// <paramref name="right"/> es la mitad derecha de <paramref name="left"/> tras un corte: mismo medio, misma
    /// velocidad, desplazamientos contiguos y, juntos, el mismo mapeo que un solo clip partido en el corte. Dos generadores
    /// continúan si tienen el mismo contenido (salvo los bordes) y el origen sigue sin salto.
    /// </summary>
    private static bool Continues(Clip left, Clip right)
    {
        if (!Touch(left, right) || left.MediaId != right.MediaId || left.SequenceId != right.SequenceId)
        {
            return false;
        }

        if (left.Generator is not null || right.Generator is not null)
        {
            return left.Generator is not null && right.Generator is not null && left.Generator.SameContent(right.Generator)
                && left.Speed is null && right.Speed is null && left.SourceOut == right.SourceIn;
        }

        if (left.Speed is null || right.Speed is null)
        {
            return left.Speed is null && right.Speed is null && left.SourceOut == right.SourceIn;
        }

        if (!left.Speed.SameTiming(right.Speed) || right.Speed.Offset != left.Speed.Offset + left.Duration)
        {
            return false;
        }

        try
        {
            ClipTimeMap first = left.TimeMap;
            ClipTimeMap whole = ClipTimeMap.Create(first.Start, first.Duration + right.Duration, first.Reverse ? first.SourceOut : first.SourceIn, left.Speed);
            return whole.SplitAt(right.Start).Right.Equals(right.TimeMap);
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        {
            return false;
        }
    }
}
