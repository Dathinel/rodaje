using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Commands;

/// <summary>
/// Fábricas de comandos de velocidad (fase 5c). Si el clip se acorta, lo que sigue se une; si crece, solo empuja lo que
/// pisa. <c>scope</c> es el modo de ripple de la sesión: las pistas del grupo, todas, o todas y los marcadores.
/// </summary>
public static partial class Edits
{
    /// <summary>Velocidad constante del clip y sus enlazados; se funde por clip para arrastrar el deslizador.</summary>
    public static SequenceEditCommand SetSpeed(
        string sequenceId,
        string clipId,
        Rational rate,
        RippleScope scope = RippleScope.Track,
        IIdGenerator? ids = null) =>
        new("Cambiar velocidad", sequenceId, ctx => TimelineEdits.SetSpeed(ctx, clipId, rate, scope), ids, $"speed:{clipId}");

    /// <summary>Alt + arrastrar un borde: cambia la duración con el mismo material. Se funde por clip y borde.</summary>
    public static SequenceEditCommand RateStretch(
        string sequenceId,
        string clipId,
        TrimEdge edge,
        Time delta,
        RippleScope scope = RippleScope.Track,
        IIdGenerator? ids = null) =>
        new("Estirar velocidad", sequenceId, ctx => TimelineEdits.RateStretch(ctx, clipId, edge, delta, scope), ids,
            $"ratestretch:{clipId}:{EdgeKey(edge)}");

    /// <summary>Congela el cuadro de <paramref name="at"/> durante <paramref name="duration"/> (3 s por omisión).</summary>
    /// <remarks><see cref="SequenceEditCommand.CreatedIds"/>: el clip congelado.</remarks>
    public static SequenceEditCommand FreezeFrame(
        string sequenceId,
        string clipId,
        Time at,
        Time? duration = null,
        IIdGenerator? ids = null,
        RippleScope scope = RippleScope.Track) =>
        new("Congelar cuadro", sequenceId, ctx => (IReadOnlyList<string>)[TimelineEdits.FreezeFrame(ctx, clipId, at, duration, scope)], ids);

    /// <summary>Curva de velocidad del clip y sus enlazados; <c>null</c> vuelve a 1×. Se funde por clip.</summary>
    public static SequenceEditCommand SetCurve(
        string sequenceId,
        string clipId,
        SpeedCurve? curve,
        RippleScope scope = RippleScope.Track,
        IIdGenerator? ids = null)
    {
        SpeedCurve? copy = curve?.Clone();
        return new("Cambiar curva de velocidad", sequenceId, ctx => TimelineEdits.SetCurve(ctx, clipId, copy, scope), ids, $"curve:{clipId}");
    }

    /// <summary>Invierte o endereza el clip y sus enlazados, con sus keyframes reflejados en el mismo paso.</summary>
    public static SequenceEditCommand SetReverse(string sequenceId, string clipId, bool reverse, IIdGenerator? ids = null) =>
        new(reverse ? "Invertir clip" : "Quitar reversa", sequenceId, ctx => TimelineEdits.SetReverse(ctx, clipId, reverse), ids);

    public static SequenceEditCommand SetPreservePitch(string sequenceId, string clipId, bool preservePitch, IIdGenerator? ids = null) =>
        new("Mantener tono", sequenceId, ctx => TimelineEdits.SetPreservePitch(ctx, clipId, preservePitch), ids);
}
