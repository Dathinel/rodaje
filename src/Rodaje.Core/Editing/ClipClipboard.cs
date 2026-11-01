using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

/// <summary>Un clip copiado y de dónde salió: el tipo de pista y su número en la lista (0 es la de más abajo).</summary>
public sealed record CopiedClip(Clip Clip, TrackKind Kind, int TrackIndex);

/// <summary>
/// Una transición copiada entre dos clips copiados de la misma pista. <see cref="TransitionInstance.From"/> y
/// <see cref="TransitionInstance.To"/> siguen siendo los identificadores de los clips de origen; al pegar se cambian por
/// los de las copias.
/// </summary>
public sealed record CopiedTransition(TransitionInstance Transition, TrackKind Kind, int TrackIndex);

/// <summary>
/// Qué hace falta para que los medios de lo copiado estén en un proyecto: los que ya están con otro identificador se
/// renombran (<see cref="Renamed"/>, del identificador copiado al del proyecto) y los que no están se importan por su
/// archivo (<see cref="ToImport"/>).
/// </summary>
public sealed record MediaResolution(IReadOnlyDictionary<string, string> Renamed, IReadOnlyList<MediaRef> ToImport)
{
    public bool IsComplete => ToImport.Count == 0;
}

/// <summary>
/// Lo que se copió con «Copiar clips» (Ctrl+C): copias independientes del modelo, con sus efectos, su velocidad, sus
/// enlaces y las transiciones entre ellos, para pegarlas en el cursor aunque el proyecto haya cambiado mientras tanto.
/// <see cref="Origin"/> es el inicio del primero: al pegar, ese instante cae en el cursor y los demás guardan su distancia.
/// Guarda también los medios que usan (<see cref="Media"/>), para traerlos a otro proyecto por su archivo.
/// </summary>
public sealed class ClipClipboard
{
    private ClipClipboard(IReadOnlyList<CopiedClip> items, IReadOnlyList<CopiedTransition> transitions, IReadOnlyList<MediaRef> media, Time origin)
    {
        Items = items;
        Transitions = transitions;
        Media = media;
        Origin = origin;
    }

    public IReadOnlyList<CopiedClip> Items { get; }

    /// <summary>Las transiciones cuyos dos clips están en <see cref="Items"/>, en la misma pista.</summary>
    public IReadOnlyList<CopiedTransition> Transitions { get; }

    /// <summary>Copias de los medios que usan los clips copiados, tal como estaban en el proyecto de origen.</summary>
    public IReadOnlyList<MediaRef> Media { get; }

    public Time Origin { get; }

    /// <summary>Fin del último clip copiado, medido desde <see cref="Origin"/>.</summary>
    public Time Length => Items.Select(i => i.Clip.End).DefaultIfEmpty(Origin).Max() - Origin;

    /// <summary>
    /// Copia los clips pedidos con sus enlazados y sus grupos (con <paramref name="alone"/>, solo ellos), las
    /// transiciones entre los copiados y, si se da el proyecto, sus medios. Un clip compuesto no se copia: dos clips no
    /// pueden compartir la misma secuencia interior. <c>null</c> si no hay nada que copiar.
    /// </summary>
    public static ClipClipboard? Capture(Sequence sequence, IEnumerable<string> clipIds, bool alone = false, Project? project = null)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(clipIds);
        IReadOnlyList<(Track Track, Clip Clip)> found = alone
            ? [.. clipIds.Distinct(StringComparer.Ordinal).Select(sequence.FindClip).OfType<(Track, Clip)>()]
            : TimelineEdits.ExpandWithGroups(sequence, clipIds);
        if (found.Count == 0)
        {
            return null;
        }

        if (found.Any(f => f.Clip.IsCompound))
        {
            throw new EditException("Un clip compuesto no se copia ni se duplica: entra en él y copia lo de dentro.");
        }

