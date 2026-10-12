using System.Numerics;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Playback.Composition;
using Rodaje.Render;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.App.Views.Player;

/// <summary>Una máscara de un clip en el cursor: dónde está la imagen de la capa en la secuencia y la forma evaluada.</summary>
public readonly record struct MaskBox(string ClipId, string EffectId, LayerPlacement Layer, MaskSpec Mask, Vector2[] Path)
{
    public float ImageWidth => Layer.LocalWidth;

    public float ImageHeight => Layer.LocalHeight;
}

/// <summary>Qué parte de la máscara queda bajo el puntero.</summary>
public enum MaskGrip
{
    None,
    Body,
    TopLeft,
    TopRight,
    BottomRight,
    BottomLeft,
}

/// <summary>
/// Las asas de una máscara en el visor, sin WinUI: su contorno con la misma geometría que el render (la imagen colocada con
/// <see cref="LayerLayout.Place"/> y la forma de <see cref="MaskGeometry"/>), el acierto del puntero y lo que hace un arrastre.
/// </summary>
public static class MaskHandles
{
    public const double GripRadius = 8;

    /// <summary>Puntos del contorno de una elipse.</summary>
    public const int EllipseSegments = 48;

    /// <summary>La máscara <paramref name="effect"/> del clip en <paramref name="timeline"/>; nula si el clip no se ve ahí o no tiene imagen.</summary>
    public static MaskBox? Find(Project project, Sequence sequence, Clip clip, EffectInstance effect, Time timeline)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(effect);
        if (timeline < clip.Start || timeline >= clip.End || project.Media.Find(m => m.Id == clip.MediaId) is not { Width: > 0, Height: > 0 } media)
        {
            return null;
        }

        Time at = clip.ToEffectTime(timeline);
        if (LayerLayout.Place(sequence.Width, sequence.Height, media.Width, media.Height, TransformAt(clip, at)) is not { } layer)
        {
            return null;
        }

