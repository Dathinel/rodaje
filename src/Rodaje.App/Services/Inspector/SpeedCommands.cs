using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services.Inspector;

/// <summary>
/// Las ediciones de velocidad que usa la pestaña Velocidad. Cada una cambia el clip y sus enlazados en un solo paso; con
/// <c>ripple</c> corre lo que viene detrás según cambie la duración, y sin él solo si el clip alargado choca.
/// </summary>
/// <remarks>Costura para que las pruebas de la pestaña registren lo que pide; la aplicación usa <see cref="SpeedCommands.Default"/>.</remarks>
public interface ISpeedCommands
{
    /// <summary>Velocidad constante <paramref name="rate"/>; a 1× sin curva ni reversa el clip vuelve a no tener velocidad.</summary>
    ICommand SetSpeed(string sequenceId, string clipId, Rational rate, bool ripple);

    /// <summary>La velocidad constante que deja el clip con la duración <paramref name="duration"/> en la pista.</summary>
    ICommand RateStretch(string sequenceId, string clipId, Time duration, bool ripple);

    /// <summary>Solo cambia algo en un clip con velocidad: a 1× el tono no se toca.</summary>
    ICommand SetPreservePitch(string sequenceId, string clipId, bool preservePitch);

    /// <summary>Congela el cuadro de <paramref name="at"/>: corta ahí y mete un congelado de <paramref name="duration"/>.</summary>
    ICommand FreezeFrame(string sequenceId, string clipId, Time at, Time duration);
}

public static class SpeedCommands
{
    /// <summary>Las ediciones del núcleo (<see cref="Edits"/>).</summary>
    public static ISpeedCommands Default { get; } = new CoreSpeedCommands();

    private sealed class CoreSpeedCommands : ISpeedCommands
    {
        public ICommand SetSpeed(string sequenceId, string clipId, Rational rate, bool ripple) =>
            Edits.SetSpeed(sequenceId, clipId, rate, ripple);

        // La duración pasa a cuánto se mueve el final al aplicarse, con el clip de ese momento.
        public ICommand RateStretch(string sequenceId, string clipId, Time duration, bool ripple) =>
            new SequenceEditCommand("Cambiar duración", sequenceId, ctx =>
            {
                Time current = ctx.Sequence.FindClip(clipId)?.Clip.Duration ?? throw new EditException("El clip ya no está en la secuencia.");
                TimelineEdits.RateStretch(ctx, clipId, TrimEdge.Out, duration - current, ripple);
            });

        public ICommand SetPreservePitch(string sequenceId, string clipId, bool preservePitch) =>
            Edits.SetPreservePitch(sequenceId, clipId, preservePitch);

        public ICommand FreezeFrame(string sequenceId, string clipId, Time at, Time duration) =>
            Edits.FreezeFrame(sequenceId, clipId, at, duration);
    }
}
