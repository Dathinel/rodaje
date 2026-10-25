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

    /// <summary>
    /// El efecto <paramref name="effectId"/> del clip o, si no lo tiene, el primero de ese tipo: por id primero, para que con
    /// dos filtros iguales se edite el pulsado.
    /// </summary>
    public static EffectInstance? Equivalent(Clip clip, string? effectId, string type)
    {
        ArgumentNullException.ThrowIfNull(clip);
        return clip.Effects.Find(e => e.Id == effectId) ?? clip.Effects.Find(e => e.Type == type);
    }

    /// <summary>
    /// El efecto <paramref name="effectId"/> de <paramref name="current"/> y el del mismo puesto entre los de su tipo en cada
    /// clip que tenga tantos como él: con otra cuenta (Añadir los pone al final), el puesto empareja con uno que no es.
    /// </summary>
    public static List<(Clip Clip, string EffectId)> SamePlace(Clip current, string effectId, string type, IEnumerable<Clip> clips)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(clips);
        List<EffectInstance> mine = current.Effects.FindAll(e => e.Type == type);
        int index = mine.FindIndex(e => e.Id == effectId);
        var list = new List<(Clip Clip, string EffectId)>();
        if (index < 0)
        {
            return list;
        }

        foreach (Clip clip in clips)
        {
            List<EffectInstance> theirs = clip.Effects.FindAll(e => e.Type == type);
            if (theirs.Count == mine.Count)
            {
                list.Add((clip, theirs[index].Id));
            }
        }

        return list;
    }

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
