using FotoToma.Core.Editing;
using FotoToma.Core.Model;

namespace FotoToma.Core.Commands;

/// <summary>Agrega un medio a la biblioteca del proyecto.</summary>
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

        project.Media.Add(_media);
    }

    public void Revert(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        project.Media.Remove(_media);
    }

    public bool TryMerge(ICommand following) => false;
}

/// <summary>
/// Quita un medio de la biblioteca. Falla si algún clip de cualquier secuencia lo usa; al deshacer
/// vuelve a su misma posición en la lista.
/// </summary>
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
            if (sequence.AllTracks.Any(t => t.Clips.Exists(c => c.MediaId == _mediaId)))
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

/// <summary>Agrega una secuencia al proyecto, al final o en la posición indicada.</summary>
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

            project.Sequences.Insert(index, _sequence);
        }
        else
        {
            project.Sequences.Add(_sequence);
        }
    }

    public void Revert(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        project.Sequences.Remove(_sequence);
    }

    public bool TryMerge(ICommand following) => false;
}

/// <summary>Quita una secuencia del proyecto; al deshacer vuelve a su misma posición.</summary>
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

        _removed = project.Sequences[index];
        _index = index;
        project.Sequences.RemoveAt(index);
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

/// <summary>Cambia el nombre del proyecto.</summary>
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
