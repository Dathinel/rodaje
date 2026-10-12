using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.App.Services.Inspector;

/// <summary>Qué hace un arrastre en el visor sobre la transformación de un clip de video.</summary>
public enum ClipGesture
{
    Move,
    Scale,
    Rotate,

    /// <summary>Mueve el punto de anclaje sin mover la imagen (la posición lo compensa).</summary>
    Anchor,

    /// <summary>Un manejador de la trayectoria de la posición.</summary>
    Path,
}

/// <summary>
/// Mover, escalar, girar o anclar un clip con las asas del visor: un gesto (un paso del historial, Esc lo revierte) que
/// cambia <c>rodaje.transform</c> en el cursor; con keyframes crea o ajusta el del cursor, como la pestaña Propiedades.
/// </summary>
public sealed class ClipTransformDrag
{
    private readonly EditorSession _session;
    private readonly string _clipId;
    private readonly string _effectId;
    private readonly Time _at;
    private bool _open = true;

    private ClipTransformDrag(EditorSession session, string clipId, string effectId, Time at, TransformValues start)
    {
        _session = session;
        _clipId = clipId;
        _effectId = effectId;
        _at = at;
        Start = start;
    }

    /// <summary>Los valores del clip en el cursor al empezar.</summary>
    public TransformValues Start { get; }

    /// <summary>Empieza el gesto; nulo si el clip no está o el cursor no está sobre él.</summary>
    public static ClipTransformDrag? Begin(EditorSession session, ClipPropertyEditor editor, string clipId, ClipGesture gesture)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(editor);
        if (session.Sequence.FindClip(clipId) is not { } found || !editor.IsPlayheadInside(found.Clip))
        {
            return null;
        }

        session.BeginGesture(gesture switch
        {
            ClipGesture.Scale => "Escalar el clip",
            ClipGesture.Rotate => "Girar el clip",
            ClipGesture.Anchor => "Mover el punto de anclaje",
            ClipGesture.Path => "Cambiar trayectoria",
            _ => "Mover el clip",
        });
        if (ClipPropertyEditor.FindEffect(found.Clip, BuiltInEffects.Transform) is null
            && !session.Execute(Edits.EnsureEffect(session.SequenceId, clipId, BuiltInEffects.Transform)))
        {
            session.CancelGesture();
            return null;
        }

        Clip clip = session.Sequence.GetClip(clipId).Clip;
        EffectInstance effect = ClipPropertyEditor.FindEffect(clip, BuiltInEffects.Transform)!;
        Time at = editor.KeyframeTimeAtPlayhead(clip);
        ParameterState x = editor.Read(clip, BuiltInEffects.Transform, T.X, 0);
        Time keyframe = x.KeyframeHere?.Time ?? editor.Read(clip, BuiltInEffects.Transform, T.Y, 0).KeyframeHere?.Time ?? at;
        return new ClipTransformDrag(session, clipId, effect.Id, keyframe, TransformValues.Read(effect, keyframe));
    }

    public bool Move(double x, double y) => Set("Mover el clip", "mover", (T.X, Math.Round(x, 1)), (T.Y, Math.Round(y, 1)));

    public bool Scale(double scale) => Set("Escalar el clip", "escalar", (T.Scale, Math.Round(scale, 4)));

    public bool Rotate(double degrees) => Set("Girar el clip", "girar", (T.Rotation, Math.Round(degrees, 2)));

    /// <summary>Lleva el ancla a (<paramref name="anchorX"/>, <paramref name="anchorY"/>) y corrige la posición para que la imagen no se mueva.</summary>
    public bool MoveAnchor(double anchorX, double anchorY)
    {
        (double x, double y) = Start.CompensateAnchor(anchorX - Start.AnchorX, anchorY - Start.AnchorY);
        return Set("Mover el punto de anclaje", "ancla", (T.AnchorX, Math.Round(anchorX, 1)), (T.AnchorY, Math.Round(anchorY, 1)),
            (T.X, Math.Round(x, 1)), (T.Y, Math.Round(y, 1)));
    }

    /// <summary>Las tangentes de la trayectoria en el keyframe de posición de <paramref name="keyframe"/>.</summary>
    public bool SetPath(Time keyframe, (double X, double Y)? spatialIn, (double X, double Y)? spatialOut) =>
        _open && _session.Execute(Edits.SetPathTangents(_session.SequenceId, _clipId, _effectId, keyframe, spatialIn, spatialOut));

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

    private bool Set(string description, string kind, params (string Name, double Value)[] values) =>
        _open && _session.Execute(new SequenceEditCommand(description, _session.SequenceId, ctx =>
        {
            foreach ((string name, double value) in values)
            {
                EffectEdits.SetParameter(ctx, _clipId, _effectId, name, value, _at);
            }
        }, mergeKey: $"clip-{kind}|{_clipId}|{_at.Ticks}"));
}

/// <summary>La transformación de un clip en un instante, con lo que hace falta para mover el ancla sin mover la imagen.</summary>
public readonly record struct TransformValues(double X, double Y, double Scale, double Rotation, double AnchorX, double AnchorY, double ScaleX,
    double ScaleY, double SkewX, double SkewY)
{
    public static TransformValues Read(EffectInstance transform, Time at)
    {
        ArgumentNullException.ThrowIfNull(transform);
        double V(string name) => transform.Parameters.TryGetValue(name, out Parameter? p) ? p.Evaluate(at) : T.DefaultValue(name);
        return new TransformValues(V(T.X), V(T.Y), V(T.Scale), V(T.Rotation), V(T.AnchorX), V(T.AnchorY), V(T.ScaleX), V(T.ScaleY), V(T.SkewX),
            V(T.SkewY));
    }

    /// <summary>
    /// La posición que deja la imagen quieta al correr el ancla (dx, dy): <c>t' = t + (M − I)·d</c>, con M = giro ·
    /// inclinación · escala (la misma matriz que <c>LayerLayout</c>).
    /// </summary>
    /// <summary>Un desplazamiento en la secuencia llevado a la imagen sin transformar (la inversa de M); nulo si M no se invierte.</summary>
    public (double X, double Y)? ToLocal(double dx, double dy)
    {
        (double ax, double ay, double bx, double by) = Linear();
        double det = (ax * by) - (bx * ay);
        return Math.Abs(det) < 1e-12 ? null : (((by * dx) - (bx * dy)) / det, ((ax * dy) - (ay * dx)) / det);
    }

    public (double X, double Y) CompensateAnchor(double dx, double dy)
    {
        (double ax, double ay, double bx, double by) = Linear();
        return (X + (ax * dx) + (bx * dy) - dx, Y + (ay * dx) + (by * dy) - dy);
    }

    /// <summary>Las columnas de M = giro · inclinación · escala, como en <c>LayerLayout</c>.</summary>
    private (double Ax, double Ay, double Bx, double By) Linear()
    {
        double radians = Rotation % 360 * Math.PI / 180;
        (double sin, double cos) = Math.SinCos(radians);
        double sx = Scale * ScaleX;
        double sy = Scale * ScaleY;
        double kx = Math.Tan(Math.Clamp(SkewX, -89, 89) * Math.PI / 180);
        double ky = Math.Tan(Math.Clamp(SkewY, -89, 89) * Math.PI / 180);
        double ax = (sx * cos) - (ky * sx * sin);
        double ay = (sx * sin) + (ky * sx * cos);
        double bx = (kx * sy * cos) - (sy * sin);
        double by = (kx * sy * sin) + (sy * cos);
        return (ax, ay, bx, by);
    }
}
