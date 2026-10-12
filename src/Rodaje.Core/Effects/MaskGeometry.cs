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
    Path,
    Image,
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
}

/// <summary>
/// Geometría de las máscaras: el trazo libre como texto, la caja de un trazo dibujado y la cobertura de cada forma. Es la
/// referencia de la GPU (<c>PsMask</c> en <c>Rodaje.Render</c>): si cambia una fórmula, cambia la otra.
/// </summary>
/// <remarks>
/// La forma se mide en píxeles de la imagen: el centro es (<c>x·ancho</c>, <c>y·alto</c>), la caja mide
/// <c>width·ancho</c> × <c>height·alto</c> y gira <c>rotation</c> grados. El borde suave mide <c>feather</c> veces el lado
/// corto y queda centrado en el contorno; sin él, el borde se suaviza un píxel. Varias máscaras se multiplican.
/// </remarks>
public static class MaskGeometry
{
    public const int MaxPathPoints = 256;

    /// <summary>
    /// Hueco de los valores evaluados (tras los 7 parámetros y las 3 elecciones: forma, invertir y uso) donde el motor deja
    /// los segundos del video gris que tocan en el cuadro: tiempo del origen menos <see cref="BuiltInEffects.MaskOptions.MatteStart"/>.
    /// </summary>
    public const int MatteTimeSlot = 10;

    /// <summary>Hueco del uso de la máscara (<see cref="BuiltInEffects.MaskOptions.Use"/>): 0 recorta la capa, 1 limita efectos.</summary>
    public const int UseSlot = 9;

    /// <summary>Lee una forma: un valor desconocido es un rectángulo, como en el registro.</summary>
    public static MaskShape ParseShape(string? value)
    {
        int index = value is null ? -1 : EffectRegistry.MaskShapes.IndexOf(value);
        return index < 0 ? MaskShape.Rectangle : (MaskShape)index;
    }

    public static string ShapeName(MaskShape shape) => EffectRegistry.MaskShapes[(int)shape];

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
            effect.Options.GetValueOrDefault(O.Invert) == "on");
    }

    /// <summary>
    /// Puntos del trazo como texto: <c>x,y;x,y;…</c> con hasta cuatro decimales, relativos a la caja de la máscara
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

    /// <summary>Lee un trazo; vacío si el texto no es válido o tiene menos de tres puntos (entonces la máscara no recorta).</summary>
    public static Vector2[] ParsePath(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var points = new List<Vector2>();
        foreach (Range part in text.AsSpan().Split(';'))
        {
            ReadOnlySpan<char> pair = text.AsSpan()[part];
            int comma = pair.IndexOf(',');
            if (points.Count == MaxPathPoints || comma < 0
                || !float.TryParse(pair[..comma], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                || !float.TryParse(pair[(comma + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
                || !float.IsFinite(x) || !float.IsFinite(y))
            {
                return [];
            }

            points.Add(new Vector2(x, y));
        }

        return points.Count >= 3 ? [.. points] : [];
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

    /// <summary>Cuánto deja ver la máscara en el centro de un píxel de la imagen (de 0 a 1).</summary>
    /// <param name="mask">La máscara.</param>
    /// <param name="px">Punto en píxeles de la imagen de la capa.</param>
    /// <param name="py">Punto en píxeles de la imagen de la capa.</param>
    /// <param name="imageWidth">Ancho de la imagen de la capa, en píxeles.</param>
    /// <param name="imageHeight">Alto de la imagen de la capa, en píxeles.</param>
    /// <param name="path">Puntos del trazo (<see cref="ParsePath"/>), para <see cref="MaskShape.Path"/>.</param>
    /// <param name="matte">Valor de 0 a 1 del video gris en ese punto, para <see cref="MaskShape.Image"/>.</param>
    /// <param name="pixel">Píxeles de la imagen por píxel de la máscara: el borde mínimo para suavizar.</param>
    public static double Factor(in MaskSpec mask, double px, double py, double imageWidth, double imageHeight,
        ReadOnlySpan<Vector2> path = default, double matte = 1, double pixel = 1)
    {
        double coverage;
        if (mask.Shape == MaskShape.Image)
        {
            coverage = matte;
        }
        else if (mask.Shape == MaskShape.Path && path.Length < 3)
        {
            coverage = 1;
        }
        else
        {
            double d = SignedDistance(mask, px, py, imageWidth, imageHeight, path);
            double soft = Math.Max(mask.Feather * Math.Min(imageWidth, imageHeight), pixel);
            coverage = Math.Clamp(0.5 - (d / soft), 0, 1);
        }

        return mask.Opacity * (mask.Invert ? 1 - coverage : coverage);
    }

    /// <summary>Distancia con signo al contorno en píxeles de la imagen: negativa adentro.</summary>
    public static double SignedDistance(in MaskSpec mask, double px, double py, double imageWidth, double imageHeight,
        ReadOnlySpan<Vector2> path = default)
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
                return k2 < 1e-12 ? -Math.Min(hw, hh) : k1 * (k1 - 1) / k2;
            }

            case MaskShape.Path:
                return PolygonDistance(path, lx, ly, 2 * hw, 2 * hh);
            default:
            {
                double qx = Math.Abs(lx) - hw, qy = Math.Abs(ly) - hh;
                double outside = Math.Sqrt((Math.Max(qx, 0) * Math.Max(qx, 0)) + (Math.Max(qy, 0) * Math.Max(qy, 0)));
                return outside + Math.Min(Math.Max(qx, qy), 0);
            }
        }
    }

    // Distancia a un polígono cerrado con signo por paridad (Quílez); los puntos se escalan a la caja.
    private static double PolygonDistance(ReadOnlySpan<Vector2> path, double x, double y, double width, double height)
    {
        double vx = path[0].X * width, vy = path[0].Y * height;
        double best = ((x - vx) * (x - vx)) + ((y - vy) * (y - vy));
        double sign = 1;
        for (int i = 0, j = path.Length - 1; i < path.Length; j = i, i++)
        {
            double ix = path[i].X * width, iy = path[i].Y * height;
            double jx = path[j].X * width, jy = path[j].Y * height;
            double ex = jx - ix, ey = jy - iy, wx = x - ix, wy = y - iy;
            double t = Math.Clamp(((wx * ex) + (wy * ey)) / Math.Max((ex * ex) + (ey * ey), 1e-12), 0, 1);
            double bx = wx - (ex * t), by = wy - (ey * t);
            best = Math.Min(best, (bx * bx) + (by * by));
            bool c1 = y >= iy, c2 = y < jy, c3 = ex * wy > ey * wx;
            if ((c1 && c2 && c3) || (!c1 && !c2 && !c3))
            {
                sign = -sign;
            }
        }

        return sign * Math.Sqrt(best);
    }

    private static double SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float length = ab.LengthSquared();
        float t = length < 1e-12f ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / length, 0, 1);
        return Vector2.Distance(p, a + (ab * t));
    }
}
