using FotoToma.Core.Model;

namespace FotoToma.Core.Commands;

/// <summary>Varios comandos que se aplican todo o nada y se deshacen como uno solo.</summary>
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

    /// <summary>Compuesto de comandos ya aplicados; la siguiente llamada válida es <see cref="Revert"/>.</summary>
    public static CompositeCommand FromApplied(string description, IEnumerable<ICommand> appliedCommands) =>
        new(description, appliedCommands);

    public string Description { get; }

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

    public bool TryMerge(ICommand following) => false;
}
