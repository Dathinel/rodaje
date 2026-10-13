using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services.Inspector;

/// <summary>Una transición encontrada en la secuencia, con los dos clips de su corte.</summary>
public sealed record TransitionTarget(Track Track, TransitionInstance Transition, Clip From, Clip To);

/// <summary>
/// Edita la transición seleccionada con los comandos del núcleo (<c>Edits.SetTransition*</c>): tipo, duración,
/// parámetros y opciones. La seleccionada es <see cref="EditorSession.SelectedTransitionId"/>.
/// </summary>
public sealed class TransitionEditor(EditorSession session)
{
    /// <summary>Duración más larga que ofrece el deslizador; escrita a mano puede ser más.</summary>
    public const double MaxSeconds = 10;

    public EditorSession Session { get; } = session ?? throw new ArgumentNullException(nameof(session));

    /// <summary>Tipos que se eligen en el desplegable (las de paquete se eligen por su preajuste).</summary>
    public static IReadOnlyList<EffectDefinition> BuiltInTypes { get; } =
        [.. EffectRegistry.Transitions.Where(t => t.Type != BuiltInTransitions.Preset)];

    /// <summary>Los tipos que van en una pista de esa clase: las de audio en audio y las de imagen en video.</summary>
    public static IReadOnlyList<EffectDefinition> TypesFor(TrackKind kind) =>
        [.. BuiltInTypes.Where(t => BuiltInTransitions.IsAudio(t.Type) == (kind == TrackKind.Audio))];

    /// <summary>A qué transiciones se copia la duración con «Aplicar a…».</summary>
    public enum DurationScope
    {
        Track,
        Sequence,
    }

    /// <summary>La transición seleccionada, si sigue en la secuencia.</summary>
    public TransitionTarget? Selected() => Session.SelectedTransitionId is { } id ? Find(id) : null;

    public TransitionTarget? Find(string transitionId)
    {
        ArgumentNullException.ThrowIfNull(transitionId);
        foreach (Track track in Session.Sequence.AllTracks)
        {
            if (track.FindTransition(transitionId) is { } transition
                && track.Clips.Find(c => c.Id == transition.From) is { } from
                && track.Clips.Find(c => c.Id == transition.To) is { } to)
            {
                return new TransitionTarget(track, transition, from, to);
            }
        }

        return null;
    }

    /// <summary>Valor del parámetro, o el de fábrica si no se cambió.</summary>
    public static double Read(TransitionInstance transition, string name)
    {
        ArgumentNullException.ThrowIfNull(transition);
        return transition.Parameters.TryGetValue(name, out double value) ? value : EffectRegistry.DefaultValue(transition.Type, name);
    }

    public static string ReadOption(TransitionInstance transition, string name)
    {
        ArgumentNullException.ThrowIfNull(transition);
        return transition.Options.TryGetValue(name, out string? value) ? value : EffectRegistry.DefaultOption(transition.Type, name);
    }

    /// <summary>Rango de la duración en segundos: de un cuadro al tope del núcleo (el clip más corto), hasta 10 s.</summary>
    public (double Minimum, double Maximum) DurationRange(TransitionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        double frame = Time.FromFrames(1, Session.Sequence.FrameRate).ToSeconds();
        double longest = TransitionOps.MaxDuration(target.Track, target.Transition).ToSeconds();
        return (frame, Math.Max(frame, Math.Min(MaxSeconds, longest)));
    }

    public bool SetType(string transitionId, string type) =>
        Find(transitionId) is { } target && target.Transition.Type != type
        && Session.Execute(Edits.SetTransitionType(Session.SequenceId, transitionId, type));

    /// <summary>Usa la transición de paquete <paramref name="reference"/>: tipo y preajuste en un solo paso.</summary>
    public bool SetPreset(string transitionId, string reference)
    {
        ArgumentException.ThrowIfNullOrEmpty(reference);
        if (Find(transitionId) is not { } target
            || (target.Transition.Type == BuiltInTransitions.Preset && ReadOption(target.Transition, BuiltInTransitions.PresetOptions.Preset) == reference))
        {
            return false;
        }

        return Session.Execute(PresetCommand(Session.SequenceId, transitionId, reference));
    }

