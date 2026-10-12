using System.Numerics;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Playback.Composition;
using Rodaje.Render;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.App.Views.Player;

/// <summary>Una máscara de un clip en el cursor: dónde está la imagen de la capa en la secuencia, la forma y sus vértices.</summary>
public readonly record struct MaskBox(string ClipId, string EffectId, LayerPlacement Layer, MaskSpec Mask, MaskVertex[] Vertices)
{
    public float ImageWidth => Layer.LocalWidth;

    public float ImageHeight => Layer.LocalHeight;

    public Vector2 ImageSize => new(Layer.LocalWidth, Layer.LocalHeight);

    /// <summary>Color del contorno (<c>#RRGGBB</c>).</summary>
    public string Color { get; init; } = MaskHandleColors.Default;

    /// <summary>La forma tiene keyframes de forma.</summary>
    public bool Animated { get; init; }

    /// <summary>Sus vértices se editan uno a uno (Bézier y pluma).</summary>
    public bool EditsVertices => Mask.Shape is MaskShape.Bezier or MaskShape.Stroke;
}

/// <summary>Colores de las asas.</summary>
public static class MaskHandleColors
{
    public const string Default = "#4FC3F7";
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
    Top,
    Right,
    Bottom,
    Left,
    Rotate,
    Vertex,
    InHandle,
    OutHandle,

    /// <summary>Sobre el contorno entre dos vértices: ahí se agrega uno.</summary>
    Segment,
}

/// <summary>Lo que acertó el puntero: la parte, el vértice o tramo y el parámetro en el tramo.</summary>
public readonly record struct MaskHit(MaskGrip Grip, int Index = -1, float T = 0);

/// <summary>
/// Las asas de una máscara en el visor, sin WinUI: su contorno con la misma geometría que el render (la imagen colocada con
/// <see cref="LayerLayout.Place"/> y la forma de <see cref="MaskGeometry"/>), el acierto del puntero y lo que hace un arrastre.
/// </summary>
public static class MaskHandles
{
    public const double GripRadius = 8;

    /// <summary>Distancia del asa de girar al borde de arriba, en unidades del panel.</summary>
    public const float RotateDistance = 26;

    /// <summary>Con Mayús, el giro va de este en este paso (grados).</summary>
    public const double RotateSnap = 15;

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
        MaskVertex[] vertices = mask.HasVertices ? MaskGeometry.Vertices(effect, at) : [];
        return new MaskBox(clip.Id, effect.Id, layer, mask, vertices)
        {
            Color = effect.Options.GetValueOrDefault(BuiltInEffects.MaskOptions.Color) ?? MaskHandleColors.Default,
            Animated = MaskGeometry.IsShapeAnimated(effect),
        };
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

    /// <summary>Un punto de la caja (−0,5 a 0,5) en el panel.</summary>
    public static Vector2 ToPanel(in MaskBox box, ImageFrame frame, Vector2 local) => frame.ToPanel(ToSequence(box, BoxToImage(box, local)));

    /// <summary>Un punto del panel en la caja de la máscara (−0,5 a 0,5).</summary>
    public static Vector2 FromPanel(in MaskBox box, ImageFrame frame, Vector2 panel) =>
        ImageToBox(box, ToImage(box.Layer, frame.ToSequence(panel.X, panel.Y)));

    /// <summary>Las cuatro esquinas de la caja de la máscara en la secuencia (arriba izquierda, arriba derecha, abajo derecha, abajo izquierda).</summary>
    public static Vector2[] Corners(in MaskBox box)
    {
        MaskBox b = box;
        return [Corner(-0.5f, -0.5f), Corner(0.5f, -0.5f), Corner(0.5f, 0.5f), Corner(-0.5f, 0.5f)];

        Vector2 Corner(float u, float v) => ToSequence(b, BoxToImage(b, new Vector2(u, v)));
    }

    /// <summary>
    /// El contorno como vértices de la caja (las formas de caja también, para dibujarlas como curvas) y si es cerrado;
    /// vacío en una máscara de imagen.
    /// </summary>
    public static (MaskVertex[] Vertices, bool Closed) Shape(in MaskBox box) => box.Mask.Shape switch
    {
        MaskShape.Ellipse => (MaskPath.Ellipse(), true),
        MaskShape.Rectangle when box.Mask.Roundness > 0 =>
            (MaskPath.RoundedRectangle(box.Mask.Width * box.ImageWidth, box.Mask.Height * box.ImageHeight, box.Mask.Roundness), true),
        MaskShape.Rectangle => (MaskPath.Rectangle(), true),
        MaskShape.Image => ([], true),
        MaskShape.Stroke => (box.Vertices, false),
        _ => (box.Vertices, true),
    };