        MaskSpec mask = MaskGeometry.Evaluate(effect, at);
        Vector2[] path = mask.Shape == MaskShape.Path ? MaskGeometry.ParsePath(effect.Options.GetValueOrDefault(BuiltInEffects.MaskOptions.Path)) : [];
        return new MaskBox(clip.Id, effect.Id, layer, mask, path);
    }

    /// <summary>La primera transformación y el primer recorte activos del clip en <paramref name="sourceTime"/>, como los arma el motor.</summary>
    public static LayerTransform TransformAt(Clip clip, Time sourceTime)
    {
        ArgumentNullException.ThrowIfNull(clip);
        LayerTransform result = LayerTransform.Identity;
        if (clip.Effects.Find(e => e.Enabled && e.Type == BuiltInEffects.Transform) is { } transform)
        {
            double Value(string name) => transform.Parameters.TryGetValue(name, out Parameter? p) ? p.Evaluate(sourceTime) : T.DefaultValue(name);
            result = new LayerTransform(Value(T.X), Value(T.Y), Value(T.Scale), Value(T.Rotation), Value(T.Opacity));
        }

        if (clip.Effects.Find(e => e.Enabled && e.Type == BuiltInEffects.Crop) is { } crop
            && CropGeometry.Evaluate(crop, sourceTime) is { IsNone: false } rect)
        {
            result = result with { Crop = rect };
        }

        return result;
    }

    /// <summary>Un punto de la imagen de la capa (en píxeles de la imagen) en la secuencia.</summary>
    public static Vector2 ToSequence(in MaskBox box, Vector2 image) => box.Layer.Map(image.X, image.Y);

    /// <summary>Un punto de la secuencia en píxeles de la imagen de la capa.</summary>
    public static Vector2 ToImage(LayerPlacement layer, Vector2 sequence) =>
        Matrix3x2.Invert(layer.LocalToSequence, out Matrix3x2 inverse) ? Vector2.Transform(sequence, inverse) : Vector2.Zero;

    /// <summary>Las cuatro esquinas de la caja de la máscara en la secuencia (arriba izquierda, arriba derecha, abajo derecha, abajo izquierda).</summary>
    public static Vector2[] Corners(in MaskBox box)
    {
        MaskBox b = box;
        return [Corner(-0.5f, -0.5f), Corner(0.5f, -0.5f), Corner(0.5f, 0.5f), Corner(-0.5f, 0.5f)];

        Vector2 Corner(float u, float v) => ToSequence(b, BoxPoint(b, u, v));
    }

    /// <summary>El contorno que se ve, en la secuencia: la caja, la elipse o el trazo.</summary>
    public static Vector2[] Outline(in MaskBox box)
    {
        MaskBox b = box;
        switch (box.Mask.Shape)
        {
            case MaskShape.Ellipse:
                return [.. Enumerable.Range(0, EllipseSegments).Select(i =>
                {
                    float a = i * MathF.Tau / EllipseSegments;
                    return ToSequence(b, BoxPoint(b, 0.5f * MathF.Cos(a), 0.5f * MathF.Sin(a)));
                })];
            case MaskShape.Path when box.Path.Length >= 3:
                return [.. box.Path.Select(p => ToSequence(b, BoxPoint(b, p.X, p.Y)))];
            case MaskShape.Image:
                return [];
            default:
                return Corners(box);
        }
    }

    /// <summary>Una esquina si el puntero está a <see cref="GripRadius"/> de ella; si no, el cuerpo (dentro de la caja) o nada.</summary>
    public static MaskGrip Hit(Vector2[] panelCorners, Vector2 point)
    {
        ArgumentNullException.ThrowIfNull(panelCorners);
        for (int i = 0; i < 4; i++)
        {
            if (Vector2.Distance(panelCorners[i], point) <= GripRadius)
            {
                return MaskGrip.TopLeft + i;
            }
        }

        int sign = 0;
        for (int i = 0; i < 4; i++)
        {
            Vector2 a = panelCorners[i];
            Vector2 b = panelCorners[(i + 1) % 4];
            int s = Math.Sign(((b.X - a.X) * (point.Y - a.Y)) - ((b.Y - a.Y) * (point.X - a.X)));
            if (s != 0)
            {
                if (sign != 0 && s != sign)
                {
                    return MaskGrip.None;
                }

                sign = s;
            }
        }

        return sign != 0 ? MaskGrip.Body : MaskGrip.None;
    }

    /// <summary>El centro nuevo (fracción de la imagen) tras mover el puntero de <paramref name="from"/> a <paramref name="to"/> (píxeles de la imagen).</summary>
    public static (double X, double Y) Moved(in MaskBox start, Vector2 from, Vector2 to) =>
        (start.Mask.X + ((to.X - from.X) / start.ImageWidth), start.Mask.Y + ((to.Y - from.Y) / start.ImageHeight));

    /// <summary>
    /// El tamaño nuevo (fracción de la imagen) con una esquina en <paramref name="to"/> (píxeles de la imagen): la caja crece
    /// alrededor de su centro y sigue girada.
    /// </summary>
    public static (double Width, double Height) Resized(in MaskBox start, Vector2 to)
    {
        double radians = start.Mask.Rotation * Math.PI / 180;
        double dx = to.X - (start.Mask.X * start.ImageWidth), dy = to.Y - (start.Mask.Y * start.ImageHeight);
        double lx = (Math.Cos(radians) * dx) + (Math.Sin(radians) * dy);
        double ly = (-Math.Sin(radians) * dx) + (Math.Cos(radians) * dy);
        return (Math.Clamp(2 * Math.Abs(lx) / start.ImageWidth, 0.01, 4), Math.Clamp(2 * Math.Abs(ly) / start.ImageHeight, 0.01, 4));
    }

    /// <summary>Un punto de la caja (−0,5 a 0,5 en cada eje) en píxeles de la imagen, con el giro de la máscara.</summary>
    private static Vector2 BoxPoint(in MaskBox box, float u, float v)
    {
        MaskSpec m = box.Mask;
        float radians = (float)(m.Rotation * Math.PI / 180);
        float x = u * (float)m.Width * box.ImageWidth;
        float y = v * (float)m.Height * box.ImageHeight;
        (float sin, float cos) = MathF.SinCos(radians);
        return new Vector2(((float)m.X * box.ImageWidth) + (cos * x) - (sin * y), ((float)m.Y * box.ImageHeight) + (sin * x) + (cos * y));
    }
}