        var items = new List<CopiedClip>(found.Count);
        foreach ((Track track, Clip clip) in found.OrderBy(f => f.Clip.Start.Ticks))
        {
            Clip copy = clip.Clone();
            if (alone)
            {
                // Sin su pareja ni su grupo, la copia va suelta.
                copy.LinkId = null;
                copy.GroupId = null;
            }

            items.Add(new CopiedClip(copy, track.Kind, IndexOf(sequence, track)));
        }

        var copiedIds = new HashSet<string>(found.Select(f => f.Clip.Id), StringComparer.Ordinal);
        var transitions = new List<CopiedTransition>();
        foreach (Track track in found.Select(f => f.Track).Distinct())
        {
            foreach (TransitionInstance transition in track.Transitions)
            {
                if (copiedIds.Contains(transition.From) && copiedIds.Contains(transition.To))
                {
                    transitions.Add(new CopiedTransition(transition.Clone(), track.Kind, IndexOf(sequence, track)));
                }
            }
        }

        List<MediaRef> media = project is null
            ? []
            : [.. items.Select(i => i.Clip.MediaId).Distinct(StringComparer.Ordinal).Select(project.FindMedia).OfType<MediaRef>().Select(m => m.Clone())];
        return new ClipClipboard(items, transitions, media, items[0].Clip.Start);
    }

    /// <summary>
    /// Qué les falta a los medios de lo copiado en <paramref name="project"/>. Un medio está si el proyecto tiene uno con
    /// su identificador y el mismo archivo; si tiene el archivo con otro identificador, se usa ese; y si no lo tiene, hay
    /// que importarlo. Un medio del que no se guardó referencia (lo copiado no traía el proyecto) cuenta como que falta
    /// si no está su identificador.
    /// </summary>
    public MediaResolution ResolveMedia(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        var toImport = new List<MediaRef>();
        foreach (string mediaId in Items.Where(i => i.Clip.Generator is null && !i.Clip.IsCompound).Select(i => i.Clip.MediaId).Distinct(StringComparer.Ordinal))
        {
            MediaRef? copied = Media.FirstOrDefault(m => m.Id == mediaId);
            MediaRef? byId = project.FindMedia(mediaId);
            if (copied is null)
            {
                if (byId is null)
                {
                    toImport.Add(new MediaRef { Id = mediaId });
                }

                continue;
            }

            if (byId is not null && SamePath(byId, copied))
            {
                continue;
            }

            if (project.Media.Find(m => SamePath(m, copied)) is { } byPath)
            {
                renamed[mediaId] = byPath.Id;
            }
            else
            {
                toImport.Add(copied.Clone());
            }
        }

        return new MediaResolution(renamed, toImport);
    }

    /// <summary>Lo mismo copiado con los medios renombrados según <paramref name="renamed"/> (del identificador de origen al nuevo).</summary>
    public ClipClipboard WithMediaRenamed(IReadOnlyDictionary<string, string> renamed)
    {
        ArgumentNullException.ThrowIfNull(renamed);
        if (renamed.Count == 0)
        {
            return this;
        }

        List<CopiedClip> items = [.. Items.Select(i =>
        {
            if (!renamed.TryGetValue(i.Clip.MediaId, out string? newId))
            {
                return i;
            }

            Clip copy = i.Clip.Clone();
            copy.MediaId = newId;
            return i with { Clip = copy };
        })];
        List<MediaRef> media = [.. Media.Select(m =>
        {
            if (!renamed.TryGetValue(m.Id, out string? newId))
            {
                return m;
            }

            MediaRef copy = m.Clone();
            copy.Id = newId;
            return copy;
        })];
        return new ClipClipboard(items, Transitions, media, Origin);
    }

    private static int IndexOf(Sequence sequence, Track track) =>
        track.Kind == TrackKind.Video ? sequence.VideoTracks.IndexOf(track) : sequence.AudioTracks.IndexOf(track);

    private static bool SamePath(MediaRef a, MediaRef b) =>
        a.Path.Length > 0 && string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
}
