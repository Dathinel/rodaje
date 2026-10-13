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
        if (clip.IsCompound)
        {
            throw new EditException("Un clip compuesto no admite esta edición: entra en él con doble clic para editar lo de dentro.");
        }

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
        bool all = scope.IncludesAllTracks();
        IEnumerable<Track> seeds = all ? Sequence.AllTracks.Where(t => !t.Locked) : editedTracks.Distinct();
        IReadOnlyList<Track> tracks = RippleEach(seeds.Select(t => (t, from, delta)), exclude ?? new HashSet<Clip>(), skipLocked: all);
        if (scope == RippleScope.AllTracksAndMarkers)
        {
            ShiftMarkers(Sequence, from, delta);
        }

        return tracks;
    }

    /// <summary>
    /// Ripple con un Δ por pista: cada semilla corre su pista desde su punto, y las pistas de los enlazados de lo que se
    /// corre se corren con el Δ y el punto de la pista que los arrastra. Una pista con semilla conserva su Δ; una que
    /// llega por dos caminos se queda con el primero. Así un clip enlazado con otro de distinto largo corre lo suyo.
    /// </summary>
    /// <returns>Las pistas corridas.</returns>
    internal IReadOnlyList<Track> RippleEach(IEnumerable<(Track Track, Time From, Time Delta)> seeds, IReadOnlySet<Clip> exclude, bool skipLocked = false)
    {
        ArgumentNullException.ThrowIfNull(seeds);
        var shifts = new List<(Track Track, Time From, Time Delta)>();
        var index = new Dictionary<Track, int>();
        foreach ((Track track, Time from, Time delta) in seeds)
        {
            if (delta != Time.Zero && index.TryAdd(track, shifts.Count))
            {
                shifts.Add((track, from, delta));
            }
        }

        int seeded = shifts.Count;
        List<Time> seedFrom = [.. shifts.Select(s => s.From)];

        // Un enlazado de lo que se corre se corre con él aunque empiece antes del punto (una pareja más larga, un corte en
        // J): su pista se corre desde el inicio de ese enlazado.
        Dictionary<string, List<(Track Track, Clip Clip)>>? links = null;
        var pending = new Queue<int>(Enumerable.Range(0, shifts.Count));
        while (pending.TryDequeue(out int i))
        {
            (Track track, Time from, Time delta) = shifts[i];
            foreach (Clip clip in track.Clips)
            {
                if (clip.LinkId is null || clip.Start < from || exclude.Contains(clip))
                {
                    continue;
                }

                links ??= IndexLinks();
                foreach ((Track partner, Clip mate) in links[clip.LinkId])
                {
                    if (exclude.Contains(mate) || (skipLocked && partner.Locked))
                    {
                        continue;
                    }

                    Time start = Time.Min(from, mate.Start);
                    if (!index.TryGetValue(partner, out int j))
                    {
                        index[partner] = shifts.Count;
                        pending.Enqueue(shifts.Count);
                        shifts.Add((partner, start, delta));
                    }
                    else if (shifts[j].Delta == delta && start < shifts[j].From)
                    {
                        shifts[j] = (partner, start, delta);
                        pending.Enqueue(j);
                    }
                }
            }
        }

        foreach ((Track track, Time from, Time delta) in shifts)
        {
            // Con todas las pistas, las semillas no se miran (lo hacen quien llama y el control de solapes); una pista que se
            // corre desde antes por un enlazado, sí.
            int k = index[track];
            if (skipLocked && k < seeded && from == seedFrom[k])
            {
                EnsureUnlocked(track);
            }
            else
            {
                EnsureCanClose(track, from, delta, exclude);
            }
        }

        foreach ((Track track, Time from, Time delta) in shifts)
        {
            ShiftClips(track, from, delta, exclude);
        }

        return [.. shifts.Select(s => s.Track)];
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

    private Dictionary<string, List<(Track Track, Clip Clip)>> IndexLinks()
    {
        var index = new Dictionary<string, List<(Track, Clip)>>(StringComparer.Ordinal);
        foreach (Track track in Sequence.AllTracks)
        {
            foreach (Clip clip in track.Clips)
            {
                if (clip.LinkId is null)
                {
                    continue;
                }

                if (!index.TryGetValue(clip.LinkId, out List<(Track, Clip)>? list))
                {
                    index[clip.LinkId] = list = [];
                }

                list.Add((track, clip));
            }
        }

        return index;
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
        TrackKeyframes.Shift(track, from, delta);
    }

    /// <summary>
    /// Concilia las transiciones con los cortes (<see cref="TransitionOps.Reconcile"/>) y lanza <see cref="EditException"/>
    /// con el primer problema si la secuencia quedó inválida.
    /// </summary>
    public void EnsureValid()
    {
        TransitionOps.Reconcile(Sequence);
        IReadOnlyList<string> errors = ModelValidator.ValidateSequence(Project, Sequence);
        if (errors.Count > 0)
        {
            throw new EditException(errors[0]);
        }
    }
}
