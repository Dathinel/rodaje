using System.Globalization;
using System.Numerics;
using System.Text;
using Rodaje.Core.Model;
using M = Rodaje.Core.Model.BuiltInEffects.MaskParameters;
using O = Rodaje.Core.Model.BuiltInEffects.MaskOptions;

namespace Rodaje.Core.Effects;

/// <summary>Forma de <see cref="BuiltInEffects.Mask"/>, en el orden de <see cref="EffectRegistry.MaskShapes"/>.</summary>
public enum MaskShape
{
    Rectangle,
    Ellipse,

    /// <summary>Trazo libre de antes del formato 7: polígono de esquinas (se dibuja como una Bézier sin manejadores).</summary>
    Path,
    Image,

    /// <summary>Contorno cerrado de vértices con manejadores, con borde suave por vértice.</summary>
    Bezier,

    /// <summary>Pluma de trazo: una línea abierta con grosor por vértice.</summary>
    Stroke,
}

/// <summary>
/// Cómo se combina una máscara con lo que llevan las anteriores del mismo destino (operadores de Porter y Duff sobre la
/// cobertura), en el orden de <see cref="EffectRegistry.MaskModes"/>.
/// </summary>
public enum MaskMode
{
    /// <summary>Unión: <c>a + m − a·m</c>.</summary>
    Add,

    /// <summary>Resta: <c>a·(1 − m)</c>.</summary>
    Subtract,

    /// <summary>Intersección: <c>a·m</c> (lo que hacían todas antes del formato 7).</summary>
    Intersect,

    /// <summary>Diferencia (o exclusiva): <c>a + m − 2·a·m</c>.</summary>
    Difference,
}

/// <summary>Una máscara evaluada en un instante, en coordenadas de la imagen de la capa (0–1).</summary>
public readonly record struct MaskSpec(
    MaskShape Shape,
    double X,
    double Y,
    double Width,
    double Height,
    double Rotation,
    double Feather,
    double Opacity,
    bool Invert)
{
    public static readonly MaskSpec Default = new(MaskShape.Rectangle, 0.5, 0.5, 0.5, 0.5, 0, 0, 1, false);

    /// <summary>Agranda o achica el contorno, en fracción del lado corto de la imagen.</summary>
    public double Expansion { get; init; }

    /// <summary>Radio de las esquinas del rectángulo, en fracción de la mitad de su lado corto.</summary>
    public double Roundness { get; init; }

    /// <summary>Grosor de la pluma de trazo, en fracción del lado corto de la imagen.</summary>
    public double Thickness { get; init; } = 0.02;

    public MaskMode Mode { get; init; } = MaskMode.Intersect;

    /// <summary>Si su contorno sale de vértices (<see cref="MaskPath"/>).</summary>
    public bool HasVertices => Shape is MaskShape.Path or MaskShape.Bezier or MaskShape.Stroke;
}

/// <summary>
/// Geometría de las máscaras: lectura del efecto, cobertura de cada forma y combinación. Es la referencia de la GPU
/// (<c>PsMask</c> en <c>Rodaje.Render</c>): si cambia una fórmula, cambia la otra.
/// </summary>
/// <remarks>
/// La forma se mide en píxeles de la imagen: el centro es (<c>x·ancho</c>, <c>y·alto</c>), la caja mide
/// <c>width·ancho</c> × <c>height·alto</c> y gira <c>rotation</c> grados. Los vértices se aplanan a una polilínea
/// (<see cref="MaskPath.Flatten"/>) y la distancia al contorno se mide a esa polilínea, con el peso del punto más cercano.
/// El borde suave mide <c>feather</c> veces el lado corto (por el peso del vértice en una Bézier) y queda centrado en el
/// contorno, corrido <c>expansion</c> veces el lado corto; sin borde, se suaviza un píxel.
/// </remarks>
public static class MaskGeometry
{
    /// <summary>Tope de puntos del trazo libre de antes (<see cref="MaskShape.Path"/>).</summary>
    public const int MaxPathPoints = 256;

    /// <summary>Huecos de los valores evaluados: tras los 10 parámetros van la forma, invertir, el modo y el uso.</summary>
    public const int ShapeSlot = 10;

    public const int InvertSlot = 11;

    public const int ModeSlot = 12;