    /// <summary>El contorno que se ve, en la secuencia (aplanado): la caja, la elipse, el trazo o la Bézier.</summary>
    public static Vector2[] Outline(in MaskBox box)
    {
        MaskBox b = box;
        (MaskVertex[] vertices, bool closed) = Shape(box);
        if (vertices.Length < 2)
        {
            return [];
        }

        var polyline = new Vector3[MaskPath.MaxPolyline];
        int count = MaskPath.Flatten(vertices, closed, (float)(box.Mask.Width * box.ImageWidth), (float)(box.Mask.Height * box.ImageHeight),
            polyline, 1);
        return [.. polyline[..count].Select(p => ToSequence(b, BoxToImage(b, new Vector2(p.X, p.Y))))];
    }

    /// <summary>Las ocho asas de la caja (esquinas y luego bordes: arriba, derecha, abajo, izquierda) y la de girar, en el panel.</summary>
    public static (Vector2[] Grips, Vector2 Rotate, Vector2 Stem) BoxGrips(in MaskBox box, ImageFrame frame)
    {
        MaskBox b = box;
        Vector2 P(float u, float v) => ToPanel(b, frame, new Vector2(u, v));
        Vector2[] grips = [P(-0.5f, -0.5f), P(0.5f, -0.5f), P(0.5f, 0.5f), P(-0.5f, 0.5f), P(0, -0.5f), P(0.5f, 0), P(0, 0.5f), P(-0.5f, 0)];
        Vector2 up = grips[4] - P(0, 0);
        up = up.LengthSquared() < 1e-6f ? new Vector2(0, -1) : Vector2.Normalize(up);
        return (grips, grips[4] + (up * RotateDistance), grips[4]);
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

        return Inside(panelCorners, point) ? MaskGrip.Body : MaskGrip.None;
    }

    /// <summary>
    /// Qué acierta el puntero en el panel, en este orden: manejadores de los vértices elegidos, vértices, asa de girar y
    /// asas de la caja (si se ven), el contorno (para agregar un vértice) y el cuerpo.
    /// </summary>
    public static MaskHit HitTest(in MaskBox box, ImageFrame frame, Vector2 panel, IReadOnlyList<int> selected, bool showBox)
    {
        ArgumentNullException.ThrowIfNull(selected);
        MaskBox b = box;
        float radius = (float)GripRadius;
        if (box.EditsVertices)
        {
            foreach (int i in selected)
            {
                if (i < 0 || i >= box.Vertices.Length)
                {
                    continue;
                }

                MaskVertex v = box.Vertices[i];
                if (v.Out != Vector2.Zero && Vector2.Distance(ToPanel(b, frame, v.Point + v.Out), panel) <= radius)
                {
                    return new MaskHit(MaskGrip.OutHandle, i);
                }

                if (v.In != Vector2.Zero && Vector2.Distance(ToPanel(b, frame, v.Point + v.In), panel) <= radius)
                {
                    return new MaskHit(MaskGrip.InHandle, i);
                }
            }

            for (int i = 0; i < box.Vertices.Length; i++)
            {
                if (Vector2.Distance(ToPanel(b, frame, box.Vertices[i].Point), panel) <= radius)
                {
                    return new MaskHit(MaskGrip.Vertex, i);
                }
            }
        }

        if (showBox && box.Mask.Shape != MaskShape.Image)
        {
            (Vector2[] grips, Vector2 rotate, _) = BoxGrips(box, frame);
            if (Vector2.Distance(rotate, panel) <= radius)
            {
                return new MaskHit(MaskGrip.Rotate);
            }

            for (int i = 0; i < grips.Length; i++)
            {
                if (Vector2.Distance(grips[i], panel) <= radius)
                {
                    return new MaskHit(MaskGrip.TopLeft + i);
                }
            }
        }

        if (box.EditsVertices && box.Vertices.Length >= 2)
        {
            Vector2 local = FromPanel(box, frame, panel);
            (int segment, float t, float _) = MaskPath.Nearest(box.Vertices, box.Mask.Shape != MaskShape.Stroke, local,
                new Vector2((float)(box.Mask.Width * box.ImageWidth), (float)(box.Mask.Height * box.ImageHeight)));
            if (segment >= 0)
            {
                MaskVertex a = box.Vertices[segment], c = box.Vertices[(segment + 1) % box.Vertices.Length];
                Vector2 onCurve = ToPanel(b, frame, MaskPath.Cubic(a.Point, a.Point + a.Out, c.Point + c.In, c.Point, t));
                if (Vector2.Distance(onCurve, panel) <= radius * 0.75f)
                {
                    return new MaskHit(MaskGrip.Segment, segment, t);
                }
            }
        }

        Vector2[] outline = [.. Outline(box).Select(frame.ToPanel)];
        bool inside = box.Mask.Shape == MaskShape.Image
            ? Inside([.. Corners(box).Select(frame.ToPanel)], panel)
            : box.Mask.Shape == MaskShape.Stroke ? NearPolyline(outline, panel, radius) : outline.Length >= 3 && InsidePolygon(outline, panel);
        return inside ? new MaskHit(MaskGrip.Body) : new MaskHit(MaskGrip.None);
    }

