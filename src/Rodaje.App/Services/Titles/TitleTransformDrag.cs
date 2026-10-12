using Rodaje.App.Services.Inspector;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Playback.Composition;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.App.Services.Titles;

/// <summary>Qué hace el arrastre de un asa del título.</summary>
public enum TitleGesture
{
    Move,
    Scale,
    Rotate,
}

/// <summary>
/// Mover, escalar o girar un título con las asas del visor: un gesto (un paso del historial, Esc lo revierte) que cambia
/// la posición, la escala o el giro de <c>rodaje.transform</c> en el cursor; con keyframes, crea o ajusta el del cursor
/// como la pestaña Propiedades. En plena entrada o salida el título se ve desplazado por la animación del estilo, un delta
/// que gira y se escala con el clip: el gesto trabaja sobre lo que se ve, así el título no salta al escalarlo o girarlo.
/// </summary>
public sealed class TitleTransformDrag
{
    private readonly EditorSession _session;
    private readonly string _clipId;
    private readonly string _effectId;
    private readonly Time _at;
    private readonly Time _atScale;
    private readonly Time _atRotation;
    private readonly double _offsetX;
    private readonly double _offsetY;
    private bool _open = true;

    private TitleTransformDrag(EditorSession session, string clipId, string effectId, (Time Position, Time Scale, Time Rotation) at,
        (double X, double Y, double Scale, double Rotation) start, (double X, double Y) offset)
    {
        _session = session;
        _clipId = clipId;
        _effectId = effectId;
        _at = at.Position;
        _atScale = at.Scale;
        _atRotation = at.Rotation;
        StartX = start.X;
        StartY = start.Y;
        StartScale = start.Scale;
        StartRotation = start.Rotation;
        _offsetX = offset.X;
        _offsetY = offset.Y;
    }

    /// <summary>Posición, escala y giro del clip en el cursor al empezar (sin la animación del estilo).</summary>
    public double StartX { get; }

    public double StartY { get; }

    public double StartScale { get; }

    public double StartRotation { get; }

    /// <summary>
    /// Empieza el gesto sobre el título; nulo si el clip no es un título o el cursor no está sobre él. <paramref name="shown"/>
    /// es cómo se dibujó en el último cuadro (con la animación del estilo); sin él, se supone que no hay animación.
    /// </summary>
    public static TitleTransformDrag? Begin(EditorSession session, ClipPropertyEditor editor, string clipId, TitleGesture gesture, LayerTransform? shown = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(editor);
        if (session.Sequence.FindClip(clipId) is not { } found || found.Clip.Generator is null || !editor.IsPlayheadInside(found.Clip))
        {
            return null;
        }

        session.BeginGesture(gesture switch
        {
            TitleGesture.Scale => "Escalar el título",
            TitleGesture.Rotate => "Girar el título",
            _ => "Mover el título",
        });
        if (ClipPropertyEditor.FindEffect(found.Clip, BuiltInEffects.Transform) is null
            && !session.Execute(Edits.EnsureEffect(session.SequenceId, clipId, BuiltInEffects.Transform)))
        {
            session.CancelGesture();
            return null;
        }

        Clip clip = session.Sequence.GetClip(clipId).Clip;
        string effectId = ClipPropertyEditor.FindEffect(clip, BuiltInEffects.Transform)!.Id;
        Time at = editor.KeyframeTimeAtPlayhead(clip);
        ParameterState x = editor.Read(clip, BuiltInEffects.Transform, T.X, 0);
        ParameterState y = editor.Read(clip, BuiltInEffects.Transform, T.Y, 0);
        ParameterState s = editor.Read(clip, BuiltInEffects.Transform, T.Scale, 1);
        ParameterState r = editor.Read(clip, BuiltInEffects.Transform, T.Rotation, 0);
        // Un keyframe a menos de medio cuadro del cursor se ajusta en su sitio en vez de sembrar otro al lado.
        (Time, Time, Time) times = (x.KeyframeHere?.Time ?? y.KeyframeHere?.Time ?? at, s.KeyframeHere?.Time ?? at, r.KeyframeHere?.Time ?? at);
        (double, double) offset = shown is { } seen ? (seen.X - x.Value, seen.Y - y.Value) : (0, 0);
        return new TitleTransformDrag(session, clipId, effectId, times, (x.Value, y.Value, s.Value, r.Value), offset);
    }

    /// <summary>Lleva el centro a <paramref name="x"/>, <paramref name="y"/> (píxeles de la secuencia, desde el centro).</summary>
    public bool Move(double x, double y) => _open && _session.Execute(new SequenceEditCommand("Mover el título", _session.SequenceId, ctx =>
    {
        EffectEdits.SetParameter(ctx, _clipId, _effectId, T.X, Math.Round(x, 1), _at);
        EffectEdits.SetParameter(ctx, _clipId, _effectId, T.Y, Math.Round(y, 1), _at);
    }, mergeKey: $"titulo-mover|{_clipId}|{_at.Ticks}"));

    /// <summary>
    /// Escala alrededor de lo que se ve: el delta de la animación crece con la escala del clip, así que la posición del
    /// clip se corrige para que el centro dibujado no se mueva.
    /// </summary>
    public bool Scale(double scale) => _open && _session.Execute(new SequenceEditCommand("Escalar el título", _session.SequenceId, ctx =>
    {
        EffectEdits.SetParameter(ctx, _clipId, _effectId, T.Scale, Math.Round(scale, 4), _atScale);
        double grow = StartScale > 0 ? scale / StartScale : 1;
        KeepShownCenter(ctx, _offsetX * grow, _offsetY * grow);
    }, mergeKey: $"titulo-escalar|{_clipId}|{_atScale.Ticks}"));

    /// <summary>Gira alrededor de lo que se ve: el delta de la animación gira con el clip y la posición lo compensa.</summary>
    public bool Rotate(double degrees) => _open && _session.Execute(new SequenceEditCommand("Girar el título", _session.SequenceId, ctx =>
    {
        EffectEdits.SetParameter(ctx, _clipId, _effectId, T.Rotation, Math.Round(degrees, 2), _atRotation);
        double radians = (degrees - StartRotation) * Math.PI / 180;
        (double sin, double cos) = Math.SinCos(radians);
        KeepShownCenter(ctx, (_offsetX * cos) - (_offsetY * sin), (_offsetX * sin) + (_offsetY * cos));
    }, mergeKey: $"titulo-girar|{_clipId}|{_atRotation.Ticks}"));

    public void Complete()
    {
        if (_open)
        {
            _open = false;
            _session.EndGesture();
        }
    }

    public void Cancel()
    {
        if (_open)
        {
            _open = false;
            _session.CancelGesture();
        }
    }

    /// <summary>Con el delta de la animación ya en <paramref name="offsetX"/>, <paramref name="offsetY"/>, el centro dibujado queda donde estaba.</summary>
    private void KeepShownCenter(EditContext ctx, double offsetX, double offsetY)
    {
        if (_offsetX == 0 && _offsetY == 0)
        {
            return;
        }

        EffectEdits.SetParameter(ctx, _clipId, _effectId, T.X, Math.Round(StartX + _offsetX - offsetX, 1), _at);
        EffectEdits.SetParameter(ctx, _clipId, _effectId, T.Y, Math.Round(StartY + _offsetY - offsetY, 1), _at);
    }
}