    /// <summary>Hueco del uso de la máscara (<see cref="BuiltInEffects.MaskOptions.Use"/>): 0 recorta la capa, 1 limita efectos.</summary>
    public const int UseSlot = 13;

    /// <summary>
    /// Hueco donde el motor deja los segundos del origen en el cuadro menos <see cref="BuiltInEffects.MaskOptions.MatteStart"/>
    /// (0 si no hay): el cuadro del video gris y el instante de la forma animada.
    /// </summary>
    public const int MatteTimeSlot = 14;

    /// <summary>
    /// Hueco donde el motor deja a qué limita la máscara: 0 la capa (también si nadie lo escribe), <c>k + 1</c> el efecto
    /// <c>k</c> de la lista de la capa, o <see cref="NoTarget"/> si su efecto está apagado o no existe (no hace nada).
    /// </summary>
    public const int TargetSlot = 15;

    public const int NoTarget = -1;

    /// <summary>Lee una forma: un valor desconocido es un rectángulo, como en el registro.</summary>
    public static MaskShape ParseShape(string? value)
    {
        int index = value is null ? -1 : EffectRegistry.MaskShapes.IndexOf(value);
        return index < 0 ? MaskShape.Rectangle : (MaskShape)index;
    }

    public static string ShapeName(MaskShape shape) => EffectRegistry.MaskShapes[(int)shape];

    /// <summary>Lee un modo; sin la opción, intersección (como se combinaban antes).</summary>
    public static MaskMode ParseMode(string? value)
    {
        int index = value is null ? -1 : EffectRegistry.MaskModes.IndexOf(value);
        return index < 0 ? MaskMode.Intersect : (MaskMode)index;
    }

    public static string ModeName(MaskMode mode) => EffectRegistry.MaskModes[(int)mode];

    /// <summary>La máscara de <paramref name="effect"/> en <paramref name="sourceTime"/>, con los valores dentro del registro.</summary>
    public static MaskSpec Evaluate(EffectInstance effect, Timing.Time sourceTime)
    {
        ArgumentNullException.ThrowIfNull(effect);
        EffectDefinition definition = EffectRegistry.Find(BuiltInEffects.Mask)!;
        double Value(string name)
        {
            ParameterDefinition p = definition.FindParameter(name)!;
            return p.Clamp(effect.Parameters.TryGetValue(name, out Parameter? parameter) ? parameter.Evaluate(sourceTime) : p.DefaultValue);
        }

        return new MaskSpec(
            ParseShape(effect.Options.GetValueOrDefault(O.Shape)),
            Value(M.X), Value(M.Y), Value(M.Width), Value(M.Height), Value(M.Rotation), Value(M.Feather), Value(M.Opacity),
            effect.Options.GetValueOrDefault(O.Invert) == "on")
        {
            Expansion = Value(M.Expansion),
            Roundness = Value(M.Roundness),
            Thickness = Value(M.Thickness),
            Mode = ParseMode(effect.Options.GetValueOrDefault(O.Mode)),
        };
    }

    /// <summary>El texto del contorno que lee el render: la forma animada si tiene, o los vértices fijos (o el trazo de antes).</summary>
    public static string? ShapeText(EffectInstance effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        return ParseShape(effect.Options.GetValueOrDefault(O.Shape)) switch
        {
            MaskShape.Path => effect.Options.GetValueOrDefault(O.Path),
            MaskShape.Bezier or MaskShape.Stroke => effect.Options.GetValueOrDefault(O.ShapeKeys) is { Length: > 0 } keys
                ? keys
                : effect.Options.GetValueOrDefault(O.Points),
            _ => null,
        };
    }

    /// <summary>Los vértices de la máscara en <paramref name="sourceTime"/> (vacío si no tiene).</summary>
    public static MaskVertex[] Vertices(EffectInstance effect, Timing.Time sourceTime) =>
        MaskShapeTrack.Parse(ShapeText(effect))?.Evaluate(sourceTime) ?? [];

    /// <summary>Si la forma está animada (tiene keyframes de forma).</summary>
    public static bool IsShapeAnimated(EffectInstance effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        return effect.Options.GetValueOrDefault(O.ShapeKeys) is { Length: > 0 };
    }

