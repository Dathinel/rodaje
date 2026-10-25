using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services;

/// <summary>Las ediciones en el cursor que usan los atajos y la barra de la línea de tiempo.</summary>
/// <remarks>
/// Actúan sobre los clips seleccionados que pasan por el cursor; sin selección, sobre el clip de la pista principal que
/// está bajo el cursor. En la pista principal magnética, borrar y recortar no dejan huecos.
/// </remarks>
public sealed partial class EditorSession
{
    /// <summary>El cursor llevado al inicio del cuadro más cercano de la secuencia: ahí se corta y se recorta.</summary>
    public Time EditPosition => ProgramPosition.SnapToFrame(Sequence.FrameRate);

    /// <summary>Corta en el cursor; los enlazados se cortan con su clip.</summary>
    public bool SplitAtPlayhead()
    {
        VerifyAccess();
        Time at = EditPosition;
        List<string> targets = ClipsAtPlayhead(at);
        if (targets.Count == 0)
        {
            return false;
        }

        return RunAsOneStep("Cortar clip", targets, id =>
            Sequence.FindClip(id) is { } found && found.Clip.Start < at && at < found.Clip.End
                ? Edits.Split(SequenceId, id, at, _ids)
                : null);
    }

    /// <summary>
    /// Borra los clips seleccionados con sus enlazados. En la pista principal magnética (o con ripple automático) se
    /// cierra el hueco; en las demás queda. Con una transición seleccionada, borra solo la transición.
    /// </summary>
    public bool DeleteSelection()
    {
        VerifyAccess();
        if (DeleteSelectedTransition())
        {
            return true;
        }

        Sequence sequence = Sequence;
        List<string> existing = _selection.FindAll(id => sequence.FindClip(id) is not null);
        if (existing.Count == 0)
        {
            return false;
        }

        // Un grupo enlazado va entero a un lado (como en DeleteSideOfPlayhead): si el ripple del video borrara también su
        // audio, el lift de ese audio ya no lo encontraría y se revertiría todo.
        List<string> ripple = existing.FindAll(id => sequence.GetLinkGroup(id).Exists(m => ClosesGaps(m.Track)));
        List<string> lift = existing.FindAll(id => !ripple.Contains(id));
        var commands = new List<ICommand>(2);
        if (lift.Count > 0)
        {
            commands.Add(Edits.Lift(SequenceId, lift, _ids));
        }

        if (ripple.Count > 0)
        {
            commands.Add(Edits.RippleDelete(SequenceId, ripple, Timeline.RippleScope, _ids));
        }

        bool done = commands.Count == 1 ? Execute(commands[0]) : Execute(new CompositeCommand("Borrar", commands));
        if (done)
        {
            ClearSelection();
        }

        return done;
    }

    public void SelectAll()
    {
        VerifyAccess();
        Select(Sequence.AllTracks.SelectMany(t => t.Clips).Select(c => c.Id));
    }

    private bool ClosesGaps(Track track) =>
        Timeline.AutoRipple || (Timeline.MagneticMainTrack && track.Kind == TrackKind.Video && ReferenceEquals(track, MainTrack));

    /// <summary>Los seleccionados que pasan por el instante; sin selección, el clip de la pista principal en ese instante.</summary>
    private List<string> ClipsAtPlayhead(Time at)
    {
        Sequence sequence = Sequence;
        if (_selection.Count > 0)
        {
            return _selection.FindAll(id => sequence.FindClip(id) is { } found && found.Clip.Start < at && at < found.Clip.End);
        }

        return MainTrack?.ClipAt(at) is { } clip && clip.Start < at ? [clip.Id] : [];
    }

    /// <summary>Una edición por clip, todas en un paso del historial; cada una se arma con el estado que dejó la anterior.</summary>
    private bool RunAsOneStep(string description, List<string> clipIds, Func<string, ICommand?> edit)
    {
        BeginGesture(description);
        bool any = false;
        try
        {
            foreach (string id in clipIds)
            {
                if (edit(id) is { } command)
                {
                    any |= Execute(command);
                }
            }
        }
        finally
        {
            EndGesture();
        }

        return any;
    }
}
