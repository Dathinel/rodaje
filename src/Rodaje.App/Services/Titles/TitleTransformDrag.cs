using Rodaje.App.Services.Inspector;
using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.App.Services.Titles;

/// <summary>
/// Mover o escalar un título con las asas del visor: un gesto (un paso del historial, Esc lo revierte) que cambia la
/// posición o la escala de <c>rodaje.transform</c> en el cursor; con keyframes, crea o ajusta el del cursor como la
/// pestaña Propiedades.
/// </summary>
public sealed class TitleTransformDrag
{
    private readonly EditorSession _session;
    private readonly string _clipId;
    private readonly string _effectId;
    private readonly Time _at;
    private readonly Time _atScale;
    private bool _open = true;

    private TitleTransformDrag(EditorSession session, string clipId, string effectId, Time at, Time atScale, double x, double y, double scale)
    {
        _session = session;
        _clipId = clipId;
        _effectId = effectId;
        _at = at;
        _atScale = atScale;
        StartX = x;
        StartY = y;
        StartScale = scale;
    }

    /// <summary>Posición y escala del clip en el cursor al empezar (sin la animación del estilo).</summary>
    public double StartX { get; }

    public double StartY { get; }

    public double StartScale { get; }

    /// <summary>Empieza el gesto sobre el título; nulo si el clip no es un título o el cursor no está sobre él.</summary>
    public static TitleTransformDrag? Begin(EditorSession session, ClipPropertyEditor editor, string clipId, bool scale)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(editor);
        if (session.Sequence.FindClip(clipId) is not { } found || found.Clip.Generator is null || !editor.IsPlayheadInside(found.Clip))
        {
            return null;
        }

        session.BeginGesture(scale ? "Escalar el título" : "Mover el título");
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
        // Un keyframe a menos de medio cuadro del cursor se ajusta en su sitio en vez de sembrar otro al lado.
        Time atPosition = x.KeyframeHere?.Time ?? y.KeyframeHere?.Time ?? at;
        Time atScale = s.KeyframeHere?.Time ?? at;
        return new TitleTransformDrag(session, clipId, effectId, atPosition, atScale, x.Value, y.Value, s.Value);
    }

    /// <summary>Lleva el centro a <paramref name="x"/>, <paramref name="y"/> (píxeles de la secuencia, desde el centro).</summary>
    public bool Move(double x, double y) => _open && _session.Execute(new SequenceEditCommand("Mover el título", _session.SequenceId, ctx =>
    {
        EffectEdits.SetParameter(ctx, _clipId, _effectId, T.X, Math.Round(x, 1), _at);
        EffectEdits.SetParameter(ctx, _clipId, _effectId, T.Y, Math.Round(y, 1), _at);
    }, mergeKey: $"titulo-mover|{_clipId}|{_at.Ticks}"));

    public bool Scale(double scale) => _open && _session.Execute(new SequenceEditCommand("Escalar el título", _session.SequenceId, ctx =>
        EffectEdits.SetParameter(ctx, _clipId, _effectId, T.Scale, Math.Round(scale, 4), _atScale),
        mergeKey: $"titulo-escalar|{_clipId}|{_atScale.Ticks}"));

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
}