    /// <summary>
    /// La polilínea que dibujan la GPU y la referencia: vértices aplanados para una caja de
    /// <paramref name="boxWidth"/> × <paramref name="boxHeight"/> píxeles de la máscara. Vacía si no alcanza.
    /// </summary>
    /// <param name="shape">Bézier, pluma o trazo de antes.</param>
    /// <param name="vertices">Los vértices relativos a la caja.</param>
    /// <param name="boxWidth">Ancho de la caja en píxeles de la imagen.</param>
    /// <param name="boxHeight">Alto de la caja en píxeles de la imagen.</param>
    /// <param name="featherPixels">Borde suave en píxeles de la imagen: con más borde se admite más error (<see cref="MaskPath.ToleranceFor"/>).</param>
    public static Vector3[] Polyline(MaskShape shape, ReadOnlySpan<MaskVertex> vertices, float boxWidth, float boxHeight, float featherPixels = 0)
    {
        Span<Vector3> buffer = new Vector3[MaskPath.MaxPolyline];
        int count = MaskPath.Flatten(vertices, shape != MaskShape.Stroke, boxWidth, boxHeight, buffer, MaskPath.ToleranceFor(featherPixels));
        return count < MinimumPoints(shape) ? [] : buffer[..count].ToArray();
    }

    /// <summary>Puntos que necesita la polilínea para recortar algo: tres cerrada, dos abierta.</summary>
    public static int MinimumPoints(MaskShape shape) => shape == MaskShape.Stroke ? 2 : 3;

    /// <summary>
    /// Puntos del trazo de antes como texto: <c>x,y;x,y;…</c> con hasta cuatro decimales, relativos a la caja de la máscara
    /// (−0,5 a 0,5 en cada eje).
    /// </summary>
    public static string FormatPath(IReadOnlyList<Vector2> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        var text = new StringBuilder(points.Count * 16);
        for (int i = 0; i < Math.Min(points.Count, MaxPathPoints); i++)
        {
            if (i > 0)
            {
                text.Append(';');
            }

            text.Append(Math.Round(points[i].X, 4).ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(Math.Round(points[i].Y, 4).ToString(CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    /// <summary>Lee un trazo de antes; vacío si el texto no es válido o tiene menos de tres puntos.</summary>
    public static Vector2[] ParsePath(string? text)
    {
        MaskVertex[] vertices = MaskPath.Parse(text);
        return vertices.Length >= 3 && vertices.Length <= MaxPathPoints && vertices.All(v => v.IsCorner)
            ? [.. vertices.Select(v => v.Point)]
            : [];
    }

    /// <summary>
    /// De un trazo dibujado en coordenadas de la imagen (0–1) a la máscara: su caja (centro y tamaño) y los puntos relativos
    /// a ella, simplificados hasta <paramref name="tolerance"/> (fracción de la caja) y a lo sumo <see cref="MaxPathPoints"/>.
    /// </summary>
    public static (double X, double Y, double Width, double Height, Vector2[] Points) FromStroke(IReadOnlyList<Vector2> stroke, double tolerance = 0.004)
    {
        ArgumentNullException.ThrowIfNull(stroke);
        if (stroke.Count < 3)
        {
            throw new ArgumentException("Un trazo necesita al menos tres puntos.", nameof(stroke));
        }

        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (Vector2 p in stroke)
        {
            minX = Math.Min(minX, p.X);
            minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X);
            maxY = Math.Max(maxY, p.Y);
        }

        float width = Math.Max(maxX - minX, 1e-4f);
        float height = Math.Max(maxY - minY, 1e-4f);
        var center = new Vector2((minX + maxX) / 2, (minY + maxY) / 2);
        List<Vector2> local = [.. stroke.Select(p => new Vector2((p.X - center.X) / width, (p.Y - center.Y) / height))];
        List<Vector2> simple = Simplify(local, tolerance);
        while (simple.Count > MaxPathPoints)
        {
            tolerance *= 1.5;
            simple = Simplify(local, tolerance);
        }

        return (center.X, center.Y, width, height, [.. simple]);
    }

    /// <summary>
    /// Un trazo a mano alzada como Bézier suave: se simplifica en píxeles (<paramref name="tolerancePixels"/>) y cada punto
    /// que queda lleva manejadores de Catmull–Rom. Los puntos van en coordenadas de la imagen (0–1).
    /// </summary>
    public static MaskVertex[] SmoothStroke(IReadOnlyList<Vector2> stroke, float imageWidth, float imageHeight, bool closed,
        double tolerancePixels = 2.5)
    {
        ArgumentNullException.ThrowIfNull(stroke);
        var scale = new Vector2(Math.Max(imageWidth, 1), Math.Max(imageHeight, 1));
        List<Vector2> pixels = Simplify([.. stroke.Select(p => p * scale)], tolerancePixels);
        while (pixels.Count > MaskPath.MaxVertices)
        {
            tolerancePixels *= 1.5;
            pixels = Simplify([.. stroke.Select(p => p * scale)], tolerancePixels);
        }

        if (closed && pixels.Count > 3 && Vector2.Distance(pixels[0], pixels[^1]) < tolerancePixels * 2)
        {
            pixels.RemoveAt(pixels.Count - 1);
        }

        return MaskPath.Smooth([.. pixels.Select(p => p / scale)], closed, 0.85f);
    }

    /// <summary>Ramer–Douglas–Peucker sobre un trazo abierto (se cierra solo al dibujarlo).</summary>
    public static List<Vector2> Simplify(IReadOnlyList<Vector2> points, double tolerance)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < 3)
        {
            return [.. points];
        }

        bool[] keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, points.Count - 1));
        while (stack.Count > 0)
        {
            (int first, int last) = stack.Pop();
            double worst = 0;
            int index = -1;
            for (int i = first + 1; i < last; i++)
            {
                double d = SegmentDistance(points[i], points[first], points[last]);
                if (d > worst)
                {
                    worst = d;
                    index = i;
                }
            }

            if (index >= 0 && worst > tolerance)
            {
                keep[index] = true;
                stack.Push((first, index));
                stack.Push((index, last));
            }
        }

        return [.. points.Where((_, i) => keep[i])];
    }

