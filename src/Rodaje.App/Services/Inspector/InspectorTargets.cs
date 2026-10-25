using Rodaje.Core.Commands;
using Rodaje.Core.Model;

namespace Rodaje.App.Services.Inspector;

/// <summary>
/// Resolución común de la selección para los paneles del inspector: los clips seleccionados que siguen en la secuencia, en
/// el orden de la selección y pasados por el filtro de cada panel.
/// </summary>
public static class InspectorTargets
{
    public static List<(Track Track, Clip Clip)> Selected(EditorSession session, Func<(Track Track, Clip Clip), bool>? where = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        Sequence sequence = session.Sequence;
        var list = new List<(Track Track, Clip Clip)>();
        foreach (string id in session.SelectedClipIds)
        {
            if (sequence.FindClip(id) is { } found && where?.Invoke(found) != false)
            {
                list.Add(found);
            }
        }

        return list;
    }

    public static (Track Track, Clip Clip)? First(EditorSession session, Func<(Track Track, Clip Clip), bool>? where = null) =>
        Selected(session, where) is [var first, ..] ? first : null;

    /// <summary>Varios comandos como un solo paso del historial; uno solo, tal cual; ninguno, nulo.</summary>
    public static ICommand? AsOneStep(IReadOnlyList<ICommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        return commands.Count switch
        {
            0 => null,
            1 => commands[0],
            _ => new CompositeCommand(commands[0].Description, commands),
        };
    }
}
