using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services.Inspector;

/// <summary>La velocidad del clip que muestra la pestaña.</summary>
/// <param name="Rate">Constante; con una curva, la media (origen ÷ duración).</param>
/// <param name="SourceDuration">Lo que dura el tramo del origen a 1×.</param>
/// <param name="Duration">Lo que dura en la pista.</param>
/// <param name="HasAudio">El clip o alguno de sus enlazados suena.</param>
/// <param name="IsVideo">Está en una pista de video (solo esos se congelan).</param>
public sealed record SpeedState(
    string ClipId,
    Rational Rate,
    Time SourceDuration,
    Time Duration,
    bool PreservePitch,
    bool IsFrozen,
    bool IsReversed,
    bool HasCurve,
    bool HasAudio,
    bool IsVideo)
{
    public static readonly Rational Normal = new(1, 1);

    /// <summary>Se puede cambiar la velocidad constante (un congelado no tiene).</summary>
    public bool CanChangeRate => !IsFrozen;

    /// <summary>Restablecer cambiaría algo: no está a 1× o tiene una curva (D-024).</summary>
    public bool CanReset => !IsFrozen && (HasCurve || Rate != Normal);

    /// <summary>
    /// «Mantener tono» importa: suena y no va a 1×. La reversa sola no cambia el tono, así que a 1× invertido tampoco.
    /// </summary>
    public bool CanChangePitch => HasAudio && !IsFrozen && (HasCurve || Rate != Normal);

    /// <summary>Se puede invertir o enderezar: todo menos un cuadro congelado.</summary>
    public bool CanReverse => !IsFrozen;
}

/// <summary>
/// Edita la velocidad del clip seleccionado y sus enlazados. Un arrastre del deslizador (<see cref="BeginChange"/>,
/// <see cref="Change"/>, <see cref="EndChange"/>) es un gesto: se ve en vivo y queda como un paso del historial.
/// </summary>
public sealed partial class SpeedEditor
{
    private Drag? _drag;

