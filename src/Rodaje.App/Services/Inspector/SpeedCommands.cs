using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services.Inspector;

/// <summary>
/// Las ediciones de velocidad que usa la pestaña Velocidad. Cada una cambia el clip y sus enlazados en un solo paso y corre
/// lo que viene detrás según cambie la duración.
/// </summary>
/// <remarks>
/// Es la costura con las ediciones de velocidad del núcleo (<c>Edits.SetSpeed</c>, <c>FreezeFrame</c>, <c>RateStretch</c>
/// de la fase 5c): <see cref="SpeedCommands.Default"/> las llama cuando existen; las pruebas usan un doble.
/// </remarks>
public interface ISpeedCommands
{
    /// <summary>Velocidad constante <paramref name="rate"/>; a 1× sin curva ni reversa el clip vuelve a no tener velocidad.</summary>
    ICommand SetSpeed(string sequenceId, string clipId, Rational rate);

    /// <summary>La velocidad constante que deja el clip con la duración <paramref name="duration"/> en la pista.</summary>
    ICommand RateStretch(string sequenceId, string clipId, Time duration);

    ICommand SetPreservePitch(string sequenceId, string clipId, bool preservePitch);

    /// <summary>Congela el cuadro de <paramref name="at"/>: corta ahí y mete un congelado de <paramref name="duration"/>.</summary>
    ICommand FreezeFrame(string sequenceId, string clipId, Time at, Time duration);
}

public static class SpeedCommands
{
    /// <summary>Las ediciones que usa la aplicación.</summary>
    public static ISpeedCommands Default { get; } = new ProvisionalSpeedCommands();
}

/// <summary>
/// Versión mínima mientras el núcleo no trae sus ediciones de velocidad: cambia la velocidad del clip y de sus enlazados,
/// corre su pista lo que cambió la duración y valida. Congelar todavía no está.
/// </summary>
/// <remarks>Al unir la ronda 5c.1 se sustituye por las fábricas de <c>Edits</c>; las pruebas de la pestaña no cambian.</remarks>
internal sealed class ProvisionalSpeedCommands : ISpeedCommands
{
    public ICommand SetSpeed(string sequenceId, string clipId, Rational rate) =>
        Retime(sequenceId, clipId, "Cambiar velocidad", _ => speed => speed.Rate = rate);

    public ICommand RateStretch(string sequenceId, string clipId, Time duration) =>
        Retime(sequenceId, clipId, "Cambiar duración", target =>
        {
            Rational rate = SpeedMath.RateForDuration(target.SourceDuration, duration);
            return speed => speed.Rate = rate;
        });

    public ICommand SetPreservePitch(string sequenceId, string clipId, bool preservePitch) =>
        Retime(sequenceId, clipId, preservePitch ? "Mantener tono" : "No mantener tono", _ => speed => speed.PreservePitch = preservePitch);

    public ICommand FreezeFrame(string sequenceId, string clipId, Time at, Time duration)
    {
        Action<EditContext> pending = _ => throw new EditException("Congelar llega con las ediciones de velocidad del núcleo.");
        return new SequenceEditCommand("Congelar cuadro", sequenceId, pending);
    }

    // El cambio se decide con el clip pedido y se aplica igual a todo su grupo enlazado.
    private static SequenceEditCommand Retime(string sequenceId, string clipId, string description, Func<Clip, Action<ClipSpeed>> changeFor) =>
        new(description, sequenceId, ctx =>
        {
            if (ctx.Sequence.FindClip(clipId) is not { } target)
            {
                throw new EditException("El clip ya no está en la secuencia.");
            }

            Action<ClipSpeed> change = changeFor(target.Clip);
            foreach ((Track track, Clip clip) in ctx.Sequence.GetLinkGroup(clipId))
            {
                EditContext.EnsureUnlocked(track);
                if (clip.Speed is { Freeze: true })
                {
                    throw new EditException("Un cuadro congelado no cambia de velocidad.");
                }

                Time oldEnd = clip.End;
                ClipSpeed speed = clip.Speed?.Clone() ?? new ClipSpeed();
                change(speed);
                speed.Curve = null;
                speed.Offset = Time.Zero;
                clip.Speed = speed.IsIdentity && speed.PreservePitch ? null : speed;
                try
                {
                    ClipTimeMap.Fit(clip.Start, clip.SourceIn, clip.SourceOut, clip.Speed).ApplyTo(clip);
                }
                catch (ArgumentException)
                {
                    throw new EditException("A esa velocidad el clip quedaría más largo de lo que admite (unas 6,9 horas) o más corto que un instante.");
                }

                EditContext.ShiftClips(track, oldEnd, clip.End - oldEnd, new HashSet<Clip> { clip });
            }

            ctx.EnsureValid();
        }, mergeKey: $"speed:{clipId}");
}
