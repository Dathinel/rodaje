using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services;

/// <summary>
/// La velocidad desde los atajos: abrir su pestaña, más rápido y más lento por escalones, congelar el cuadro del cursor e
/// invertir. Actúan sobre los clips seleccionados (uno por grupo enlazado); sin selección, sobre el clip de la pista
/// principal bajo el cursor. Con magnetismo en cualquier pista, lo que sigue se une con el modo de ripple de la sesión.
/// </summary>
public sealed partial class EditorSession
{
    /// <summary>Duración del cuadro congelado que crea <see cref="FreezeFrameAtPlayhead"/>.</summary>
    public static readonly Time FreezeDuration = Time.FromSeconds(3);

    /// <summary>Los escalones de «Más rápido» y «Más lento».</summary>
    public static IReadOnlyList<Rational> SpeedSteps { get; } =
    [
        new(1, 10), new(1, 5), new(1, 4), new(1, 2), new(3, 4), new(1, 1), new(5, 4), new(3, 2),
        new(2, 1), new(3, 1), new(4, 1), new(5, 1), new(8, 1), new(10, 1), new(20, 1), new(50, 1), new(100, 1),
    ];

    /// <summary>
    /// Se pidió la pestaña «Velocidad» (Ctrl+R); la abre el inspector. Antes se selecciona el clip si no lo estaba.
    /// </summary>
    public event EventHandler? SpeedPanelRequested;

    /// <summary>Selecciona el clip del cursor si no había selección y pide la pestaña «Velocidad».</summary>
    public bool ShowClipSpeed()
    {
        VerifyAccess();
        List<string> targets = SpeedTargets();
        if (targets.Count == 0)
        {
            return false;
        }

        if (_selection.Count == 0)
        {
            Select(targets);
        }

        SpeedPanelRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Pone la misma velocidad constante a los clips (uno por grupo enlazado), en un paso del historial.</summary>
    public bool SetSpeed(IEnumerable<string> clipIds, Rational rate)
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(clipIds);
        List<string> targets = OnePerLink([.. clipIds]);
        return targets.Count > 0 && RunAsOneStep("Cambiar velocidad", targets, id =>
            Sequence.FindClip(id) is { } found && Retimable(found.Clip)
                ? Edits.SetSpeed(SequenceId, id, rate, SpeedScope, _ids)
                : null);
    }

    /// <summary>Sube (o baja) un escalón de <see cref="SpeedSteps"/> la velocidad de cada clip.</summary>
    public bool StepSpeed(bool faster)
    {
        VerifyAccess();
        List<string> targets = SpeedTargets();
        return targets.Count > 0 && RunAsOneStep(faster ? "Más rápido" : "Más lento", targets, id =>
        {
            if (Sequence.FindClip(id) is not { } found || !Retimable(found.Clip))
            {
                return null;
            }

            Rational current = found.Clip.Speed?.Rate ?? new Rational(1, 1);
            return NextStep(current, faster) is { } next
                ? Edits.SetSpeed(SequenceId, id, next, SpeedScope, _ids)
                : null;
        });
    }

    /// <summary>
    /// Congela durante <see cref="FreezeDuration"/> el cuadro del cursor del primer clip de video que pasa por él y
    /// selecciona el cuadro congelado (Mayús+R).
    /// </summary>
    public bool FreezeFrameAtPlayhead()
    {
        VerifyAccess();
        Time at = EditPosition;
        Sequence sequence = Sequence;
        IEnumerable<string> candidates = _selection.Count > 0
            ? _selection
            : MainTrack?.ClipAt(at) is { } under ? [under.Id] : [];
        foreach (string id in candidates)
        {
            if (sequence.FindClip(id) is not { } found || found.Track.Kind != TrackKind.Video || found.Clip.Speed is { Freeze: true } || found.Clip.Generator is not null
                || at < found.Clip.Start || at >= found.Clip.End)
            {
                continue;
            }

            SequenceEditCommand command = Edits.FreezeFrame(SequenceId, id, at, FreezeDuration, _ids, SpeedScope);
            if (!Execute(command))
            {
                return false;
            }

            Select(command.CreatedIds);
            return true;
        }

        return false;
    }

    /// <summary>Invierte los clips; si todos ya lo estaban, los vuelve a poner hacia adelante.</summary>
    public bool ToggleReverse()
    {
        VerifyAccess();
        List<string> targets = SpeedTargets().FindAll(id => Sequence.FindClip(id) is { } found && found.Clip.Generator is null && found.Clip.Speed is not { Freeze: true });
        if (targets.Count == 0)
        {
            return false;
        }

        bool reverse = !targets.TrueForAll(id => Sequence.GetClip(id).Clip.Speed is { Reverse: true });
        return RunAsOneStep(reverse ? "Invertir" : "Quitar invertir", targets, id => Edits.SetReverse(SequenceId, id, reverse, _ids));
    }

    /// <summary>El escalón siguiente (o anterior) a <paramref name="current"/>; <c>null</c> en el extremo.</summary>
    public static Rational? NextStep(Rational current, bool faster)
    {
        double value = current.ToDouble();
        if (faster)
        {
            foreach (Rational step in SpeedSteps)
            {
                if (step.ToDouble() > value + 1e-9)
                {
                    return step;
                }
            }

            return null;
        }

        for (int i = SpeedSteps.Count - 1; i >= 0; i--)
        {
            if (SpeedSteps[i].ToDouble() < value - 1e-9)
            {
                return SpeedSteps[i];
            }
        }

        return null;
    }

    /// <summary>
    /// Qué se corre al cambiar la velocidad: siempre hay magnetismo, en cualquier pista (decisión de Daniel); el ámbito es
    /// el modo de ripple de la sesión.
    /// </summary>
    public RippleScope SpeedScope => Timeline.RippleScope;

    // Un título no tiene material: ni velocidad, ni congelar, ni invertir.
    private static bool Retimable(Clip clip) => clip.Generator is null && clip.Speed is not { Freeze: true } and not { Curve: not null };

    /// <summary>Los seleccionados (uno por grupo enlazado); sin selección, el clip de la pista principal bajo el cursor.</summary>
    private List<string> SpeedTargets()
    {
        if (_selection.Count > 0)
        {
            return OnePerLink(_selection);
        }

        return MainTrack?.ClipAt(EditPosition) is { } clip ? [clip.Id] : [];
    }

    private List<string> OnePerLink(List<string> clipIds)
    {
        Sequence sequence = Sequence;
        var links = new HashSet<string>(StringComparer.Ordinal);
        return clipIds.FindAll(id => sequence.FindClip(id) is { } found && (found.Clip.LinkId is not { } link || links.Add(link)));
    }
}
