using Rodaje.Core.Editing;
using Rodaje.Core.Model;

namespace Rodaje.Core.Commands;

/// <summary>
/// Vuelve a enlazar uno o varios medios con su archivo en un solo paso del historial.
/// Solo cambian la ruta y, si el archivo nuevo es distinto, los datos del medio; los clips, los efectos y los keyframes
/// siguen igual porque apuntan al identificador del medio.
/// </summary>
public sealed class RelinkMediaCommand : ICommand
{
    private readonly MediaRelinkTarget[] _targets;
    private List<MediaRef>? _before;

    public RelinkMediaCommand(IReadOnlyList<MediaRelinkTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0)
        {
            throw new ArgumentException("Hace falta al menos un medio para volver a enlazar.", nameof(targets));
        }

        _targets = [.. targets];
    }

    public string Description => _targets.Length == 1 ? "Volver a enlazar medio" : $"Volver a enlazar {_targets.Length} medios";

    public void Apply(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        List<MediaRef> before = [.. _targets.Select(t => project.FindMedia(t.MediaId)?.Clone()).OfType<MediaRef>()];
        ProjectGuard.ApplyValidated(
            project,
            "No se pudo volver a enlazar",
            () => MediaRelinkEdits.Relink(project, _targets),
            () => Restore(project, before));
        _before = before;
    }

    public void Revert(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (_before is null)
        {
            throw new InvalidOperationException("No se puede revertir un comando que todavía no se ha aplicado.");
        }

        Restore(project, _before);
    }

    public bool TryMerge(ICommand following) => false;

    private static void Restore(Project project, List<MediaRef> before)
    {
        foreach (MediaRef saved in before)
        {
            if (project.FindMedia(saved.Id) is { } media)
            {
                media.Path = saved.Path;
                RelinkProbe.Of(saved).ApplyTo(media);
            }
        }
    }
}
