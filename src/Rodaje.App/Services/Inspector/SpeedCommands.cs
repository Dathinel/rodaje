using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services.Inspector;

/// <summary>
/// Las ediciones de velocidad que usa la pestaña Velocidad. Cada una cambia el clip y sus enlazados en un solo paso y,
/// en cualquier pista, une lo que viene detrás si el clip se acorta y empuja solo lo que pisa si crece; <c>scope</c> es
/// el modo de ripple de la sesión (solo las pistas del grupo, todas, o todas y los marcadores).
/// </summary>
/// <remarks>Costura para que las pruebas de la pestaña registren lo que pide; la aplicación usa <see cref="SpeedCommands.Default"/>.</remarks>
public interface ISpeedCommands
{
    /// <summary>Velocidad constante <paramref name="rate"/>; a 1× sin curva ni reversa el clip vuelve a no tener velocidad.</summary>
    ICommand SetSpeed(string sequenceId, string clipId, Rational rate, RippleScope scope);

    /// <summary>La velocidad constante que deja el clip con la duración <paramref name="duration"/> en la pista.</summary>
    ICommand RateStretch(string sequenceId, string clipId, Time duration, RippleScope scope);

    /// <summary>Solo cambia algo en un clip con velocidad: a 1× el tono no se toca.</summary>
    ICommand SetPreservePitch(string sequenceId, string clipId, bool preservePitch);

    /// <summary>Invierte o endereza el clip y sus enlazados; la duración y el material no cambian.</summary>
    ICommand SetReverse(string sequenceId, string clipId, bool reverse);

    /// <summary>Congela el cuadro de <paramref name="at"/>: corta ahí y mete un congelado de <paramref name="duration"/>.</summary>
    ICommand FreezeFrame(string sequenceId, string clipId, Time at, Time duration, RippleScope scope);

    /// <summary>Curva de velocidad (la duración sale de su velocidad media); <c>null</c> vuelve a 1×.</summary>
    ICommand SetCurve(string sequenceId, string clipId, SpeedCurve? curve, RippleScope scope);

    /// <summary>La curva de un preajuste propio (<see cref="SpeedPresets"/>).</summary>
    ICommand ApplySpeedPreset(string sequenceId, string clipId, SpeedPreset preset, RippleScope scope);
}

public static class SpeedCommands
{
    /// <summary>Las ediciones del núcleo (<see cref="Edits"/>).</summary>
    public static ISpeedCommands Default { get; } = new CoreSpeedCommands();

    private sealed class CoreSpeedCommands : ISpeedCommands
    {
        public ICommand SetSpeed(string sequenceId, string clipId, Rational rate, RippleScope scope) =>
            Edits.SetSpeed(sequenceId, clipId, rate, scope);

        // La duración pasa a cuánto se mueve el final al aplicarse, con el clip de ese momento.
        public ICommand RateStretch(string sequenceId, string clipId, Time duration, RippleScope scope) =>
            new SequenceEditCommand("Cambiar duración", sequenceId, ctx =>
            {
                Time current = ctx.Sequence.FindClip(clipId)?.Clip.Duration ?? throw new EditException("El clip ya no está en la secuencia.");
                TimelineEdits.RateStretch(ctx, clipId, TrimEdge.Out, duration - current, scope);
            });

        public ICommand SetPreservePitch(string sequenceId, string clipId, bool preservePitch) =>
            Edits.SetPreservePitch(sequenceId, clipId, preservePitch);

        public ICommand SetReverse(string sequenceId, string clipId, bool reverse) =>
            Edits.SetReverse(sequenceId, clipId, reverse);

        public ICommand FreezeFrame(string sequenceId, string clipId, Time at, Time duration, RippleScope scope) =>
            Edits.FreezeFrame(sequenceId, clipId, at, duration, scope: scope);

        public ICommand SetCurve(string sequenceId, string clipId, SpeedCurve? curve, RippleScope scope) =>
            Edits.SetCurve(sequenceId, clipId, curve, scope);

        public ICommand ApplySpeedPreset(string sequenceId, string clipId, SpeedPreset preset, RippleScope scope) =>
            Edits.ApplySpeedPreset(sequenceId, clipId, preset, scope);
    }
}