    /// <summary>Cuánto deja ver la máscara en el centro de un píxel de la imagen (de 0 a 1), ya con invertir y opacidad.</summary>
    /// <param name="mask">La máscara.</param>
    /// <param name="px">Punto en píxeles de la imagen de la capa.</param>
    /// <param name="py">Punto en píxeles de la imagen de la capa.</param>
    /// <param name="imageWidth">Ancho de la imagen de la capa, en píxeles.</param>
    /// <param name="imageHeight">Alto de la imagen de la capa, en píxeles.</param>
    /// <param name="polyline">Contorno aplanado (<see cref="Polyline"/>), para las formas con vértices.</param>
    /// <param name="matte">Valor de 0 a 1 del video gris en ese punto, para <see cref="MaskShape.Image"/>.</param>
    /// <param name="pixel">Píxeles de la imagen por píxel de la máscara: el borde mínimo para suavizar.</param>
    public static double Factor(in MaskSpec mask, double px, double py, double imageWidth, double imageHeight,
        ReadOnlySpan<Vector3> polyline = default, double matte = 1, double pixel = 1)
    {
        double coverage;
        if (mask.Shape == MaskShape.Image)
        {
            coverage = matte;
        }
        else if (mask.HasVertices && polyline.Length < MinimumPoints(mask.Shape))
        {
            coverage = 1;
        }
        else
        {
            double shortSide = Math.Min(imageWidth, imageHeight);
            (double d, double weight) = SignedDistance(mask, px, py, imageWidth, imageHeight, polyline);
            d -= mask.Expansion * shortSide;
            double feather = mask.Feather * shortSide * (mask.Shape == MaskShape.Bezier ? weight : 1);
            double soft = Math.Max(feather, pixel);
            coverage = Math.Clamp(0.5 - (d / soft), 0, 1);
        }

        return mask.Opacity * (mask.Invert ? 1 - coverage : coverage);
    }

