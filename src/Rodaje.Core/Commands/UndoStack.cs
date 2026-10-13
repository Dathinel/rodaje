using Rodaje.Core.Model;

namespace Rodaje.Core.Commands;

/// <summary>Historial de deshacer y rehacer; toda modificación pasa por <see cref="Execute"/>.</summary>
/// <remarks>
/// Nunca se funde sobre el punto limpio, para que «guardado» siga significando el estado guardado. Si el
/// punto limpio se pierde (por el límite o al ejecutar tras deshacer), no vuelve hasta otro <see cref="MarkClean"/>.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "«Pila de deshacer» es el nombre establecido del concepto; no es una colección genérica.")]
public sealed class UndoStack
{
    private readonly List<ICommand> _undo = [];
    private readonly List<ICommand> _redo = [];
    private readonly List<ICommand> _groupCommands = [];
    private string _groupDescription = "";
    private int _groupDepth;

    private bool _mergeBarrier;

    // Pasos de deshacer en el punto limpio; null si ya no se puede alcanzar.
    private int? _cleanIndex = 0;

    public UndoStack(Project project, int limit = 500)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        Project = project;
        Limit = limit;
    }

    public event EventHandler? Changed;

    public Project Project { get; }

    /// <summary>Máximo de pasos de deshacer; los más antiguos se descartan.</summary>
    public int Limit { get; }

    public int Count => _undo.Count;

    public int RedoCount => _redo.Count;

    public bool IsInGroup => _groupDepth > 0;

    public bool CanUndo => !IsInGroup && _undo.Count > 0;

    public bool CanRedo => !IsInGroup && _redo.Count > 0;

    public string? UndoDescription => _undo.Count > 0 ? _undo[^1].Description : null;

    public string? RedoDescription => _redo.Count > 0 ? _redo[^1].Description : null;

    /// <summary>Pasos aplicados, del más antiguo al más reciente (el último es el que deshace <see cref="Undo"/>).</summary>
    public IReadOnlyList<ICommand> UndoCommands => _undo;

    /// <summary>Pasos deshechos, del más antiguo al más reciente (el último es el que rehace <see cref="Redo"/>).</summary>
    public IReadOnlyList<ICommand> RedoCommands => _redo;

    /// <summary>
    /// Pasos aplicados en el punto limpio, o <c>null</c> si ya no se puede alcanzar; para marcar en un historial qué paso
    /// coincide con lo guardado.
    /// </summary>
    public int? CleanIndex => _cleanIndex;

    /// <summary>Indica si el proyecto está como en el último <see cref="MarkClean"/>.</summary>
    public bool IsClean => _groupCommands.Count == 0 && _cleanIndex == _undo.Count;

    /// <summary>Aplica el comando y lo guarda, fundiéndolo con el último si se puede.</summary>
    public void Execute(ICommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Apply(Project);

        if (IsInGroup)
        {
            // Lo que se puede rehacer se pierde al cerrar el grupo, no antes: si se cancela (Esc mientras se arrastra), todo
            // queda como estaba, también el historial.
            // Un arrastre dentro de un gesto ejecuta un paso por movimiento: los que se funden no se acumulan.
            if (_mergeBarrier || _groupCommands.Count == 0 || !_groupCommands[^1].TryMerge(command))
            {
                _groupCommands.Add(command);
            }
        }
        else
        {
            ClearRedo();
            if (_mergeBarrier || _undo.Count == 0 || _cleanIndex == _undo.Count || !_undo[^1].TryMerge(command))
            {
                Push(command);
            }
        }

        _mergeBarrier = false;
        OnChanged();
    }

    public bool Undo()
    {
        EnsureNoGroup();
        if (_undo.Count == 0)
        {
            return false;
        }

        ICommand command = _undo[^1];
        command.Revert(Project);
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(command);
        _mergeBarrier = true;
        OnChanged();
        return true;
    }

    public bool Redo()
    {
        EnsureNoGroup();
        if (_redo.Count == 0)
        {
            return false;
        }

        ICommand command = _redo[^1];
        command.Apply(Project);
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(command);
        _mergeBarrier = true;
        OnChanged();
        return true;
    }

    /// <summary>Fin de un gesto: el próximo comando no se funde con el último aunque comparta clave.</summary>
    public void BreakMerge() => _mergeBarrier = true;

    /// <summary>Abre un grupo anidable; manda la descripción del más externo.</summary>
    public void BeginGroup(string description)
    {
        ArgumentNullException.ThrowIfNull(description);
        if (_groupDepth == 0)
        {
            _groupDescription = description;
            _groupCommands.Clear();
        }

        _groupDepth++;
        OnChanged();
    }

    /// <summary>Cierra un nivel; al cerrar el más externo, lo ejecutado queda como un solo paso.</summary>
    public void EndGroup()
    {
        if (_groupDepth == 0)
        {
            throw new InvalidOperationException("No hay ningún grupo abierto que cerrar.");
        }

        _groupDepth--;
        if (_groupDepth == 0 && _groupCommands.Count > 0)
        {
            CompositeCommand group = CompositeCommand.FromApplied(_groupDescription, _groupCommands);
            _groupCommands.Clear();
            ClearRedo();
            Push(group);
        }

        if (_groupDepth == 0)
        {
            // Lo que venga después del gesto es otro paso: no se funde con lo que había antes de él.
            _mergeBarrier = true;
        }

        OnChanged();
    }

    /// <summary>Revierte todo el grupo abierto, con todos sus niveles, sin dejar nada en el historial.</summary>
    public void CancelGroup()
    {
        EnsureGroup();
        for (int i = _groupCommands.Count - 1; i >= 0; i--)
        {
            _groupCommands[i].Revert(Project);
        }

        _groupCommands.Clear();
        _groupDepth = 0;
        // Los comandos del grupo cancelado quitaron la barrera: sin esto, el siguiente se fundiría con el último paso de
        // antes del gesto (empujar, cancelar un arrastre y volver a empujar dejaba un solo paso con los dos empujones).
        _mergeBarrier = true;
        OnChanged();
    }

    /// <summary>Marca el estado actual como limpio (al guardar); no se permite con un grupo abierto.</summary>
    public void MarkClean()
    {
        EnsureNoGroup();
        _cleanIndex = _undo.Count;
        OnChanged();
    }

    /// <summary>
    /// Marca el proyecto como distinto de lo guardado sin un paso en el historial: al abrir un archivo que se corrigió al
    /// leerlo. Ningún deshacer vuelve a «guardado»; solo otro <see cref="MarkClean"/>. No se permite con un grupo abierto.
    /// </summary>
    public void MarkDirty()
    {
        EnsureNoGroup();
        _cleanIndex = null;
        OnChanged();
    }

    private void Push(ICommand command)
    {
        _undo.Add(command);
        while (_undo.Count > Limit)
        {
            _undo.RemoveAt(0);
            if (_cleanIndex is int clean)
            {
                _cleanIndex = clean > 0 ? clean - 1 : null;
            }
        }
    }

    private void ClearRedo()
    {
        // Un punto limpio entre los pasos deshechos se pierde con ellos.
        if (_cleanIndex > _undo.Count)
        {
            _cleanIndex = null;
        }

        _redo.Clear();
    }

    private void EnsureNoGroup()
    {
        if (IsInGroup)
        {
            throw new InvalidOperationException("Hay un grupo de comandos abierto; ciérralo o cancélalo primero.");
        }
    }

    private void EnsureGroup()
    {
        if (!IsInGroup)
        {
            throw new InvalidOperationException("No hay ningún grupo abierto.");
        }
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
