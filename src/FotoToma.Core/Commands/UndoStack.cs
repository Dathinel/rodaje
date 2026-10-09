using FotoToma.Core.Model;

namespace FotoToma.Core.Commands;

/// <summary>
/// Historial de deshacer y rehacer de un proyecto. Toda modificación pasa por <see cref="Execute"/>.
/// </summary>
/// <remarks>
/// <para>
/// Fusión: si el comando nuevo puede fundirse con el último (<see cref="ICommand.TryMerge"/>), ambos
/// quedan como un solo paso; así un arrastre con cientos de movimientos se deshace de una vez. Nunca
/// se funde dentro de un grupo ni sobre el punto limpio, para que «guardado» siga significando el
/// estado que se guardó.
/// </para>
/// <para>
/// Grupos: entre <see cref="BeginGroup"/> y <see cref="EndGroup"/> los comandos se aplican al momento
/// y, al cerrar el grupo más externo, quedan como un único <see cref="CompositeCommand"/>. Los grupos
/// se pueden anidar; manda la descripción del más externo.
/// </para>
/// <para>
/// Punto limpio: <see cref="MarkClean"/> recuerda la posición actual del historial (por ejemplo al
/// guardar). Si ese paso se pierde, porque se descartó por el límite o porque se borró al ejecutar
/// algo después de deshacer, el historial ya no puede volver a estar limpio hasta el próximo
/// <see cref="MarkClean"/>.
/// </para>
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

    /// <summary>Cantidad de pasos de deshacer en el punto limpio; <c>null</c> si ya no se puede alcanzar.</summary>
    private int? _cleanIndex = 0;

    public UndoStack(Project project, int limit = 500)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        Project = project;
        Limit = limit;
    }

    /// <summary>Se dispara después de cada cambio del historial o del proyecto hecho a través de la pila.</summary>
    public event EventHandler? Changed;

    public Project Project { get; }

    /// <summary>Máximo de pasos de deshacer que se guardan; los más antiguos se descartan.</summary>
    public int Limit { get; }

    /// <summary>Pasos que se pueden deshacer.</summary>
    public int Count => _undo.Count;

    /// <summary>Pasos que se pueden rehacer.</summary>
    public int RedoCount => _redo.Count;

    /// <summary>Indica si hay un grupo abierto.</summary>
    public bool IsInGroup => _groupDepth > 0;

    public bool CanUndo => !IsInGroup && _undo.Count > 0;

    public bool CanRedo => !IsInGroup && _redo.Count > 0;

    public string? UndoDescription => _undo.Count > 0 ? _undo[^1].Description : null;

    public string? RedoDescription => _redo.Count > 0 ? _redo[^1].Description : null;

    /// <summary>Indica si el proyecto está como en el último <see cref="MarkClean"/>.</summary>
    public bool IsClean => _groupCommands.Count == 0 && _cleanIndex == _undo.Count;

    /// <summary>
    /// Aplica el comando y lo guarda en el historial. Si el comando lanza una excepción, no se guarda
    /// nada y la excepción sigue su camino (el comando ya dejó el proyecto como estaba).
    /// </summary>
    public void Execute(ICommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        command.Apply(Project);
        ClearRedo();

        if (IsInGroup)
        {
            _groupCommands.Add(command);
        }
        else if (_undo.Count == 0 || _cleanIndex == _undo.Count || !_undo[^1].TryMerge(command))
        {
            Push(command);
        }

        OnChanged();
    }

    /// <summary>Deshace el último paso. Devuelve <c>false</c> si no había nada que deshacer.</summary>
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
        OnChanged();
        return true;
    }

    /// <summary>Rehace el último paso deshecho. Devuelve <c>false</c> si no había nada que rehacer.</summary>
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
        OnChanged();
        return true;
    }

    /// <summary>Abre un grupo (o un nivel más de anidamiento).</summary>
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

    /// <summary>
    /// Cierra un nivel de grupo. Al cerrar el más externo, lo ejecutado dentro queda como un solo paso;
    /// un grupo vacío no deja nada.
    /// </summary>
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
            Push(group);
        }

        OnChanged();
    }

    /// <summary>
    /// Cancela el grupo abierto con todos sus niveles: revierte en orden inverso lo ejecutado dentro y
    /// no deja nada en el historial.
    /// </summary>
    public void CancelGroup()
    {
        EnsureGroup();
        for (int i = _groupCommands.Count - 1; i >= 0; i--)
        {
            _groupCommands[i].Revert(Project);
        }

        _groupCommands.Clear();
        _groupDepth = 0;
        OnChanged();
    }

    /// <summary>
    /// Marca el estado actual como limpio, por ejemplo al guardar el proyecto. No se puede con un grupo
    /// abierto, porque el estado intermedio de un grupo no es un paso del historial.
    /// </summary>
    public void MarkClean()
    {
        EnsureNoGroup();
        _cleanIndex = _undo.Count;
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
                // Si el paso descartado era el punto limpio, ya no se puede volver a él.
                _cleanIndex = clean > 0 ? clean - 1 : null;
            }
        }
    }

    private void ClearRedo()
    {
        // Si el punto limpio estaba entre los pasos deshechos, se pierde con ellos.
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
