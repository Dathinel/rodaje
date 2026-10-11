using Rodaje.Core.Commands;
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
}

/// <summary>
/// Edita la velocidad del clip seleccionado y sus enlazados. Un arrastre del deslizador (<see cref="BeginChange"/>,
/// <see cref="Change"/>, <see cref="EndChange"/>) es un gesto: se ve en vivo y queda como un paso del historial.
/// </summary>
public sealed class SpeedEditor
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
    public Clip? Target()
    {
        Sequence sequence = Session.Sequence;
        Clip? audio = null;
        foreach (string id in Session.SelectedClipIds)
        {
            if (sequence.FindClip(id) is { } found)
            {
                if (found.Track.Kind == TrackKind.Video)
                {
                    return found.Clip;
                }

                audio ??= found.Clip;
            }
        }

        return audio;
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

    /// <summary>Empieza un arrastre del deslizador sobre el clip de ahora.</summary>
    public void BeginChange()
    {
        EndChange();
        if (Target() is not { } clip)
        {
            return;
        }

        Session.BeginGesture("Cambiar velocidad");
        _drag = new Drag(clip.Id);
    }

    /// <summary>Aplica la velocidad del arrastre en curso; <c>false</c> si no se pudo (y la sesión ya avisó).</summary>
    public bool Change(Rational rate)
    {
        if (_drag is not { Failed: false } drag || Session.Sequence.FindClip(drag.ClipId) is not { } found)
        {
            return false;
        }

        if (Read(found.Clip) is { HasCurve: false } state && state.Rate == rate)
        {
            return true;
        }

        // Si un paso falla (pista bloqueada, largo máximo), el resto del arrastre no insiste ni repite el aviso.
        drag.Failed = !Session.Execute(Commands.SetSpeed(Session.SequenceId, drag.ClipId, rate));
        return !drag.Failed;
    }

    public void EndChange()
    {
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
        if (Target() is not { } clip || Read(clip) is { HasCurve: false } state && state.Rate == rate)
        {
            return false;
        }

        Session.BreakMerge();
        bool done = Session.Execute(Commands.SetSpeed(Session.SequenceId, clip.Id, rate));
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
        bool done = Session.Execute(Commands.RateStretch(Session.SequenceId, clip.Id, duration));
        Session.BreakMerge();
        return done;
    }

    public bool SetPreservePitch(bool preservePitch)
    {
        EndChange();
        if (Target() is not { } clip || Read(clip).PreservePitch == preservePitch)
        {
            return false;
        }

        Session.BreakMerge();
        bool done = Session.Execute(Commands.SetPreservePitch(Session.SequenceId, clip.Id, preservePitch));
        Session.BreakMerge();
        return done;
    }

    /// <summary>Congela el cuadro del cursor durante <see cref="SpeedPresets.FreezeDuration"/>.</summary>
    public bool Freeze()
    {
        EndChange();
        if (Target() is not { } clip || !CanFreeze(clip))
        {
            return false;
        }

        return Session.Execute(Commands.FreezeFrame(Session.SequenceId, clip.Id, Session.EditPosition, SpeedPresets.FreezeDuration));
    }

    // Esc revirtió el gesto: el resto del arrastre no hace nada y al soltar no hay grupo que cerrar.
    private void OnProjectEdited(object? sender, ProjectEditedEventArgs e)
    {
        if (e.Kind == EditKind.GestureCancelled && _drag is { Grouped: true } drag)
        {
            _drag = drag with { Grouped = false, Failed = true };
        }
    }

    private sealed record Drag(string ClipId, bool Grouped = true)
    {
        public bool Failed { get; set; }
    }
}
