namespace Rodaje.Core.Collab;

/// <summary>Por qué no se puede deshacer o rehacer un paso en sesión.</summary>
public enum HistoryFailure
{
    /// <summary>No hay pasos.</summary>
    Nothing,

    /// <summary>Alguien cambió después alguno de los elementos del paso: «No se puede deshacer: Ana cambió ese clip».</summary>
    ChangedByOthers,
}

/// <summary>Un paso listo para mandar: el parche y su descripción.</summary>
public sealed record HistoryPlan(Patch Patch, string Description);

/// <summary>El resultado de preparar un deshacer o un rehacer: un plan o la razón de que no se pueda.</summary>
/// <param name="Plan">El parche listo, o <see langword="null"/>.</param>
/// <param name="Failure">Por qué no se puede, o <see langword="null"/>.</param>
/// <param name="Element">En <see cref="HistoryFailure.ChangedByOthers"/>, el primer elemento que ya no está como lo dejó el paso.</param>
public sealed record HistoryOutcome(HistoryPlan? Plan, HistoryFailure? Failure, ElementKey? Element = null);

/// <summary>
/// Deshacer y rehacer de cada persona en una sala, con parches inversos en lugar de <c>UndoStack</c> (que restauraría la
/// secuencia entera y borraría lo que llegó de otros). Un paso solo se deshace si el contenido de sus elementos sigue siendo el
/// que dejó el paso; deshacer y rehacer son parches nuevos que se mandan como cualquier otro. Sin sesión, nada de esto se usa.
/// </summary>
public sealed class CollabHistory
{
    public CollabHistory(int limit = 500)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        Limit = limit;
    }

    public int Limit { get; }

    public bool CanUndo => throw Pending.Exception();

    public bool CanRedo => throw Pending.Exception();

    public string? UndoDescription => throw Pending.Exception();

    public string? RedoDescription => throw Pending.Exception();

    /// <summary>Anota un paso propio ya confirmado: el estado antes y después. Lo que se pudiera rehacer se pierde.</summary>
    public void Record(SharedState before, SharedState after, string description) => throw Pending.Exception();

    /// <summary>Prepara el parche que deshace el último paso sobre el estado actual, sin tocar la pila.</summary>
    public HistoryOutcome PlanUndo(SharedState current, ElementVersions versions, PatchHeader header) => throw Pending.Exception();

    /// <summary>Prepara el parche que rehace el último paso deshecho, sin tocar la pila.</summary>
    public HistoryOutcome PlanRedo(SharedState current, ElementVersions versions, PatchHeader header) => throw Pending.Exception();

    /// <summary>El anfitrión aceptó el parche de <see cref="PlanUndo"/>: el paso pasa a poder rehacerse.</summary>
    public void CommitUndo() => throw Pending.Exception();

    /// <summary>El anfitrión aceptó el parche de <see cref="PlanRedo"/>: el paso vuelve a poder deshacerse.</summary>
    public void CommitRedo() => throw Pending.Exception();

    /// <summary>Descarta el paso que no se pudo deshacer (alguien tocó su contenido), como Figma.</summary>
    public void DiscardUndo() => throw Pending.Exception();

    /// <summary>Descarta todo (al salir de la sala o al reemplazar el proyecto desde fuera).</summary>
    public void Clear() => throw Pending.Exception();
}
