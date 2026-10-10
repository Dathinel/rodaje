using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Editing;

/// <summary>Proyecto, secuencia editada y fuente de identificadores de una operación de edición.</summary>
public sealed class EditContext
{
    private readonly Dictionary<string, string> _derivedLinks = new(StringComparer.Ordinal);

    public EditContext(Project project, Sequence sequence, IIdGenerator ids)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(ids);
        Project = project;
        Sequence = sequence;
        Ids = ids;
    }

    public Project Project { get; }

    public Sequence Sequence { get; }

    public IIdGenerator Ids { get; }

    public MediaRef MediaOf(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return Project.FindMedia(clip.MediaId)
            ?? throw new EditException($"El clip «{clip.Id}» usa un medio que ya no está en el proyecto.");
    }

    public static void EnsureUnlocked(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (track.Locked)
        {
            throw new EditException($"La pista «{track.Name}» está bloqueada. Desbloquéala para editarla.");
        }
    }

    /// <summary>Enlace de la mitad derecha de un clip partido, compartido por las mitades del mismo enlace en esta operación.</summary>
    public string? DerivedLinkId(string? originalLinkId)
    {
        if (originalLinkId is null)
        {
            return null;
        }

        if (!_derivedLinks.TryGetValue(originalLinkId, out string? derived))
        {
            derived = Ids.NewId("link");
            _derivedLinks[originalLinkId] = derived;
        }

        return derived;
    }

    /// <summary>
    /// Corre en <paramref name="delta"/> los clips desde <paramref name="from"/>; con <see cref="RippleScope.AllTracks"/>
    /// salta las pistas bloqueadas, y con <see cref="RippleScope.AllTracksAndMarkers"/> corre también marcadores y regiones.
    /// Con <see cref="RippleScope.Track"/> corre además las pistas de los enlazados de lo que se corre.
    /// </summary>
    /// <returns>Las pistas corridas.</returns>
    public IReadOnlyList<Track> RippleShift(IEnumerable<Track> editedTracks, Time from, Time delta, RippleScope scope, IReadOnlySet<Clip>? exclude = null)
    {
        ArgumentNullException.ThrowIfNull(editedTracks);
        List<Track> tracks;
        if (scope.IncludesAllTracks())
        {
            tracks = [.. Sequence.AllTracks.Where(t => !t.Locked)];
        }
        else
        {
            List<Track> edited = [.. editedTracks.Distinct()];
            tracks = LinkedRippleTracks(edited, from, exclude);
            foreach (Track added in tracks.Skip(edited.Count))
            {
                EnsureCanClose(added, from, delta, exclude);
            }
        }

        foreach (Track track in tracks)
        {
            ShiftClips(track, from, delta, exclude);
        }

        if (scope == RippleScope.AllTracksAndMarkers)
        {
            ShiftMarkers(Sequence, from, delta);
        }

        return tracks;
    }

    /// <summary>
    /// <paramref name="edited"/> seguidas de las pistas de los enlazados de los clips que se corren desde
    /// <paramref name="from"/>, y así hasta no sumar más, para que ningún grupo enlazado se desincronice.
    /// </summary>
    internal List<Track> LinkedRippleTracks(IReadOnlyList<Track> edited, Time from, IReadOnlySet<Clip>? exclude)
    {
        List<Track> tracks = [.. edited];
        Dictionary<string, List<Track>>? linkTracks = null;
        for (int i = 0; i < tracks.Count; i++)
        {
            foreach (Clip clip in tracks[i].Clips)
            {
                if (clip.LinkId is null || clip.Start < from || (exclude is not null && exclude.Contains(clip)))
                {
                    continue;
                }

                linkTracks ??= IndexLinkTracks();
                foreach (Track partnerTrack in linkTracks[clip.LinkId])
                {
                    if (!tracks.Contains(partnerTrack))
                    {
                        tracks.Add(partnerTrack);
                    }
                }
            }
        }

        return tracks;
    }

    /// <summary>
    /// Falla si <paramref name="track"/> está bloqueada o si, al cerrar el tramo, un clip que no se corre quedaría
    /// debajo de los que sí.
    /// </summary>
    internal static void EnsureCanClose(Track track, Time from, Time delta, IReadOnlySet<Clip>? exclude)
    {
        EnsureUnlocked(track);
        if (delta >= Time.Zero)
        {
            return;
        }

        Time closedStart = from + delta;
        Clip? straddling = track.Clips.Find(c =>
            c.Start < from && c.End > closedStart && (exclude is null || !exclude.Contains(c)));
        if (straddling is not null)
        {
            throw new EditException(
                $"No se puede correr la pista «{track.Name}», que tiene clips enlazados con lo que se corre, porque el clip «{straddling.Id}» atraviesa el tramo que se cierra.");
        }
    }

    private Dictionary<string, List<Track>> IndexLinkTracks()
    {
        var index = new Dictionary<string, List<Track>>(StringComparer.Ordinal);
        foreach (Track track in Sequence.AllTracks)
        {
            foreach (Clip clip in track.Clips)
            {
                if (clip.LinkId is null)
                {
                    continue;
                }

                if (!index.TryGetValue(clip.LinkId, out List<Track>? list))
                {
                    list = [];
                    index[clip.LinkId] = list;
                }

                if (!list.Contains(track))
                {
                    list.Add(track);
                }
            }
        }

        return index;
    }

    /// <summary>
    /// Corre en <paramref name="delta"/> los marcadores y los bordes de región desde <paramref name="from"/>. Si el
    /// tramo se cierra (<paramref name="delta"/> negativo), lo que caía dentro de lo quitado queda en su inicio, y una
    /// región que queda vacía se borra.
    /// </summary>
    public static void ShiftMarkers(Sequence sequence, Time from, Time delta)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        if (delta == Time.Zero)
        {
            return;
        }

        foreach (Marker marker in sequence.Markers)
        {
            marker.Time = ShiftPoint(marker.Time, from, delta);
        }

        foreach (Region region in sequence.Regions)
        {
            region.Start = ShiftPoint(region.Start, from, delta);
            region.End = ShiftPoint(region.End, from, delta);
        }

        sequence.Regions.RemoveAll(r => r.End <= r.Start);
        SortMarkers(sequence);
    }

    /// <summary>Ordena marcadores por tiempo y regiones por inicio, sin cambiar el orden de los empates.</summary>
    internal static void SortMarkers(Sequence sequence)
    {
        List<Marker> markers = [.. sequence.Markers.OrderBy(m => m.Time)];
        sequence.Markers.Clear();
        sequence.Markers.AddRange(markers);
        List<Region> regions = [.. sequence.Regions.OrderBy(r => r.Start)];
        sequence.Regions.Clear();
        sequence.Regions.AddRange(regions);
    }

    private static Time ShiftPoint(Time point, Time from, Time delta)
    {
        if (point >= from)
        {
            return point + delta;
        }

        Time closedStart = from + delta;
        return delta < Time.Zero && point > closedStart ? closedStart : point;
    }

    public static void ShiftClips(Track track, Time from, Time delta, IReadOnlySet<Clip>? exclude = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (delta == Time.Zero)
        {
            return;
        }

        foreach (Clip clip in track.Clips)
        {
            if (clip.Start >= from && (exclude is null || !exclude.Contains(clip)))
            {
                clip.Start += delta;
            }
        }

        track.SortClips();
    }

    /// <summary>Lanza <see cref="EditException"/> con el primer problema si la secuencia quedó inválida.</summary>
    public void EnsureValid()
    {
        IReadOnlyList<string> errors = ModelValidator.ValidateSequence(Project, Sequence);
        if (errors.Count > 0)
        {
            throw new EditException(errors[0]);
        }
    }
}