    /// <summary>El centro nuevo (fracción de la imagen) tras mover el puntero de <paramref name="from"/> a <paramref name="to"/> (píxeles de la imagen).</summary>
    public static (double X, double Y) Moved(in MaskBox start, Vector2 from, Vector2 to) =>
        (start.Mask.X + ((to.X - from.X) / start.ImageWidth), start.Mask.Y + ((to.Y - from.Y) / start.ImageHeight));

    /// <summary>
    /// El tamaño nuevo (fracción de la imagen) con una esquina en <paramref name="to"/> (píxeles de la imagen): la caja crece
    /// alrededor de su centro y sigue girada.
    /// </summary>
    public static (double Width, double Height) Resized(in MaskBox start, Vector2 to) => Resized(start, MaskGrip.TopLeft, to, false);

    /// <summary>
    /// El tamaño nuevo con el asa <paramref name="grip"/> en <paramref name="to"/>: una esquina cambia los dos lados (con
    /// <paramref name="keepAspect"/>, en proporción) y un borde solo el suyo.
    /// </summary>
    public static (double Width, double Height) Resized(in MaskBox start, MaskGrip grip, Vector2 to, bool keepAspect)
    {
        double radians = start.Mask.Rotation * Math.PI / 180;
        double dx = to.X - (start.Mask.X * start.ImageWidth), dy = to.Y - (start.Mask.Y * start.ImageHeight);
        double lx = (Math.Cos(radians) * dx) + (Math.Sin(radians) * dy);
        double ly = (-Math.Sin(radians) * dx) + (Math.Cos(radians) * dy);
        double width = Math.Clamp(2 * Math.Abs(lx) / start.ImageWidth, 0.01, 4);
        double height = Math.Clamp(2 * Math.Abs(ly) / start.ImageHeight, 0.01, 4);
        switch (grip)
        {
            case MaskGrip.Top or MaskGrip.Bottom:
                return (start.Mask.Width, height);
            case MaskGrip.Left or MaskGrip.Right:
                return (width, start.Mask.Height);
        }

        if (keepAspect && start.Mask.Width > 0 && start.Mask.Height > 0)
        {
            double scale = Math.Max(width / start.Mask.Width, height / start.Mask.Height);
            return (start.Mask.Width * scale, start.Mask.Height * scale);
        }

        return (width, height);
    }

    /// <summary>El giro nuevo (grados) tras llevar el asa de girar de <paramref name="from"/> a <paramref name="to"/> (píxeles de la imagen).</summary>
    public static double Rotated(in MaskBox start, Vector2 from, Vector2 to, bool snap)
    {
        var center = new Vector2((float)(start.Mask.X * start.ImageWidth), (float)(start.Mask.Y * start.ImageHeight));
        double a0 = Math.Atan2(from.Y - center.Y, from.X - center.X), a1 = Math.Atan2(to.Y - center.Y, to.X - center.X);
        double rotation = start.Mask.Rotation + ((a1 - a0) * 180 / Math.PI);
        rotation = Math.IEEERemainder(rotation, 360);
        return snap ? Math.Round(rotation / RotateSnap) * RotateSnap : rotation;
    }

    /// <summary>Un punto de la caja (−0,5 a 0,5) en píxeles de la imagen, con el giro de la máscara.</summary>
    public static Vector2 BoxToImage(in MaskBox box, Vector2 local)
    {
        MaskSpec m = box.Mask;
        float radians = (float)(m.Rotation * Math.PI / 180);
        float x = local.X * (float)m.Width * box.ImageWidth;
        float y = local.Y * (float)m.Height * box.ImageHeight;
        (float sin, float cos) = MathF.SinCos(radians);
        return new Vector2(((float)m.X * box.ImageWidth) + (cos * x) - (sin * y), ((float)m.Y * box.ImageHeight) + (sin * x) + (cos * y));
    }

    /// <summary>Un punto en píxeles de la imagen en la caja de la máscara (−0,5 a 0,5).</summary>
    public static Vector2 ImageToBox(in MaskBox box, Vector2 image)
    {
        MaskSpec m = box.Mask;
        float radians = (float)(m.Rotation * Math.PI / 180);
        float dx = image.X - ((float)m.X * box.ImageWidth), dy = image.Y - ((float)m.Y * box.ImageHeight);
        (float sin, float cos) = MathF.SinCos(radians);
        float lx = (cos * dx) + (sin * dy), ly = (-sin * dx) + (cos * dy);
        return new Vector2(lx / Math.Max((float)m.Width * box.ImageWidth, 1e-6f), ly / Math.Max((float)m.Height * box.ImageHeight, 1e-6f));
    }