    public SpeedEditor(EditorSession session, ISpeedCommands commands)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(commands);
        Session = session;
        Commands = commands;
        session.ProjectEdited += OnProjectEdited;
    }

    public EditorSession Session { get; }

    public ISpeedCommands Commands { get; }

    public bool IsChanging => _drag is not null;

    /// <summary>El primer clip seleccionado de video o, si no hay, el primero de audio.</summary>
    public Clip? Target() =>
        (InspectorTargets.First(Session, t => t.Track.Kind == TrackKind.Video) ?? InspectorTargets.First(Session))?.Clip;

    /// <summary>Todos los clips seleccionados que pueden tener velocidad (excluye imágenes fijas y generadores de títulos).</summary>
    public IReadOnlyList<Clip> Targets() =>
        InspectorTargets.Selected(Session, t => t.Clip.Generator is null && Session.Project.FindMedia(t.Clip.MediaId)?.IsStillImage != true)
            .ConvertAll(t => t.Clip);

    /// <summary>Verifica si los clips tienen velocidades o estados de velocidad diferentes.</summary>
    public static bool IsMixed(IReadOnlyList<Clip> clips)
    {
        if (clips.Count < 2)
        {
            return false;
        }

        Rational firstRate = clips[0].Speed?.Rate ?? SpeedState.Normal;
        bool firstCurve = clips[0].Speed?.Curve is not null;
        bool firstFrozen = clips[0].Speed?.Freeze ?? false;
        return clips.Skip(1).Any(c =>
            (c.Speed?.Rate ?? SpeedState.Normal) != firstRate
            || (c.Speed?.Curve is not null) != firstCurve
            || (c.Speed?.Freeze ?? false) != firstFrozen);
    }

    public SpeedState? Read() => Target() is { } clip ? Read(clip) : null;

    public SpeedState Read(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        List<(Track Track, Clip Clip)> group = Session.Sequence.FindClip(clip.Id) is null ? [] : Session.Sequence.GetLinkGroup(clip.Id);
        bool video = group.Exists(g => ReferenceEquals(g.Clip, clip) && g.Track.Kind == TrackKind.Video);
        bool audio = group.Exists(g => g.Track.Kind == TrackKind.Audio);
        ClipSpeed? speed = clip.Speed;
        Rational rate = speed switch
        {
            null => SpeedState.Normal,
            { Curve: not null } => SpeedMath.RateForDuration(clip.SourceDuration, clip.Duration),
            _ => speed.Rate,
        };
        return new SpeedState(
            clip.Id,
            rate,
            clip.SourceDuration,
            clip.Duration,
            speed?.PreservePitch ?? true,
            speed?.Freeze ?? false,
            speed is { Reverse: true, Freeze: false },
            speed?.Curve is not null,
            audio,
            video);
    }

    /// <summary>Se puede congelar el cuadro del cursor: clip de video sin congelar y con el cursor dentro.</summary>
    public bool CanFreeze(Clip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        Time at = Session.EditPosition;
        SpeedState state = Read(clip);
        return state.IsVideo && !state.IsFrozen && clip.Start <= at && at < clip.End;
    }

    /// <summary>Empieza un arrastre del deslizador sobre los clips seleccionados.</summary>
    public void BeginChange()
    {
        EndChange();
        IReadOnlyList<Clip> clips = Targets();
        if (clips.Count == 0)
        {
            return;
        }

        Session.BeginGesture("Cambiar velocidad");
        _drag = new Drag([.. clips.Select(c => c.Id)], SequenceSnapshot.Capture(Session.Sequence), Read(clips[0]), Mixed: IsMixed(clips));
    }

    /// <summary>Aplica la velocidad del arrastre en curso; <c>false</c> si no se pudo (y la sesión ya avisó).</summary>
    public bool Change(Rational rate)
    {
        if (_drag is not { Failed: false } drag || drag.ClipIds.Count == 0)
        {
            return false;
        }

        if (drag.ClipIds.Count == 1 && Session.Sequence.FindClip(drag.ClipIds[0]) is { } found
            && Read(found.Clip) is { HasCurve: false } state && state.Rate == rate)
        {
            return true;
        }

        // Cada paso parte del estado del inicio del gesto: encadenarlos llevaría el final al cuadro una y otra vez y el
        // material iría variando. Volver a la velocidad de partida es dejar el clip como estaba, salvo con varios clips de
        // velocidades distintas: ahí la del primero es un cambio para los demás.
        ICommand? step = drag.StartState is { HasCurve: false } start && start.Rate == rate && !drag.Mixed
            ? null
            : Commands.SetSpeed(Session.SequenceId, drag.ClipIds, rate, Session.SpeedScope);

        // Si un paso falla (pista bloqueada, largo máximo), el resto del arrastre no insiste ni repite el aviso.
        drag.Failed = !Session.Execute(new FromGestureStart(Session.SequenceId, drag.Start, step));
        return !drag.Failed;
    }

    public void EndChange()
    {
        EndPointDrag();
        if (_drag is not { } drag)
        {
            return;
        }

        _drag = null;
        if (drag.Grouped)
        {
            Session.EndGesture();
        }
    }

    /// <summary>Una velocidad escrita o de un botón rápido: un paso del historial.</summary>
    public bool SetRate(Rational rate)
    {
        EndChange();
        IReadOnlyList<Clip> clips = Targets();
        if (clips.Count == 0)
        {
            return false;
        }

        if (clips.Count == 1 && Read(clips[0]) is { HasCurve: false } state && state.Rate == rate)
        {
            return false;
        }

        List<string> ids = [.. clips.Select(c => c.Id)];
        Session.BreakMerge();
        bool done = Session.Execute(Commands.SetSpeed(Session.SequenceId, ids, rate, Session.SpeedScope));
        Session.BreakMerge();
        return done;
    }

    /// <summary>
    /// Vuelve a 1× en un paso del historial (D-024). Si algún clip tiene una curva, antes pregunta con
    /// <paramref name="confirmCurveRemoval"/>; si se cancela, no cambia nada. La reversa y «Mantener tono» se quedan.
    /// </summary>
    public async Task<bool> ResetAsync(Func<Task<bool>> confirmCurveRemoval)
    {
        ArgumentNullException.ThrowIfNull(confirmCurveRemoval);
        EndChange();
        IReadOnlyList<Clip> clips = Targets();
        if (clips.Count == 0 || !clips.Any(c => Read(c).CanReset))
        {
            return false;
        }

        bool hasCurve = clips.Any(c => Read(c).HasCurve);
        if (hasCurve && !await confirmCurveRemoval())
        {
            return false;
        }

        IReadOnlyList<Clip> currentClips = Targets();
        List<string> ids = [.. currentClips.Where(c => Read(c).CanReset).Select(c => c.Id)];
        if (ids.Count == 0)
        {
            return false;
        }

        Session.BreakMerge();
        bool done = Session.Execute(Commands.SetSpeed(Session.SequenceId, ids, SpeedState.Normal, Session.SpeedScope));
        Session.BreakMerge();
        return done;
    }

    /// <summary>Una duración escrita: la velocidad sale de ella. Un paso del historial.</summary>
    public bool SetDuration(Time duration)
    {
        EndChange();
        if (Target() is not { } clip || duration <= Time.Zero || clip.Duration == duration || Read(clip).IsFrozen)
        {
            return false;
        }

        Session.BreakMerge();
        bool done = Session.Execute(Commands.RateStretch(Session.SequenceId, clip.Id, duration, Session.SpeedScope));
        Session.BreakMerge();
        return done;
    }

    public bool SetPreservePitch(bool preservePitch)
    {
        EndChange();
        if (Target() is not { } clip || Read(clip) is not { CanChangePitch: true } state || state.PreservePitch == preservePitch)
        {
            return false;
        }

        Session.BreakMerge();
        bool done = Session.Execute(Commands.SetPreservePitch(Session.SequenceId, clip.Id, preservePitch));
        Session.BreakMerge();
        return done;
    }

    /// <summary>Invierte o endereza el clip y sus enlazados en un paso del historial; no cambia duración ni material.</summary>
    public bool SetReverse(bool reverse)
    {
        EndChange();
        if (Target() is not { } clip || Read(clip) is not { CanReverse: true } state || state.IsReversed == reverse)
        {
            return false;
        }

        Session.BreakMerge();
        bool done = Session.Execute(Commands.SetReverse(Session.SequenceId, clip.Id, reverse));
        Session.BreakMerge();
        return done;
    }

    /// <summary>Congela el cuadro del cursor durante <see cref="SpeedTabValues.FreezeDuration"/>.</summary>
    public bool Freeze()
    {
        EndChange();
        if (Target() is not { } clip || !CanFreeze(clip))
        {
            return false;
        }

        ICommand command = Commands.FreezeFrame(Session.SequenceId, clip.Id, Session.EditPosition, SpeedTabValues.FreezeDuration, Session.SpeedScope);
        if (!Session.Execute(command))
        {
            return false;
        }

        // Como Mayús+R: queda seleccionado el congelado, que es lo que se suele retocar después.
        if (command is SequenceEditCommand { CreatedIds.Count: > 0 } created)
        {
            Session.Select(created.CreatedIds);
        }

        return true;
    }

    // Esc revirtió el gesto: el resto del arrastre no hace nada y al soltar no hay grupo que cerrar.
    private void OnProjectEdited(object? sender, ProjectEditedEventArgs e)
    {
        if (e.Kind == EditKind.GestureCancelled && _drag is { Grouped: true } drag)
        {
            _drag = drag with { Grouped = false, Failed = true };
        }

        if (e.Kind == EditKind.GestureCancelled)
        {
            _pointDrag = null;
        }
    }

    private sealed record Drag(IReadOnlyList<string> ClipIds, SequenceSnapshot Start, SpeedState StartState, bool Grouped = true, bool Mixed = false)
    {
        public bool Failed { get; set; }
    }
}
