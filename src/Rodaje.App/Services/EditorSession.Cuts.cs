using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services;

/// <summary>
/// Cortar en el cursor en todas las pistas y borrar a la izquierda o a la derecha del cursor (Q/W), cada uno en un paso
/// del historial. Borrar a un lado actúa sobre los mismos clips que <see cref="TrimStartToPlayhead"/>: los seleccionados
/// que pasan por el cursor o, sin selección, el de la pista principal.
/// </summary>
public sealed partial class EditorSession
{
    /// <summary>Corta en el cursor todos los clips de las pistas desbloqueadas que pasan por él.</summary>
    public bool SplitAllTracksAtPlayhead()
    {
        VerifyAccess();
        Time at = EditPosition;
        if (!TimelineEdits.CanSplitAllTracks(Sequence, at))
        {
            return false;
        }

        return Execute(Edits.SplitAllTracks(SequenceId, at, _ids));
    }

    /// <summary>Borra lo que hay antes del cursor en los clips (Q en el estilo Rodaje).</summary>
    public bool DeleteLeftOfPlayhead() => DeleteSideOfPlayhead(TrimEdge.In);

    /// <summary>Borra lo que hay después del cursor en los clips (W en el estilo Rodaje).</summary>
    public bool DeleteRightOfPlayhead() => DeleteSideOfPlayhead(TrimEdge.Out);

    /// <summary>
    /// Un grupo enlazado cierra el hueco si alguno de sus clips está en una pista que no deja huecos (la principal
    /// magnética, o todas con ripple automático); así el video y su audio seleccionados juntos se recortan una sola vez.
    /// Primero se recortan los que dejan hueco, que no corren nada, y luego los que corren lo que sigue.
    /// </summary>
    private bool DeleteSideOfPlayhead(TrimEdge side)
    {
        VerifyAccess();
        Time at = EditPosition;
        List<string> targets = ClipsAtPlayhead(at);
        if (targets.Count == 0)
        {
            return false;
        }

        Sequence sequence = Sequence;
        List<string> ripple = targets.FindAll(id => sequence.GetLinkGroup(id).Exists(m => ClosesGaps(m.Track)));
        List<string> lift = targets.FindAll(id => !ripple.Contains(id));
        var commands = new List<ICommand>(2);
        if (lift.Count > 0)
        {
            commands.Add(Edits.DeleteToPoint(SequenceId, lift, at, side, ripple: false, Timeline.RippleScope, _ids));
        }

        if (ripple.Count > 0)
        {
            commands.Add(Edits.DeleteToPoint(SequenceId, ripple, at, side, ripple: true, Timeline.RippleScope, _ids));
        }

        // Con ripple el inicio recortado queda donde estaba el clip, y el cursor va ahí para seguir viendo lo mismo.
        Time? seekTo = side == TrimEdge.In && ripple.Count > 0 ? sequence.GetClip(ripple[0]).Clip.Start : null;
        string description = side == TrimEdge.In ? "Borrar a la izquierda del cursor" : "Borrar a la derecha del cursor";
        bool done = commands.Count == 1 ? Execute(commands[0]) : Execute(new CompositeCommand(description, commands));
        if (done && seekTo is { } position)
        {
            Seek(position);
        }

        return done;
    }
}