    /// <summary>Se funde con el cambio anterior de la misma transición: un deslizador arrastrado deja un paso.</summary>
    public bool SetDuration(string transitionId, double seconds)
    {
        if (!double.IsFinite(seconds) || Find(transitionId) is not { } target)
        {
            return false;
        }

        Time duration = Time.FromSeconds(seconds).SnapToFrame(Session.Sequence.FrameRate);
        if (duration <= Time.Zero)
        {
            duration = Time.FromFrames(1, Session.Sequence.FrameRate);
        }

        return duration != target.Transition.Duration
            && Session.Execute(Edits.SetTransitionDuration(Session.SequenceId, transitionId, duration));
    }

    public bool SetParameter(string transitionId, string name, double value) =>
        Find(transitionId) is { } target && Math.Abs(Read(target.Transition, name) - value) > 1e-12
        && Session.Execute(Edits.SetTransitionParameter(Session.SequenceId, transitionId, name, value));

    public bool SetOption(string transitionId, string name, string value) =>
        Find(transitionId) is { } target && ReadOption(target.Transition, name) != value
        && Session.Execute(Edits.SetTransitionOption(Session.SequenceId, transitionId, name, value));

    /// <summary>Un color (tres parámetros de 0 a 1) en un solo paso; se funde con el cambio anterior del mismo color.</summary>
    public bool SetColor(string transitionId, string red, string green, string blue, double r, double g, double b)
    {
        if (Find(transitionId) is not { } target
            || (Math.Abs(Read(target.Transition, red) - r) < 1e-9 && Math.Abs(Read(target.Transition, green) - g) < 1e-9
                && Math.Abs(Read(target.Transition, blue) - b) < 1e-9))
        {
            return false;
        }

        return Session.Execute(new SequenceEditCommand("Cambiar color de la transición", Session.SequenceId, ctx =>
        {
            TransitionEdits.SetParameter(ctx, transitionId, red, r);
            TransitionEdits.SetParameter(ctx, transitionId, green, g);
            TransitionEdits.SetParameter(ctx, transitionId, blue, b);
        }, mergeKey: $"transition-color:{transitionId}:{red}"));
    }

    public bool SetAlignment(string transitionId, TransitionAlignment alignment) =>
        Find(transitionId) is { } target && target.Transition.Alignment != alignment
        && Session.Execute(Edits.SetTransitionAlignment(Session.SequenceId, transitionId, alignment));

    /// <summary>Se funde con el cambio anterior de la curva de la misma transición: arrastrar un manejador deja un paso.</summary>
    public bool SetCurve(string transitionId, Interpolation? curve, BezierEase? ease = null)
    {
        if (Find(transitionId) is not { } target)
        {
            return false;
        }

        Interpolation? normalized = curve == Interpolation.Linear ? null : curve;
        BezierEase? wanted = normalized == Interpolation.Bezier ? (ease ?? target.Transition.Ease ?? BezierEase.Smooth).Clamped() : null;
        return (target.Transition.Curve != normalized || target.Transition.Ease != wanted)
            && Session.Execute(Edits.SetTransitionCurve(Session.SequenceId, transitionId, normalized, wanted));
    }

    /// <summary>
    /// La duración de <paramref name="transitionId"/> en las demás de su pista o de la secuencia, cada una acotada a sus
    /// clips, en un paso; devuelve cuántas cambió.
    /// </summary>
    public int ApplyDuration(string transitionId, DurationScope scope)
    {
        if (Find(transitionId) is not { } target)
        {
            return 0;
        }

        IEnumerable<Track> tracks = scope == DurationScope.Track ? [target.Track] : Session.Sequence.AllTracks;
        List<string> ids = [.. tracks.Where(t => !t.Locked).SelectMany(t => t.Transitions)
            .Where(t => t.Duration != target.Transition.Duration).Select(t => t.Id)];
        if (ids.Count == 0)
        {
            return 0;
        }

        return Session.Execute(Edits.SetTransitionDurations(Session.SequenceId, ids, target.Transition.Duration)) ? ids.Count : 0;
    }

    public bool Remove(string transitionId) =>
        Find(transitionId) is not null && Session.Execute(Edits.RemoveTransition(Session.SequenceId, transitionId));

    /// <summary>Cambia una transición al preajuste de paquete <paramref name="reference"/> en un solo comando.</summary>
    public static SequenceEditCommand PresetCommand(string sequenceId, string transitionId, string reference) =>
        new("Cambiar transición", sequenceId, ctx =>
        {
            TransitionEdits.SetType(ctx, transitionId, BuiltInTransitions.Preset);
            TransitionEdits.SetOption(ctx, transitionId, BuiltInTransitions.PresetOptions.Preset, reference);
        });
}
