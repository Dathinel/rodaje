using Rodaje.Core.Editing;
using Rodaje.Core.Model;

namespace Rodaje.Core.Commands;

/// <summary>Hace deshacible una operación de edición con instantáneas de la secuencia antes y después.</summary>
/// <remarks>
/// Instantáneas en vez de operaciones inversas: si la operación falla a medias se restaura la de antes,
/// y rehacer restaura la de después sin volver a correrla, así <see cref="CreatedIds"/> no cambia.
/// </remarks>
public sealed class SequenceEditCommand : ICommand
{
    private readonly Func<EditContext, IReadOnlyList<string>> _operation;
    private readonly IIdGenerator _ids;
    private SequenceSnapshot? _before;
    private SequenceSnapshot? _after;

    // Solo si la operación agregó o quitó secuencias (un clip compuesto): la lista entera antes y después.
    private List<Sequence>? _sequencesBefore;
    private List<Sequence>? _sequencesAfter;

    /// <param name="description">Texto para el menú de deshacer.</param>
    /// <param name="sequenceId">Secuencia que se edita.</param>
    /// <param name="operation">La edición; devuelve los identificadores de lo que creó.</param>
    /// <param name="ids">Fuente de identificadores; por omisión, GUID.</param>
    /// <param name="mergeKey">Comandos seguidos con la misma clave no nula y la misma secuencia se funden.</param>
    public SequenceEditCommand(
        string description,
        string sequenceId,
        Func<EditContext, IReadOnlyList<string>> operation,
        IIdGenerator? ids = null,
        string? mergeKey = null)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(sequenceId);
        ArgumentNullException.ThrowIfNull(operation);
        Description = description;
        SequenceId = sequenceId;
        _operation = operation;
        _ids = ids ?? GuidIdGenerator.Instance;
        MergeKey = mergeKey;
    }

    public SequenceEditCommand(
        string description,
        string sequenceId,
        Action<EditContext> operation,
        IIdGenerator? ids = null,
        string? mergeKey = null)
        : this(description, sequenceId, Wrap(operation), ids, mergeKey)
    {
    }

    public string Description { get; }

    public string SequenceId { get; }

    public string? MergeKey { get; }

    /// <summary>Identificadores creados por la operación, incluidos los de comandos fundidos.</summary>
    public IReadOnlyList<string> CreatedIds { get; private set; } = [];

    public bool HasRun => _after is not null;

    public void Apply(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Sequence sequence = FindSequence(project);

        if (_after is not null)
        {
            _after.Restore(sequence);
            SetSequences(project, _sequencesAfter);
            return;
        }

        SequenceSnapshot before = SequenceSnapshot.Capture(sequence);
        List<Sequence> sequencesBefore = [.. project.Sequences];
        IReadOnlyList<string> created;
        try
        {
            created = _operation(new EditContext(project, sequence, _ids));
            EnsureLockedTracksUntouched(before, sequence);
        }
        catch (Exception ex)
        {
            before.Restore(sequence);
            SetSequences(project, sequencesBefore);
            if (ex is OverflowException)
            {
                // Un cálculo de tiempos que no cabe en un long (clips cerca del final del rango, un desplazamiento enorme):
                // la edición no se puede hacer, como cualquier otra imposible, en vez de cerrar la app.
                throw new EditException("La edición se saldría del rango de tiempos que admite el proyecto.", ex);
            }

            throw;
        }

        _before = before;
        _after = SequenceSnapshot.Capture(sequence);
        if (!project.Sequences.SequenceEqual(sequencesBefore))
        {
            _sequencesBefore = sequencesBefore;
            _sequencesAfter = [.. project.Sequences];
        }

        CreatedIds = created is null ? [] : [.. created];
    }

    public void Revert(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (_before is null)
        {
            throw new InvalidOperationException("No se puede revertir un comando que todavía no se ha aplicado.");
        }

        _before.Restore(FindSequence(project));
        SetSequences(project, _sequencesBefore);
    }

    private static void SetSequences(Project project, List<Sequence>? sequences)
    {
        if (sequences is not null && !project.Sequences.SequenceEqual(sequences))
        {
            project.Sequences.Clear();
            project.Sequences.AddRange(sequences);
        }
    }

    public bool TryMerge(ICommand following)
    {
        if (following is not SequenceEditCommand next
            || ReferenceEquals(next, this)
            || MergeKey is null
            || next.MergeKey != MergeKey
            || next.SequenceId != SequenceId
            || _after is null
            || next._after is null
            || _sequencesAfter is not null
            || next._sequencesAfter is not null)
        {
            return false;
        }

        _after = next._after;
        CreatedIds = [.. CreatedIds, .. next.CreatedIds];
        return true;
    }

    private Sequence FindSequence(Project project) =>
        project.FindSequence(SequenceId)
        ?? throw new EditException($"La secuencia «{SequenceId}» ya no está en el proyecto.");

    // Red de seguridad: una pista bloqueada antes y después no puede cambiar de clips ni desaparecer.
    // Si la operación la desbloqueó, el resto de cambios de esa llamada vale (desbloquear y renombrar).
    private static void EnsureLockedTracksUntouched(SequenceSnapshot before, Sequence sequence)
    {
        Check(before.VideoTracks, sequence.VideoTracks);
        Check(before.AudioTracks, sequence.AudioTracks);

        static void Check(IReadOnlyList<SequenceSnapshot.TrackEntry> entries, List<Track> current)
        {
            foreach (SequenceSnapshot.TrackEntry entry in entries)
            {
                if (entry.State.Locked
                    && entry.Track.Locked
                    && (!current.Contains(entry.Track) || !LockedContentEquals(entry.Track, entry.State)))
                {
                    throw new EditException($"La pista «{entry.State.Name}» está bloqueada. Desbloquéala para editarla.");
                }
            }
        }
    }

    // El nombre y las marcas no cuentan: la propia operación ya rechaza cambiarlos en una pista bloqueada.
    private static bool LockedContentEquals(Track a, Track b)
    {
        if (a.Id != b.Id || a.Kind != b.Kind || a.Clips.Count != b.Clips.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Clips.Count; i++)
        {
            if (!SequenceSnapshot.ClipStateEquals(a.Clips[i], b.Clips[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static Func<EditContext, IReadOnlyList<string>> Wrap(Action<EditContext> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return context =>
        {
            operation(context);
            return [];
        };
    }
}
