using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Commands;

/// <summary>Fábricas de comandos para <see cref="TransitionEdits"/>.</summary>
public static partial class Edits
{
    /// <summary>Pone una transición en el corte donde termina <paramref name="fromClipId"/>; por defecto dura 0,5 s.</summary>
    /// <remarks><see cref="SequenceEditCommand.CreatedIds"/>: la transición creada.</remarks>
    public static SequenceEditCommand AddTransition(string sequenceId, string fromClipId, string type, Time? duration = null, IIdGenerator? ids = null) =>
        new("Añadir transición", sequenceId, ctx => (IReadOnlyList<string>)[TransitionEdits.AddTransition(ctx, fromClipId, type, duration)], ids);

    public static SequenceEditCommand RemoveTransition(string sequenceId, string transitionId, IIdGenerator? ids = null) =>
        new("Quitar transición", sequenceId, ctx => TransitionEdits.RemoveTransition(ctx, transitionId), ids);

    /// <summary>Se funde con el siguiente cambio de duración de la misma transición: arrastrar una manija deja un paso.</summary>
    public static SequenceEditCommand SetTransitionDuration(string sequenceId, string transitionId, Time duration, IIdGenerator? ids = null) =>
        new("Cambiar duración de la transición", sequenceId, ctx => TransitionEdits.SetDuration(ctx, transitionId, duration), ids,
            $"transition-duration:{transitionId}");

    /// <summary>La misma duración en varias transiciones a la vez, cada una acotada a sus clips.</summary>
    public static SequenceEditCommand SetTransitionDurations(string sequenceId, IReadOnlyCollection<string> transitionIds, Time duration, IIdGenerator? ids = null) =>
        new("Cambiar duración de las transiciones", sequenceId, ctx => TransitionEdits.SetDurations(ctx, transitionIds, duration), ids,
            $"transition-durations:{string.Join(',', transitionIds)}");

    public static SequenceEditCommand SetTransitionAlignment(string sequenceId, string transitionId, TransitionAlignment alignment, IIdGenerator? ids = null) =>
        new("Cambiar alineación de la transición", sequenceId, ctx => TransitionEdits.SetAlignment(ctx, transitionId, alignment), ids);

    /// <summary>Se funde con el cambio anterior de la curva de la misma transición: arrastrar un manejador deja un paso.</summary>
    public static SequenceEditCommand SetTransitionCurve(string sequenceId, string transitionId, Interpolation? curve, BezierEase? ease = null, IIdGenerator? ids = null) =>
        new("Cambiar curva de la transición", sequenceId, ctx => TransitionEdits.SetCurve(ctx, transitionId, curve, ease), ids,
            $"transition-curve:{transitionId}");

    public static SequenceEditCommand SetTransitionType(string sequenceId, string transitionId, string type, IIdGenerator? ids = null) =>
        new("Cambiar transición", sequenceId, ctx => TransitionEdits.SetType(ctx, transitionId, type), ids);

    public static SequenceEditCommand SetTransitionParameter(string sequenceId, string transitionId, string name, double value, IIdGenerator? ids = null) =>
        new("Cambiar transición", sequenceId, ctx => TransitionEdits.SetParameter(ctx, transitionId, name, value), ids,
            $"transition-param:{transitionId}:{name}");

    public static SequenceEditCommand SetTransitionOption(string sequenceId, string transitionId, string name, string value, IIdGenerator? ids = null) =>
        new("Cambiar transición", sequenceId, ctx => TransitionEdits.SetOption(ctx, transitionId, name, value), ids);
}
