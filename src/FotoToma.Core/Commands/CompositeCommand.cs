using FotoToma.Core.Model;

namespace FotoToma.Core.Commands;

/// <summary>
/// Varios comandos que se aplican y se deshacen como uno solo. Si uno falla al aplicarse, los que ya se
/// habían aplicado se revierten en orden inverso y el error se relanza, así que el conjunto es todo o
/// nada.
/// </summary>
public sealed class CompositeCommand : ICommand
{
    private readonly ICommand[] _commands;

    public CompositeCommand(string description, IEnumerable<ICommand> commands)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(commands);
        ICommand[] list = [.. commands];
        if (Array.Exists(list, c => c is null))
        {
            throw new ArgumentException("La lista de comandos no puede contener elementos nulos.", nameof(commands));
        }

        Description = description;
        _commands = list;
    }

    /// <summary>
    /// Construye un compuesto con comandos que <em>ya</em> se aplicaron, en ese orden (por ejemplo los
    /// de un grupo de la pila de deshacer). No hay que volver a llamar <see cref="Apply"/> antes de
    /// guardarlo: queda en estado aplicado y la próxima llamada útil es <see cref="Revert"/>.
    /// </summary>
    public static CompositeCommand FromApplied(string description, IEnumerable<ICommand> appliedCommands) =>
        new(description, appliedCommands);

    public string Description { get; }

    /// <summary>Los comandos que forman el compuesto, en orden de aplicación.</summary>
    public IReadOnlyList<ICommand> Commands => _commands;

    public void Apply(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        int applied = 0;
        try
        {
            for (; applied < _commands.Length; applied++)
            {
                _commands[applied].Apply(project);
            }
        }
        catch
        {
            for (int i = applied - 1; i >= 0; i--)
            {
                _commands[i].Revert(project);
            }

            throw;
        }
    }

    public void Revert(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        for (int i = _commands.Length - 1; i >= 0; i--)
        {
            _commands[i].Revert(project);
        }
    }

    /// <summary>Un compuesto nunca absorbe otros comandos.</summary>
    public bool TryMerge(ICommand following) => false;
}
