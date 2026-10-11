using Rodaje.Core.Model;

namespace Rodaje.Core.Editing;

/// <summary>Mantiene las transiciones pegadas a sus cortes después de cualquier edición.</summary>
public static class TransitionOps
{
    /// <summary>
    /// Para cada transición busca su corte por el clip que entra (si se dividió el que sale, el corte sigue siendo el
    /// mismo) o, si ese ya no está, por el que sale. Si el corte dejó de existir (hay un hueco, se borró un clip) la
    /// transición se quita; si quedan dos en el mismo corte, se queda la primera.
    /// </summary>
    /// <remarks>
    /// Corre dentro de la edición, así que el deshacer la incluye en la misma instantánea.
    /// TODO(fase 5, agente A): acotar la duración al material sobrante y a la de los clips.
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
        var cuts = new HashSet<string>(StringComparer.Ordinal);
        track.Transitions.RemoveAll(transition =>
        {
            if (!TryFindCut(track, transition, out Clip? from, out Clip? to) || !cuts.Add(from.Id))
            {
                return true;
            }

            transition.From = from.Id;
            transition.To = to.Id;
            return false;
        });
        track.Transitions.Sort((a, b) => track.Clips.FindIndex(c => c.Id == a.From).CompareTo(track.Clips.FindIndex(c => c.Id == b.From)));
    }

    private static bool TryFindCut(Track track, TransitionInstance transition, out Clip from, out Clip to)
    {
        int index = track.Clips.FindIndex(c => c.Id == transition.To);
        if (index > 0 && track.Clips[index - 1].End == track.Clips[index].Start)
        {
            (from, to) = (track.Clips[index - 1], track.Clips[index]);
            return true;
        }

        index = track.Clips.FindIndex(c => c.Id == transition.From);
        if (index >= 0 && index + 1 < track.Clips.Count && track.Clips[index].End == track.Clips[index + 1].Start)
        {
            (from, to) = (track.Clips[index], track.Clips[index + 1]);
            return true;
        }

        (from, to) = (null!, null!);
        return false;
    }
}
