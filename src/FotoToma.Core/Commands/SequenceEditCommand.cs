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
/// Al terminar comprueba que ninguna pista que estaba bloqueada y sigue bloqueada cambió de clips ni
/// desapareció. Si la operación la desbloqueó, todo lo demás que haya hecho en la misma llamada vale,
/// para que se pueda, por ejemplo, desbloquear y renombrar una pista de una sola vez.
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
    /// Lanza <see cref="EditException"/> si alguna pista que estaba bloqueada antes de la operación y
    /// <em>sigue</em> bloqueada después cambió de contenido o dejó de estar en su lista (sí puede cambiar
    /// de orden).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Si la operación desbloqueó la pista, cualquier otro cambio hecho en la misma llamada se acepta:
    /// es lo que permite, por ejemplo, desbloquear y renombrar a la vez con
    /// <see cref="TimelineEdits.SetTrackState"/>.
    /// </para>
    /// <para>
    /// El «contenido» que se vigila es el identificador, el tipo y los clips (con efectos, parámetros y
    /// keyframes). El nombre y las marcas de silencio y visibilidad no entran: cambiarlos en una pista
    /// que sigue bloqueada ya lo rechaza la propia operación, y esta comprobación es solo la red de
    /// seguridad para los clips.
    /// </para>
    /// </remarks>
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

    /// <summary>Compara identificador, tipo y clips de dos pistas; no mira el nombre ni las marcas.</summary>
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
