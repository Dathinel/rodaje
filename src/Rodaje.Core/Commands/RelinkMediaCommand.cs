using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Commands;

/// <summary>
/// Vuelve a enlazar uno o varios medios con su archivo en un solo paso del historial.
/// Solo cambian la ruta y, si el archivo nuevo es distinto, los datos del medio; los clips, los efectos y los keyframes
/// siguen igual porque apuntan al identificador del medio. La excepción es un medio provisional importado (D-062): sus
/// clips se corren al TC real del archivo, y deshacer los devuelve.
/// </summary>
public sealed class RelinkMediaCommand : ICommand
{
    private readonly MediaRelinkTarget[] _targets;
    private List<MediaRef>? _before;
    private List<(string SequenceId, string ClipId, Time In, Time Out)>? _ranges;

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
        var ids = _targets.Select(t => t.MediaId).ToHashSet(StringComparer.Ordinal);
        List<(string, string, Time, Time)> ranges = [.. project.Sequences.SelectMany(s => s.AllTracks.SelectMany(t => t.Clips)
            .Where(c => c.Generator is null && ids.Contains(c.MediaId)).Select(c => (s.Id, c.Id, c.SourceIn, c.SourceOut)))];
        ProjectGuard.ApplyValidated(
            project,
            "No se pudo volver a enlazar",
            () => MediaRelinkEdits.Relink(project, _targets),
            () => Restore(project, before, ranges));
        _before = before;
        _ranges = ranges;
    }

    public void Revert(Project project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (_before is null || _ranges is null)
        {
            throw new InvalidOperationException("No se puede revertir un comando que todavía no se ha aplicado.");
        }

        Restore(project, _before, _ranges);
    }

    public bool TryMerge(ICommand following) => false;

    private static void Restore(Project project, List<MediaRef> before, List<(string SequenceId, string ClipId, Time In, Time Out)> ranges)
    {
        foreach ((string sequenceId, string clipId, Time sourceIn, Time sourceOut) in ranges)
        {
            if (project.FindSequence(sequenceId)?.FindClip(clipId) is { Clip: var clip })
            {
                Time duration = clip.Duration;
                clip.SourceIn = sourceIn;
                clip.SourceOut = sourceOut;
                clip.Duration = duration;
            }
        }

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
