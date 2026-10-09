using FotoToma.Core.Editing;
using FotoToma.Core.Model;

namespace FotoToma.Core.Commands;

/// <summary>
/// Comando que envuelve cualquier operación de edición sobre una secuencia y la hace deshacible con
/// fotos completas (<see cref="SequenceSnapshot"/>) de antes y de después.
/// </summary>
/// <remarks>
/// <para>
/// La primera vez que se aplica corre la operación. Si la operación lanza cualquier excepción, la
/// secuencia vuelve a la foto de antes y la misma excepción se relanza: una edición que falla a medias
/// nunca deja el proyecto a medio cambiar.
/// </para>
/// <para>
/// Al terminar comprueba que ninguna pista que estaba bloqueada cambió de contenido ni desapareció. Las
/// marcas de bloqueo, silencio y visibilidad sí pueden cambiar, para que una operación pueda, por
/// ejemplo, desbloquear o silenciar una pista bloqueada.
/// </para>
/// <para>
/// Rehacer no vuelve a correr la operación: restaura la foto de después. Así los identificadores de los
/// objetos creados (<see cref="CreatedIds"/>) son idénticos en cada rehacer.
/// </para>
/// </remarks>
public sealed class SequenceEditCommand : ICommand
{
    private readonly Func<EditContext, IReadOnlyList<string>> _operation;
    private readonly IIdGenerator _ids;
    private SequenceSnapshot? _before;
    private SequenceSnapshot? _after;

    /// <param name="description">Texto para el menú de deshacer.</param>
    /// <param name="sequenceId">Secuencia que se edita.</param>
    /// <param name="operation">La edición; devuelve los identificadores de lo que creó.</param>
    /// <param name="ids">Fuente de identificadores; por omisión, GUID.</param>
    /// <param name="mergeKey">
    /// Si dos comandos seguidos tienen la misma clave (no nula) y la misma secuencia, se funden en un
    /// solo paso de deshacer, como los movimientos sucesivos de un arrastre.
    /// </param>
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

    /// <summary>Igual que el otro constructor, para operaciones que no crean objetos.</summary>
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

    /// <summary>
    /// Identificadores de los objetos que creó la operación (vacío antes de aplicarse). Si se fundieron
    /// varios comandos, incluye los de todos, en orden.
    /// </summary>
    public IReadOnlyList<string> CreatedIds { get; private set; } = [];

    /// <summary>Indica si la operación ya corrió con éxito al menos una vez.</summary>
    public bool HasRun => _after is not null;

    public void Apply(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        Sequence sequence = FindSequence(project);

        if (_after is not null)
        {
            _after.Restore(sequence);
            return;
        }

        SequenceSnapshot before = SequenceSnapshot.Capture(sequence);
        IReadOnlyList<string> created;
        try
        {
            created = _operation(new EditContext(project, sequence, _ids));
            EnsureLockedTracksUntouched(before, sequence);
        }
        catch
        {
            before.Restore(sequence);
            throw;
        }

        _before = before;
        _after = SequenceSnapshot.Capture(sequence);
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
    }

    public bool TryMerge(ICommand following)
    {
        if (following is not SequenceEditCommand next
            || ReferenceEquals(next, this)
            || MergeKey is null
            || next.MergeKey != MergeKey
            || next.SequenceId != SequenceId
            || _after is null
            || next._after is null)
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

    /// <summary>
    /// Lanza <see cref="EditException"/> si alguna pista bloqueada antes de la operación cambió de
    /// contenido o dejó de estar en su lista (puede cambiar de orden).
    /// </summary>
    private static void EnsureLockedTracksUntouched(SequenceSnapshot before, Sequence sequence)
    {
        Check(before.VideoTracks, sequence.VideoTracks);
        Check(before.AudioTracks, sequence.AudioTracks);

        static void Check(IReadOnlyList<SequenceSnapshot.TrackEntry> entries, List<Track> current)
        {
            foreach (SequenceSnapshot.TrackEntry entry in entries)
            {
                if (entry.State.Locked
                    && (!current.Contains(entry.Track) || !SequenceSnapshot.TrackContentEquals(entry.Track, entry.State)))
                {
                    throw new EditException($"La pista «{entry.State.Name}» está bloqueada. Desbloquéala para editarla.");
                }
            }
        }
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