    /// <summary>
    /// Distancia con signo al contorno en píxeles de la imagen (negativa adentro) y el peso del punto del contorno más
    /// cercano (1 en rectángulo y elipse). En la pluma de trazo ya resta el medio grosor.
    /// </summary>
    public static (double Distance, double Weight) SignedDistance(in MaskSpec mask, double px, double py, double imageWidth, double imageHeight,
        ReadOnlySpan<Vector3> polyline = default)
    {
        double radians = mask.Rotation * Math.PI / 180;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        double dx = px - (mask.X * imageWidth), dy = py - (mask.Y * imageHeight);
        // Al sistema de la caja: se deshace el giro (horario con y hacia abajo).
        double lx = (cos * dx) + (sin * dy);
        double ly = (-sin * dx) + (cos * dy);
        double hw = Math.Max(mask.Width * imageWidth / 2, 1e-6);
        double hh = Math.Max(mask.Height * imageHeight / 2, 1e-6);
        switch (mask.Shape)
        {
            case MaskShape.Ellipse:
            {
                double k1 = Math.Sqrt((lx * lx / (hw * hw)) + (ly * ly / (hh * hh)));
                double k2 = Math.Sqrt((lx * lx / (hw * hw * hw * hw)) + (ly * ly / (hh * hh * hh * hh)));
                return (k2 < 1e-12 ? -Math.Min(hw, hh) : k1 * (k1 - 1) / k2, 1);
            }

            case MaskShape.Path or MaskShape.Bezier:
                return PolylineDistance(polyline, lx, ly, 2 * hw, 2 * hh, closed: true);
            case MaskShape.Stroke:
            {
                (double d, double w) = PolylineDistance(polyline, lx, ly, 2 * hw, 2 * hh, closed: false);
                return (d - (mask.Thickness * Math.Min(imageWidth, imageHeight) * w / 2), w);
            }

            default:
            {
                double r = Math.Clamp(mask.Roundness, 0, 1) * Math.Min(hw, hh);
                double qx = Math.Abs(lx) - hw + r, qy = Math.Abs(ly) - hh + r;
                double outside = Math.Sqrt((Math.Max(qx, 0) * Math.Max(qx, 0)) + (Math.Max(qy, 0) * Math.Max(qy, 0)));
                return (outside + Math.Min(Math.Max(qx, qy), 0) - r, 1);
            }
        }
    }

    /// <summary>
    /// Combina las coberturas de las máscaras de un mismo destino en orden: arranca en 0 si la primera suma o hace la
    /// diferencia, y en 1 si resta o interseca.
    /// </summary>
    public static double Combine(ReadOnlySpan<(MaskMode Mode, double Factor)> masks)
    {
        if (masks.IsEmpty)
        {
            return 1;
        }

        double a = StartValue(masks[0].Mode);
        foreach ((MaskMode mode, double m) in masks)
        {
            a = Apply(mode, a, m);
        }

        return a;
    }

    /// <summary>Con qué arranca la combinación según el modo de la primera máscara.</summary>
    public static double StartValue(MaskMode first) => first is MaskMode.Add or MaskMode.Difference ? 0 : 1;

    public static double Apply(MaskMode mode, double a, double m) => mode switch
    {
        MaskMode.Add => a + m - (a * m),
        MaskMode.Subtract => a * (1 - m),
        MaskMode.Difference => a + m - (2 * a * m),
        _ => a * m,
    };

    // Distancia a una polilínea (con signo por paridad si es cerrada, según Quílez) y peso interpolado en el tramo más
    // cercano; los puntos se escalan a la caja.
    private static (double Distance, double Weight) PolylineDistance(ReadOnlySpan<Vector3> path, double x, double y, double width, double height,
        bool closed)
    {
        int n = path.Length;
        double best = double.MaxValue;
        double weight = 1;
        double sign = 1;
        int segments = closed ? n : n - 1;
        for (int k = 0; k < segments; k++)
        {
            Vector3 a = path[k];
            Vector3 b = path[(k + 1) % n];
            double ix = a.X * width, iy = a.Y * height;
            double jx = b.X * width, jy = b.Y * height;
            double ex = jx - ix, ey = jy - iy, wx = x - ix, wy = y - iy;
            double t = Math.Clamp(((wx * ex) + (wy * ey)) / Math.Max((ex * ex) + (ey * ey), 1e-12), 0, 1);
            double bx = wx - (ex * t), by = wy - (ey * t);
            double d = (bx * bx) + (by * by);
            if (d < best)
            {
                best = d;
                weight = a.Z + ((b.Z - a.Z) * t);
            }

            bool c1 = y >= iy, c2 = y < jy, c3 = ex * wy > ey * wx;
            if (closed && ((c1 && c2 && c3) || (!c1 && !c2 && !c3)))
            {
                sign = -sign;
            }
        }

        return (sign * Math.Sqrt(best), weight);
    }

    private static double SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float length = ab.LengthSquared();
        float t = length < 1e-12f ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / length, 0, 1);
        return Vector2.Distance(p, a + (ab * t));
    }
}
