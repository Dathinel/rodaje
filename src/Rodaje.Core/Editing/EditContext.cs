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
    /// </summary>
    public void RippleShift(IEnumerable<Track> editedTracks, Time from, Time delta, RippleScope scope, IReadOnlySet<Clip>? exclude = null)
    {
        ArgumentNullException.ThrowIfNull(editedTracks);
        IEnumerable<Track> tracks = scope.IncludesAllTracks()
            ? Sequence.AllTracks.Where(t => !t.Locked)
            : editedTracks.Distinct();

        foreach (Track track in tracks)
        {
            ShiftClips(track, from, delta, exclude);
        }

        if (scope == RippleScope.AllTracksAndMarkers)
        {
            ShiftMarkers(Sequence, from, delta);
        }
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
