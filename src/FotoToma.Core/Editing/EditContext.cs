using FotoToma.Core.Model;
using FotoToma.Core.Timing;

namespace FotoToma.Core.Editing;

/// <summary>
/// Lo que necesita una operación de edición: el proyecto (para consultar los medios), la secuencia que
/// se modifica y la fuente de identificadores para los objetos nuevos.
/// </summary>
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

    /// <summary>Lanza <see cref="EditException"/> si la pista está bloqueada.</summary>
    public static void EnsureUnlocked(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (track.Locked)
        {
            throw new EditException($"La pista «{track.Name}» está bloqueada. Desbloquéala para editarla.");
        }
    }

    /// <summary>
    /// Enlace para la mitad derecha de un clip que se partió. Todas las mitades derechas que venían del
    /// mismo enlace dentro de una misma operación comparten el enlace nuevo, así el video y su audio
    /// cortados siguen unidos.
    /// </summary>
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
    /// Corre (ripple) en <paramref name="delta"/> todos los clips que empiezan en <paramref name="from"/>
    /// o después, en las pistas que indique el alcance. Los clips de <paramref name="exclude"/> no se
    /// mueven. Las pistas bloqueadas se saltan cuando el alcance es <see cref="RippleScope.AllTracks"/>.
    /// </summary>
    public void RippleShift(IEnumerable<Track> editedTracks, Time from, Time delta, RippleScope scope, IReadOnlySet<Clip>? exclude = null)
    {
        ArgumentNullException.ThrowIfNull(editedTracks);
        IEnumerable<Track> tracks = scope == RippleScope.AllTracks
            ? Sequence.AllTracks.Where(t => !t.Locked)
            : editedTracks.Distinct();

        foreach (Track track in tracks)
        {
            ShiftClips(track, from, delta, exclude);
        }
    }

    /// <summary>Corre en <paramref name="delta"/> los clips de una pista que empiezan en <paramref name="from"/> o después.</summary>
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

    /// <summary>
    /// Comprueba la secuencia al terminar una operación y lanza <see cref="EditException"/> con el primer
    /// problema si quedó inválida (por ejemplo un solape). El comando que la envuelve restaura el estado.
    /// </summary>
    public void EnsureValid()
    {
        IReadOnlyList<string> errors = ModelValidator.ValidateSequence(Project, Sequence);
        if (errors.Count > 0)
        {
            throw new EditException(errors[0]);
        }
    }
}
