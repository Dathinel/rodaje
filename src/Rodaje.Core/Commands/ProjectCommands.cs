using Rodaje.Core.Editing;
using Rodaje.Core.Model;

namespace Rodaje.Core.Commands;

/// <summary>Agrega un medio; falla con <see cref="EditException"/> si deja el proyecto inválido.</summary>
public sealed class AddMediaCommand : ICommand
{
    private readonly MediaRef _media;

    public AddMediaCommand(MediaRef media)
    {
        ArgumentNullException.ThrowIfNull(media);
        _media = media;
    }

    public string Description => "Importar medio";

    public void Apply(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.FindMedia(_media.Id) is not null)
        {
            throw new EditException($"Ya hay un medio con el identificador «{_media.Id}» en el proyecto.");
        }

        ProjectGuard.ApplyValidated(
            project,
            "No se puede importar el medio",
            () => project.Media.Add(_media),
            () => project.Media.Remove(_media));
    }

    public void Revert(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        project.Media.Remove(_media);
    }

    public bool TryMerge(ICommand following) => false;
}

/// <summary>Quita un medio que ningún clip use.</summary>
public sealed class RemoveMediaCommand : ICommand
{
    private readonly string _mediaId;
    private MediaRef? _removed;
    private int _index;

    public RemoveMediaCommand(string mediaId)
    {
        ArgumentNullException.ThrowIfNull(mediaId);
        _mediaId = mediaId;
    }

    public string Description => "Quitar medio";

    public void Apply(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        int index = project.Media.FindIndex(m => m.Id == _mediaId);
        if (index < 0)
        {
            throw new EditException($"No existe el medio «{_mediaId}» en el proyecto.");
        }

        foreach (Sequence sequence in project.Sequences)
        {
            if (sequence.AllTracks.Any(t => t.Clips.Exists(c => c.Generator is null && c.MediaId == _mediaId)))
            {
                throw new EditException(
                    $"El medio «{project.Media[index].Name}» se usa en la secuencia «{sequence.Name}». Quita primero sus clips.");
            }
        }

        _removed = project.Media[index];
        _index = index;
        project.Media.RemoveAt(index);
    }

    public void Revert(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (_removed is null)
        {
            throw new InvalidOperationException("No se puede revertir un comando que todavía no se ha aplicado.");
        }

        project.Media.Insert(Math.Min(_index, project.Media.Count), _removed);
    }

    public bool TryMerge(ICommand following) => false;
}

/// <summary>Agrega una secuencia; falla con <see cref="EditException"/> si deja el proyecto inválido.</summary>
public sealed class AddSequenceCommand : ICommand
{
    private readonly Sequence _sequence;
    private readonly int? _index;

    public AddSequenceCommand(Sequence sequence, int? index = null)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentOutOfRangeException.ThrowIfNegative(index ?? 0, nameof(index));
        _sequence = sequence;
        _index = index;
    }

    public string Description => "Nueva secuencia";

    public void Apply(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.FindSequence(_sequence.Id) is not null)
        {
            throw new EditException($"Ya hay una secuencia con el identificador «{_sequence.Id}» en el proyecto.");
        }

        if (_index is int index)
        {
            if (index > project.Sequences.Count)
            {
                throw new EditException(
                    $"No se puede poner la secuencia en la posición {index}: el proyecto tiene {project.Sequences.Count}.");
            }
        }

        ProjectGuard.ApplyValidated(
            project,
            "No se puede agregar la secuencia",
            () => project.Sequences.Insert(_index ?? project.Sequences.Count, _sequence),
            () => project.Sequences.Remove(_sequence));
    }

    public void Revert(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        project.Sequences.Remove(_sequence);
    }

    public bool TryMerge(ICommand following) => false;
}

public sealed class RemoveSequenceCommand : ICommand
{
    private readonly string _sequenceId;
    private Sequence? _removed;
    private int _index;

    public RemoveSequenceCommand(string sequenceId)
    {
        ArgumentNullException.ThrowIfNull(sequenceId);
        _sequenceId = sequenceId;
    }

    public string Description => "Eliminar secuencia";

    public void Apply(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        int index = project.Sequences.FindIndex(s => s.Id == _sequenceId);
        if (index < 0)
        {
            throw new EditException($"No existe la secuencia «{_sequenceId}» en el proyecto.");
        }

        // Un clip compuesto que la muestre quedaría apuntando a nada, y Load rechazaría el archivo guardado.
        Sequence removed = project.Sequences[index];
        foreach (Sequence sequence in project.Sequences)
        {
            if (sequence != removed && sequence.AllTracks.Any(t => t.Clips.Exists(c => c.SequenceId == _sequenceId)))
            {
                throw new EditException(
                    $"La secuencia «{removed.Name}» se usa como clip compuesto en «{sequence.Name}». Quita primero esos clips.");
            }
        }

        ProjectGuard.ApplyValidated(
            project,
            "No se puede eliminar la secuencia",
            () => project.Sequences.RemoveAt(index),
            () => project.Sequences.Insert(index, removed));
        _removed = removed;
        _index = index;
    }

    public void Revert(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (_removed is null)
        {
            throw new InvalidOperationException("No se puede revertir un comando que todavía no se ha aplicado.");
        }

        project.Sequences.Insert(Math.Min(_index, project.Sequences.Count), _removed);
    }

    public bool TryMerge(ICommand following) => false;
}

public sealed class RenameProjectCommand : ICommand
{
    private readonly string _name;
    private string? _previous;

    public RenameProjectCommand(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        _name = name;
    }

    public string Description => "Renombrar proyecto";

    public void Apply(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (string.IsNullOrWhiteSpace(_name))
        {
            throw new EditException("El nombre del proyecto no puede quedar vacío.");
        }

        _previous = project.Name;
        project.Name = _name;
    }

    public void Revert(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (_previous is null)
        {
            throw new InvalidOperationException("No se puede revertir un comando que todavía no se ha aplicado.");
        }

        project.Name = _previous;
    }

    public bool TryMerge(ICommand following) => false;
}

// Rechaza un cambio que introduce problemas nuevos, para no guardar un archivo que Load rechazaría.
// Solo cuentan los nuevos (multiconjunto): un proyecto ya inválido no bloquea comandos ajenos.
internal static class ProjectGuard
{
    public static void ApplyValidated(Project project, string what, Action apply, Action revert)
    {
        IReadOnlyList<string> before = ModelValidator.Validate(project);
        apply();
        string? problem = FirstNewProblem(before, ModelValidator.Validate(project));
        if (problem is not null)
        {
            revert();
            throw new EditException($"{what}: {problem}");
        }
    }

    internal static string? FirstNewProblem(IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        var pending = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string problem in before)
        {
            pending[problem] = pending.GetValueOrDefault(problem) + 1;
        }

        foreach (string problem in after)
        {
            int count = pending.GetValueOrDefault(problem);
            if (count == 0)
            {
                return problem;
            }

            pending[problem] = count - 1;
        }

        return null;
    }
}
