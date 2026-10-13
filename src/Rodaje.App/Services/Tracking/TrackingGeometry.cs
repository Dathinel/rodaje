using System.Numerics;
using Rodaje.Analysis.Video;
using Rodaje.Core.Editing;
using Rodaje.Core.Effects;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services.Tracking;

/// <summary>
/// Cómo se ve el cuadro codificado en la capa: rotación del archivo (horaria) y recorte. La imagen de la capa va de 0 a 1
/// en cada eje (la parte que queda tras el recorte); el cuadro codificado, en píxeles de la textura.
/// </summary>
public readonly record struct SourceView(int CodedWidth, int CodedHeight, int Rotation, CropRect Crop)
{
    /// <summary>Un punto de la imagen de la capa (0–1) en píxeles del cuadro codificado.</summary>
    public Vector2 ToCoded(Vector2 image)
    {
        double dx = Crop.Left + (image.X * Crop.Width), dy = Crop.Top + (image.Y * Crop.Height);
        (double u, double v) = (((Rotation % 360) + 360) % 360) switch
        {
            90 => (dy, 1 - dx),
            180 => (1 - dx, 1 - dy),
            270 => (1 - dy, dx),
            _ => (dx, dy),
        };
        return new Vector2((float)(u * CodedWidth), (float)(v * CodedHeight));
    }

    /// <summary>Un punto del cuadro codificado (píxeles) en la imagen de la capa (0–1).</summary>
    public Vector2 ToImage(Vector2 coded)
    {
        double u = coded.X / CodedWidth, v = coded.Y / CodedHeight;
        (double dx, double dy) = (((Rotation % 360) + 360) % 360) switch
        {
            90 => (1 - v, u),
            180 => (1 - u, 1 - v),
            270 => (v, 1 - u),
            _ => (u, v),
        };
        return new Vector2((float)((dx - Crop.Left) / Math.Max(Crop.Width, 1e-9)), (float)((dy - Crop.Top) / Math.Max(Crop.Height, 1e-9)));
    }
}

/// <summary>Un cuadro seguido: su instante del origen y dónde quedó cada punto de la imagen de partida.</summary>
/// <param name="Time">Instante del origen del cuadro.</param>
/// <param name="Motion">Movimiento desde el cuadro de partida, en píxeles del cuadro codificado.</param>
/// <param name="Anchors">Los puntos sueltos, en la imagen de la capa (0–1).</param>
/// <param name="Start">Cómo se veía el cuadro de partida.</param>
/// <param name="Now">Cómo se ve este cuadro (el recorte puede estar animado).</param>
/// <param name="Lost">Sin textura que seguir: la región quedó donde estaba.</param>
public sealed record TrackedFrame(Time Time, Similarity Motion, Vector2[] Anchors, SourceView Start, SourceView Now, bool Lost)
{
    /// <summary>Dónde está ahora un punto de la imagen de partida (0–1).</summary>
    public Vector2 Map(Vector2 image)
    {
        Vector2 coded = Start.ToCoded(image);
        (double x, double y) = Motion.Apply(coded.X, coded.Y);
        return Now.ToImage(new Vector2((float)x, (float)y));
    }
}

/// <summary>
/// Del seguimiento de una región a los keyframes de una máscara: la caja sigue el centro, la escala y el giro (medidos con
/// dos ejes de la caja, así vale con rotación del archivo y recorte) y, si se piden, los vértices siguen cada uno su zona.
/// </summary>
public static class MaskTracking
{
    /// <summary>El contorno de la máscara en la imagen (0–1) y sus vértices sueltos, para empezar a seguirla.</summary>
    public static (Vector2[] Region, Vector2[] Anchors) Start(in MaskSpec mask, IReadOnlyList<MaskVertex> vertices, float imageWidth, float imageHeight)
    {
        MaskSpec m = mask;
        var size = new Vector2(imageWidth, imageHeight);
        Vector2 ToImage(Vector2 box) => BoxToImage(m, box, size);
        Vector2[] region = vertices.Count >= 3 && mask.HasVertices
            ? [.. Outline(vertices, mask.Shape != MaskShape.Stroke).Select(ToImage)]
            : mask.Shape == MaskShape.Ellipse
                ? [.. Enumerable.Range(0, 32).Select(i => ToImage(0.5f * new Vector2(MathF.Cos(i * MathF.Tau / 32), MathF.Sin(i * MathF.Tau / 32))))]
                : [ToImage(new(-0.5f, -0.5f)), ToImage(new(0.5f, -0.5f)), ToImage(new(0.5f, 0.5f)), ToImage(new(-0.5f, 0.5f))];
        Vector2[] anchors = mask.HasVertices ? [.. vertices.Select(v => ToImage(v.Point))] : [];
        return (region, anchors);
    }