    /// <summary>Los vértices dentro del rectángulo del panel entre <paramref name="a"/> y <paramref name="b"/> (selección por recuadro).</summary>
    public static int[] InRectangle(in MaskBox box, ImageFrame frame, Vector2 a, Vector2 b)
    {
        Vector2 min = Vector2.Min(a, b), max = Vector2.Max(a, b);
        MaskBox m = box;
        return [.. Enumerable.Range(0, box.Vertices.Length).Where(i =>
        {
            Vector2 p = ToPanel(m, frame, m.Vertices[i].Point);
            return p.X >= min.X && p.Y >= min.Y && p.X <= max.X && p.Y <= max.Y;
        })];
    }

    /// <summary>Mueve los vértices elegidos (con sus manejadores) <paramref name="delta"/> en el sistema de la caja.</summary>
    public static MaskVertex[] MoveVertices(IReadOnlyList<MaskVertex> vertices, IReadOnlyList<int> selected, Vector2 delta)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(selected);
        MaskVertex[] result = [.. vertices];
        foreach (int i in selected.Distinct())
        {
            if (i >= 0 && i < result.Length)
            {
                result[i] = result[i] with { Point = result[i].Point + delta };
            }
        }

        return result;
    }

    /// <summary>
    /// Lleva un manejador a <paramref name="target"/> (sistema de la caja). Si el vértice es suave, el manejador del otro lado
    /// queda alineado conservando su largo, salvo con <paramref name="independent"/> (Alt), que lo quiebra.
    /// </summary>
    public static MaskVertex[] MoveHandle(IReadOnlyList<MaskVertex> vertices, int index, bool outHandle, Vector2 target, bool independent)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        MaskVertex[] result = [.. vertices];
        MaskVertex v = result[index];
        Vector2 handle = target - v.Point;
        Vector2 other = outHandle ? v.In : v.Out;
        if (!independent && handle.LengthSquared() > 1e-10f)
        {
            float length = other == Vector2.Zero ? handle.Length() : other.Length();
            other = -Vector2.Normalize(handle) * length;
        }

        result[index] = outHandle ? v with { Out = handle, In = other } : v with { In = handle, Out = other };
        return result;
    }

    /// <summary>Quita los vértices elegidos si quedan los que pide la forma (tres en una Bézier, dos en una pluma); si no, nulo.</summary>
    public static MaskVertex[]? Remove(IReadOnlyList<MaskVertex> vertices, IReadOnlyList<int> selected, MaskShape shape)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(selected);
        MaskVertex[] left = [.. vertices.Where((_, i) => !selected.Contains(i))];
        return left.Length >= MaskGeometry.MinimumPoints(shape == MaskShape.Stroke ? MaskShape.Stroke : MaskShape.Bezier) ? left : null;
    }

    /// <summary>Cambia el peso (borde o grosor) de los vértices elegidos.</summary>
    public static MaskVertex[] Weighted(IReadOnlyList<MaskVertex> vertices, IReadOnlyList<int> selected, float weight)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(selected);
        return [.. vertices.Select((v, i) => selected.Contains(i) ? v with { Weight = Math.Clamp(weight, 0, 16) } : v)];
    }

    private static bool Inside(Vector2[] corners, Vector2 point)
    {
        int sign = 0;
        for (int i = 0; i < 4; i++)
        {
            Vector2 a = corners[i];
            Vector2 b = corners[(i + 1) % 4];
            int s = Math.Sign(((b.X - a.X) * (point.Y - a.Y)) - ((b.Y - a.Y) * (point.X - a.X)));
            if (s != 0)
            {
                if (sign != 0 && s != sign)
                {
                    return false;
                }

                sign = s;
            }
        }

        return sign != 0;
    }

    private static bool InsidePolygon(Vector2[] polygon, Vector2 p)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            Vector2 a = polygon[i], b = polygon[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < ((b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y)) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static bool NearPolyline(Vector2[] line, Vector2 p, float radius)
    {
        for (int i = 0; i + 1 < line.Length; i++)
        {
            Vector2 ab = line[i + 1] - line[i];
            float t = ab.LengthSquared() < 1e-9f ? 0 : Math.Clamp(Vector2.Dot(p - line[i], ab) / ab.LengthSquared(), 0, 1);
            if (Vector2.Distance(p, line[i] + (ab * t)) <= radius)
            {
                return true;
            }
        }

        return false;
    }
}
