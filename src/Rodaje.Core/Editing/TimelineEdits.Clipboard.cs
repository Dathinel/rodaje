using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

/// <summary>Duplicar la selección y pegar lo copiado en el cursor.</summary>
public static partial class TimelineEdits
{
    /// <summary>
    /// Pone copias de los clips a continuación de la selección (del último fin), en sus mismas pistas, sin pisar nada:
    /// si no hay sitio, no se duplica ninguno. Con <paramref name="alone"/>, solo los pedidos, sin enlazados ni grupos.
    /// Las transiciones entre los clips duplicados se duplican con ellos.
    /// </summary>
    /// <returns>Los identificadores de los clips nuevos, en orden de tiempo.</returns>
    public static IReadOnlyList<string> DuplicateClips(EditContext ctx, IEnumerable<string> clipIds, bool alone = false)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ClipClipboard copied = ClipClipboard.Capture(ctx.Sequence, clipIds, alone, ctx.Project)
            ?? throw new EditException("No hay ningún clip seleccionado.");
        return Paste(ctx, copied, copied.Origin + copied.Length, PlacementMode.Strict);
    }

    /// <summary>
    /// Pega lo copiado con el primero en <paramref name="at"/> y los demás a su distancia, cada uno en la pista con el
    /// mismo número que la suya (o la última de su tipo si ya no hay tantas), y las transiciones que había entre ellos.
    /// En <see cref="PlacementMode.Overwrite"/> tapa lo que haya en ese tramo; en <see cref="PlacementMode.Insert"/> abre
    /// un espacio del largo de lo copiado en las pistas donde cae (con los enlazados de lo que se corre, como al mover
    /// insertando) y corre lo que sigue; en <see cref="PlacementMode.Strict"/> exige que esté libre.
    /// </summary>
    /// <returns>Los identificadores de los clips nuevos, en el orden de lo copiado.</returns>
    public static IReadOnlyList<string> Paste(EditContext ctx, ClipClipboard copied, Time at, PlacementMode mode = PlacementMode.Overwrite)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(copied);
        if (copied.Items.Count == 0)
        {
            throw new EditException("No hay nada copiado.");
        }

        if (at < Time.Zero)
        {
            throw new EditException("El clip no puede empezar antes del inicio de la línea de tiempo.");
        }

        var groups = new Dictionary<string, string>(StringComparer.Ordinal);
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        var placed = new List<(Track Track, Clip Clip)>(copied.Items.Count);
        foreach (CopiedClip item in copied.Items)
        {
            Track track = TargetTrack(ctx.Sequence, item.Kind, item.TrackIndex);
            EditContext.EnsureUnlocked(track);
            Clip clip = item.Clip.Clone();
            if (clip.Generator is null && ctx.Project.FindMedia(clip.MediaId) is null)
            {
                throw new EditException("Uno de los clips copiados usa un medio que ya no está en el proyecto.");
            }

            if (clip.IsCompound)
            {
                throw new EditException("Un clip compuesto no se pega: entra en él y copia lo de dentro.");
            }

            clip.Id = ctx.Ids.NewId("clip");
            renamed[item.Clip.Id] = clip.Id;
            clip.LinkId = ctx.DerivedLinkId(clip.LinkId);
            clip.GroupId = Renamed(groups, clip.GroupId, "group", ctx);
            MaskEdits.RenewIds(ctx, clip.Effects, "fx");
            clip.Start = at + (item.Clip.Start - copied.Origin);
            placed.Add((track, clip));
        }

        // Dos copiados de pistas distintas pueden caer en la misma (TargetTrack recorta el índice): en Overwrite el
        // segundo taparía al primero recién pegado sin aviso.
        for (int i = 0; i < placed.Count; i++)
        {
            for (int j = i + 1; j < placed.Count; j++)
            {
                if (placed[i].Track == placed[j].Track && placed[i].Clip.Start < placed[j].Clip.End && placed[j].Clip.Start < placed[i].Clip.End)
                {
                    throw new EditException("Lo copiado no cabe en las pistas de este proyecto: dos clips coinciden en la misma pista.");
                }
            }
        }

        switch (mode)
        {
            case PlacementMode.Strict:
                foreach ((Track track, Clip clip) in placed)
                {
                    TrackOps.EnsureFree(track, clip.Start, clip.End);
                }

                break;
            case PlacementMode.Insert:
                // Un solo espacio, del cursor al final de lo copiado, en las pistas donde cae algo: lo que sigue se corre
                // junto y nada se desincroniza, aunque entre los clips copiados hubiera huecos.
                TrackOps.InsertSpaceLinked(ctx, [.. placed.Select(p => p.Track).Distinct()], at, copied.Length);
                break;
            default:
                break;
        }

        foreach ((Track track, Clip clip) in placed)
        {
            if (mode == PlacementMode.Overwrite)
            {
                TrackOps.ClearRange(ctx, track, clip.Start, clip.End);
            }

            TrackOps.AddClipSorted(track, clip);
        }

        PasteTransitions(ctx, copied, placed, renamed);
        ctx.EnsureValid();
        return [.. placed.Select(p => p.Clip.Id)];
    }

    /// <summary>
    /// Pone en las copias las transiciones que había entre los clips copiados, cuando las dos copias cayeron en la misma
    /// pista; <see cref="EditContext.EnsureValid"/> las concilia después con sus cortes (las acota o las quita si el corte
    /// no existe).
    /// </summary>
    private static void PasteTransitions(EditContext ctx, ClipClipboard copied, List<(Track Track, Clip Clip)> placed, Dictionary<string, string> renamed)
    {
        foreach (CopiedTransition item in copied.Transitions)
        {
            if (!renamed.TryGetValue(item.Transition.From, out string? from) || !renamed.TryGetValue(item.Transition.To, out string? to))
            {
                continue;
            }

            Track fromTrack = placed.First(p => p.Clip.Id == from).Track;
            if (placed.First(p => p.Clip.Id == to).Track != fromTrack || fromTrack.TransitionFrom(from) is not null)
            {
                continue;
            }

            TransitionInstance transition = item.Transition.Clone();
            transition.Id = ctx.Ids.NewId("transition");
            transition.From = from;
            transition.To = to;
            fromTrack.Transitions.Add(transition);
        }
    }

    private static Track TargetTrack(Sequence sequence, TrackKind kind, int index)
    {
        List<Track> tracks = kind == TrackKind.Video ? sequence.VideoTracks : sequence.AudioTracks;
        if (tracks.Count == 0)
        {
            throw new EditException($"No hay ninguna pista de {TrackOps.KindName(kind)} donde pegar.");
        }

        return tracks[Math.Clamp(index, 0, tracks.Count - 1)];
    }

    private static string? Renamed(Dictionary<string, string> map, string? id, string prefix, EditContext ctx)
    {
        if (id is null)
        {
            return null;
        }

        if (!map.TryGetValue(id, out string? renamed))
        {
            renamed = ctx.Ids.NewId(prefix);
            map[id] = renamed;
        }

        return renamed;
    }
}