    /// <summary>
    /// El keyframe de la máscara en un cuadro seguido. <paramref name="previousRotation"/> evita saltos de 360° entre
    /// cuadros seguidos.
    /// </summary>
    public static MaskTrackKey Key(in MaskSpec start, IReadOnlyList<MaskVertex> vertices, TrackedFrame frame, float imageWidth, float imageHeight,
        double previousRotation)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return Key(start, vertices, frame.Time, frame.Map, frame.Anchors, imageWidth, imageHeight, previousRotation);
    }

    /// <summary>
    /// El keyframe de la máscara cuando cada punto de la imagen de partida (0–1) va adonde dice <paramref name="map"/>; los
    /// vértices van a <paramref name="anchors"/> si los hay (uno por vértice) o, si no y la máscara los tiene, por <paramref name="map"/>.
    /// </summary>
    public static MaskTrackKey Key(in MaskSpec start, IReadOnlyList<MaskVertex> vertices, Time time, Func<Vector2, Vector2> map,
        Vector2[]? anchors, float imageWidth, float imageHeight, double previousRotation)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(vertices);
        var size = new Vector2(imageWidth, imageHeight);
        var center = new Vector2((float)start.X, (float)start.Y);
        double radians = start.Rotation * Math.PI / 180;
        var axisX = new Vector2((float)(Math.Cos(radians) * start.Width * imageWidth / 2), (float)(Math.Sin(radians) * start.Width * imageWidth / 2));
        var axisY = new Vector2((float)(-Math.Sin(radians) * start.Height * imageHeight / 2), (float)(Math.Cos(radians) * start.Height * imageHeight / 2));
        Vector2 c = map(center);
        Vector2 ax = (map(center + (axisX / size)) - c) * size;
        Vector2 ay = (map(center + (axisY / size)) - c) * size;
        double rotation = Math.Atan2(ax.Y, ax.X) * 180 / Math.PI;
        rotation += 360 * Math.Round((previousRotation - rotation) / 360);
        double width = start.Width > 0 ? 2 * ax.Length() / imageWidth : start.Width;
        double height = start.Height > 0 ? 2 * ay.Length() / imageHeight : start.Height;
        IReadOnlyList<MaskVertex>? moved = null;
        var spec = start with { X = c.X, Y = c.Y, Width = width, Height = height, Rotation = rotation };
        if (anchors is { Length: > 0 } && anchors.Length == vertices.Count)
        {
            moved = [.. vertices.Select((v, i) => v with { Point = ImageToBox(spec, anchors[i], size) })];
        }
        else if (anchors is null && vertices.Count > 0)
        {
            // Cada vértice y sus manejadores por la misma deformación (con perspectiva, la forma cambia).
            MaskSpec from = start;
            Vector2 Moved(Vector2 box) => ImageToBox(spec, map(BoxToImage(from, box, size)), size);
            moved = [.. vertices.Select(v =>
            {
                Vector2 point = Moved(v.Point);
                return v with { Point = point, In = v.In == Vector2.Zero ? v.In : Moved(v.Point + v.In) - point,
                    Out = v.Out == Vector2.Zero ? v.Out : Moved(v.Point + v.Out) - point };
            })];
        }

        return new MaskTrackKey(time, c.X, c.Y, width, height, rotation, moved);
    }

    /// <summary>Un punto de la caja (−0,5 a 0,5) en la imagen (0–1), con el giro de la máscara.</summary>
    public static Vector2 BoxToImage(in MaskSpec m, Vector2 box, Vector2 imageSize)
    {
        float radians = (float)(m.Rotation * Math.PI / 180);
        float x = box.X * (float)m.Width * imageSize.X;
        float y = box.Y * (float)m.Height * imageSize.Y;
        (float sin, float cos) = MathF.SinCos(radians);
        return new Vector2(((float)m.X * imageSize.X) + (cos * x) - (sin * y), ((float)m.Y * imageSize.Y) + (sin * x) + (cos * y)) / imageSize;
    }

    /// <summary>Un punto de la imagen (0–1) en la caja de la máscara (−0,5 a 0,5).</summary>
    public static Vector2 ImageToBox(in MaskSpec m, Vector2 image, Vector2 imageSize)
    {
        float radians = (float)(m.Rotation * Math.PI / 180);
        Vector2 d = (image - new Vector2((float)m.X, (float)m.Y)) * imageSize;
        (float sin, float cos) = MathF.SinCos(radians);
        float lx = (cos * d.X) + (sin * d.Y), ly = (-sin * d.X) + (cos * d.Y);
        return new Vector2(lx / Math.Max((float)m.Width * imageSize.X, 1e-6f), ly / Math.Max((float)m.Height * imageSize.Y, 1e-6f));
    }

    // El contorno de los vértices con algunos puntos por tramo (las curvas se salen de la recta entre vértices).
    private static IEnumerable<Vector2> Outline(IReadOnlyList<MaskVertex> vertices, bool closed)
    {
        int n = vertices.Count;
        int segments = closed ? n : n - 1;
        for (int s = 0; s < segments; s++)
        {
            MaskVertex a = vertices[s], b = vertices[(s + 1) % n];
            for (int k = 0; k < 4; k++)
            {
                yield return MaskPath.Cubic(a.Point, a.Point + a.Out, b.Point + b.In, b.Point, k / 4f);
            }
        }
    }
}
