using System.Globalization;
using System.Numerics;
using System.Text;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Effects;

/// <summary>
/// Un vértice de una máscara Bézier o de la pluma de trazo, relativo a la caja de la máscara (−0,5 a 0,5 en cada eje).
/// </summary>
/// <param name="Point">Posición del vértice.</param>
/// <param name="In">Manejador de entrada, relativo al vértice (cero en una esquina).</param>
/// <param name="Out">Manejador de salida, relativo al vértice.</param>
/// <param name="Weight">
/// Multiplicador propio del vértice: del borde suave en una Bézier (borde variable) y del grosor en la pluma de trazo.
/// </param>
public readonly record struct MaskVertex(Vector2 Point, Vector2 In, Vector2 Out, float Weight = 1)
{
    public MaskVertex(float x, float y)
        : this(new Vector2(x, y), Vector2.Zero, Vector2.Zero)
    {
    }

    /// <summary>Sin manejadores: el contorno quiebra en el vértice.</summary>
    public bool IsCorner => In == Vector2.Zero && Out == Vector2.Zero;

    public MaskVertex Lerp(in MaskVertex other, float t) => new(
        Vector2.Lerp(Point, other.Point, t), Vector2.Lerp(In, other.In, t), Vector2.Lerp(Out, other.Out, t), Weight + ((other.Weight - Weight) * t));
}

/// <summary>
/// Contornos de las máscaras Bézier y de la pluma de trazo: el texto de los vértices, el aplanado a una polilínea (lo que
/// dibujan la GPU y la referencia de la CPU) y las formas de partida (polígono, estrella, rectángulo y elipse en Bézier).
/// </summary>
/// <remarks>
/// Texto: <c>x,y[,inX,inY,outX,outY[,peso]];…</c> con hasta cuatro decimales. Un vértice con dos números es una esquina
/// de peso 1, así que el trazo libre de antes (<c>path</c>) se lee igual.
/// </remarks>
public static class MaskPath
{
    public const int MaxVertices = 256;

    public const int MaxTextLength = 32_768;

    /// <summary>Tope de puntos de la polilínea que recibe la GPU.</summary>
    public const int MaxPolyline = 1024;

    /// <summary>Error máximo del aplanado con borde duro, en píxeles de la imagen.</summary>
    public const float FlattenTolerance = 0.2f;

    /// <summary>
    /// Error admitido del aplanado según el borde suave (en píxeles de la imagen): un 2 % del borde se pierde en la rampa y
    /// ahorra tramos que la GPU recorre en cada píxel.
    /// </summary>
    public static float ToleranceFor(float featherPixels) => Math.Clamp(FlattenTolerance + (0.02f * featherPixels), FlattenTolerance, 4);

    private const int MaxSegmentSteps = 64;

    public static string Format(IReadOnlyList<MaskVertex> vertices)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        var text = new StringBuilder(vertices.Count * 40);
        for (int i = 0; i < Math.Min(vertices.Count, MaxVertices); i++)
        {
            if (i > 0)
            {
                text.Append(';');
            }

            Append(text, vertices[i]);
        }

        return text.ToString();
    }

    internal static void Append(StringBuilder text, in MaskVertex v)
    {
        Number(text, v.Point.X).Append(',');
        Number(text, v.Point.Y);
        bool weighted = Math.Abs(v.Weight - 1) > 1e-4f;
        if (!v.IsCorner || weighted)
        {
            Number(text.Append(','), v.In.X).Append(',');
            Number(text, v.In.Y).Append(',');
            Number(text, v.Out.X).Append(',');
            Number(text, v.Out.Y);
        }

        if (weighted)
        {
            Number(text.Append(','), v.Weight);
        }
    }

    private static StringBuilder Number(StringBuilder text, float value)
    {
        double rounded = Math.Round(value, 4);
        return text.Append((rounded == 0 ? 0 : rounded).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Lee los vértices; vacío si el texto no es válido o tiene menos de dos.</summary>
    public static MaskVertex[] Parse(string? text) => Parse(text.AsSpan());

    public static MaskVertex[] Parse(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return [];
        }

        var vertices = new List<MaskVertex>();
        Span<float> n = stackalloc float[7];
        foreach (Range part in text.Split(';'))
        {
            ReadOnlySpan<char> item = text[part];
            int count = 0;
            foreach (Range field in item.Split(','))
            {
                if (count == 7 || !float.TryParse(item[field], NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                    || !float.IsFinite(value))
                {
                    return [];
                }

                n[count++] = value;
            }

            if (vertices.Count == MaxVertices || count is not (2 or 6 or 7))
            {
                return [];
            }

            vertices.Add(new MaskVertex(new Vector2(n[0], n[1]), count >= 6 ? new Vector2(n[2], n[3]) : Vector2.Zero,
                count >= 6 ? new Vector2(n[4], n[5]) : Vector2.Zero, count == 7 ? Math.Clamp(n[6], 0, 16) : 1));
        }

        return vertices.Count >= 2 ? [.. vertices] : [];
    }

    /// <summary>
    /// Aplana el contorno a una polilínea relativa a la caja (x, y) con el peso de cada punto (z). Cada tramo cúbico se
    /// parte en los pasos justos para no apartarse más de <paramref name="tolerance"/> píxeles de la curva, sabiendo que
    /// la caja mide <paramref name="boxWidth"/> × <paramref name="boxHeight"/> píxeles. Cerrado, el último punto no repite
    /// el primero.
    /// </summary>
    /// <returns>Cuántos puntos escribió en <paramref name="output"/> (a lo sumo su largo).</returns>
    public static int Flatten(ReadOnlySpan<MaskVertex> vertices, bool closed, float boxWidth, float boxHeight, Span<Vector3> output,
        float tolerance = FlattenTolerance)
    {
        int n = vertices.Length;
        if (n < 2 || output.Length < 2)
        {
            return 0;
        }

        int segments = closed ? n : n - 1;
        var scale = new Vector2(Math.Max(boxWidth, 1e-3f), Math.Max(boxHeight, 1e-3f));
        tolerance = Math.Max(tolerance, 1e-3f);
        // Pasos por tramo; si no caben, se afloja la tolerancia hasta que entren (un contorno enorme y muy curvo).
        Span<int> steps = segments <= 256 ? stackalloc int[segments] : new int[segments];
        while (true)
        {
            int total = closed ? 0 : 1;
            for (int s = 0; s < segments; s++)
            {
                steps[s] = Steps(vertices[s], vertices[(s + 1) % n], scale, tolerance);
                total += steps[s];
            }

            if (total <= output.Length)
            {
                break;
            }

            tolerance *= 2;
            if (tolerance > 1e6f)
            {
                return 0;
            }
        }

        int written = 0;
        for (int s = 0; s < segments; s++)
        {
            MaskVertex a = vertices[s];
            MaskVertex b = vertices[(s + 1) % n];
            Vector2 p0 = a.Point, p1 = a.Point + a.Out, p2 = b.Point + b.In, p3 = b.Point;
            for (int k = 0; k < steps[s]; k++)
            {
                float t = (float)k / steps[s];
                output[written++] = new Vector3(Cubic(p0, p1, p2, p3, t), a.Weight + ((b.Weight - a.Weight) * t));
            }
        }

        if (!closed)
        {
            MaskVertex last = vertices[n - 1];
            output[written++] = new Vector3(last.Point, last.Weight);
        }

        return written;
    }

    public static Vector2 Cubic(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float u = 1 - t;
        return (u * u * u * p0) + (3 * u * u * t * p1) + (3 * u * t * t * p2) + (t * t * t * p3);
    }

    // Cota del error del aplanado uniforme: |B''| ≤ 6·máx(|d1|, |d2|) y el error ≤ |B''|/(8n²).
    private static int Steps(in MaskVertex a, in MaskVertex b, Vector2 scale, float tolerance)
    {
        if (a.Out == Vector2.Zero && b.In == Vector2.Zero)
        {
            return 1;
        }

        Vector2 p0 = a.Point * scale, p1 = (a.Point + a.Out) * scale, p2 = (b.Point + b.In) * scale, p3 = b.Point * scale;
        float m = Math.Max((p0 - (2 * p1) + p2).Length(), (p1 - (2 * p2) + p3).Length());
        return Math.Clamp((int)MathF.Ceiling(MathF.Sqrt(0.75f * m / tolerance)), 1, MaxSegmentSteps);
    }

    /// <summary>
    /// Manejadores suaves (Catmull–Rom) para unos vértices: cada uno apunta como la recta entre sus vecinos, con un tercio
    /// de la distancia a cada lado. Abierto, los extremos quedan como esquina.
    /// </summary>
    public static MaskVertex[] Smooth(IReadOnlyList<Vector2> points, bool closed, float tension = 1)
    {
        ArgumentNullException.ThrowIfNull(points);
        int n = points.Count;
        var vertices = new MaskVertex[n];
        for (int i = 0; i < n; i++)
        {
            bool end = !closed && (i == 0 || i == n - 1);
            if (end || n < 3)
            {
                vertices[i] = new MaskVertex(points[i].X, points[i].Y);
                continue;
            }

            Vector2 previous = points[(i - 1 + n) % n], next = points[(i + 1) % n];
            Vector2 direction = next - previous;
            float length = direction.Length();
            if (length < 1e-6f)
            {
                vertices[i] = new MaskVertex(points[i].X, points[i].Y);
                continue;
            }

            direction /= length;
            float before = Vector2.Distance(points[i], previous) / 3 * tension;
            float after = Vector2.Distance(points[i], next) / 3 * tension;
            vertices[i] = new MaskVertex(points[i], -direction * before, direction * after);
        }

        return vertices;
    }

    /// <summary>Convierte un vértice en esquina o en suave (manejadores alineados según sus vecinos).</summary>
    public static MaskVertex Toggle(IReadOnlyList<MaskVertex> vertices, int index, bool closed)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        MaskVertex v = vertices[index];
        if (!v.IsCorner)
        {
            return v with { In = Vector2.Zero, Out = Vector2.Zero };
        }

        int n = vertices.Count;
        Vector2 previous = index > 0 || closed ? vertices[(index - 1 + n) % n].Point : v.Point;
        Vector2 next = index < n - 1 || closed ? vertices[(index + 1) % n].Point : v.Point;
        Vector2 direction = next - previous;
        if (direction.LengthSquared() < 1e-10f)
        {
            direction = new Vector2(1, 0);
        }

        direction = Vector2.Normalize(direction);
        float before = Math.Max(Vector2.Distance(v.Point, previous), 0.05f) / 3;
        float after = Math.Max(Vector2.Distance(v.Point, next), 0.05f) / 3;
        return v with { In = -direction * before, Out = direction * after };
    }

    /// <summary>
    /// Parte el tramo que empieza en <paramref name="segment"/> en <paramref name="t"/> (de Casteljau): devuelve los
    /// vértices con el nuevo en <c>segment + 1</c> y la curva igual.
    /// </summary>
    public static MaskVertex[] Split(IReadOnlyList<MaskVertex> vertices, int segment, float t, bool closed)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        int n = vertices.Count;
        if (segment < 0 || segment >= (closed ? n : n - 1) || n >= MaxVertices)
        {
            return [.. vertices];
        }

        t = Math.Clamp(t, 0.001f, 0.999f);
        MaskVertex a = vertices[segment];
        MaskVertex b = vertices[(segment + 1) % n];
        Vector2 p0 = a.Point, p1 = a.Point + a.Out, p2 = b.Point + b.In, p3 = b.Point;
        Vector2 q0 = Vector2.Lerp(p0, p1, t), q1 = Vector2.Lerp(p1, p2, t), q2 = Vector2.Lerp(p2, p3, t);
        Vector2 r0 = Vector2.Lerp(q0, q1, t), r1 = Vector2.Lerp(q1, q2, t);
        Vector2 s = Vector2.Lerp(r0, r1, t);
        bool straight = a.Out == Vector2.Zero && b.In == Vector2.Zero;
        var list = new List<MaskVertex>(vertices);
        list[segment] = a with { Out = straight ? Vector2.Zero : q0 - p0 };
        int bIndex = (segment + 1) % n;
        list[bIndex] = b with { In = straight ? Vector2.Zero : q2 - p3 };
        var middle = new MaskVertex(s, straight ? Vector2.Zero : r0 - s, straight ? Vector2.Zero : r1 - s, a.Weight + ((b.Weight - a.Weight) * t));
        list.Insert(segment + 1, middle);
        return [.. list];
    }

    /// <summary>El punto del contorno más cercano a <paramref name="point"/> (relativo a la caja): tramo, parámetro y distancia en píxeles.</summary>
    public static (int Segment, float T, float Distance) Nearest(IReadOnlyList<MaskVertex> vertices, bool closed, Vector2 point, Vector2 boxPixels)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        int n = vertices.Count;
        (int, float, float) best = (-1, 0, float.MaxValue);
        int segments = closed ? n : n - 1;
        for (int s = 0; s < segments; s++)
        {
            MaskVertex a = vertices[s];
            MaskVertex b = vertices[(s + 1) % n];
            Vector2 p0 = a.Point, p1 = a.Point + a.Out, p2 = b.Point + b.In, p3 = b.Point;
            const int samples = 32;
            Vector2 previous = p0 * boxPixels;
            for (int k = 1; k <= samples; k++)
            {
                float t = (float)k / samples;
                Vector2 current = Cubic(p0, p1, p2, p3, t) * boxPixels;
                Vector2 edge = current - previous;
                float length = edge.LengthSquared();
                float u = length < 1e-12f ? 0 : Math.Clamp(Vector2.Dot((point * boxPixels) - previous, edge) / length, 0, 1);
                float distance = Vector2.Distance(point * boxPixels, previous + (edge * u));
                if (distance < best.Item3)
                {
                    best = (s, (k - 1 + u) / samples, distance);
                }

                previous = current;
            }
        }

        return best;
    }

    /// <summary>Polígono regular de <paramref name="sides"/> lados inscrito en la caja, con un vértice arriba.</summary>
    public static MaskVertex[] Polygon(int sides)
    {
        sides = Math.Clamp(sides, 3, 64);
        return [.. Enumerable.Range(0, sides).Select(i =>
        {
            double a = (-Math.PI / 2) + (i * 2 * Math.PI / sides);
            return new MaskVertex((float)(0.5 * Math.Cos(a)), (float)(0.5 * Math.Sin(a)));
        })];
    }

    /// <summary>Estrella de <paramref name="points"/> puntas; el radio de adentro es <paramref name="inner"/> del de afuera.</summary>
    public static MaskVertex[] Star(int points, double inner = 0.45)
    {
        points = Math.Clamp(points, 3, 64);
        inner = Math.Clamp(inner, 0.05, 1);
        return [.. Enumerable.Range(0, 2 * points).Select(i =>
        {
            double a = (-Math.PI / 2) + (i * Math.PI / points);
            double r = i % 2 == 0 ? 0.5 : 0.5 * inner;
            return new MaskVertex((float)(r * Math.Cos(a)), (float)(r * Math.Sin(a)));
        })];
    }

    /// <summary>La caja entera como cuatro esquinas (para convertir un rectángulo en Bézier).</summary>
    public static MaskVertex[] Rectangle() =>
        [new(-0.5f, -0.5f), new(0.5f, -0.5f), new(0.5f, 0.5f), new(-0.5f, 0.5f)];

    /// <summary>
    /// Un rectángulo de esquinas redondeadas como ocho vértices en sentido horario: cada esquina es un arco de cuarto de
    /// elipse con manejadores κ·r hacia la esquina. El radio es <paramref name="roundness"/> de la mitad del lado corto de
    /// una caja de <paramref name="boxWidth"/> × <paramref name="boxHeight"/> (en cualquier unidad común).
    /// </summary>
    public static MaskVertex[] RoundedRectangle(double boxWidth, double boxHeight, double roundness)
    {
        double r = Math.Clamp(roundness, 0, 1) * Math.Min(boxWidth, boxHeight) / 2;
        float rx = (float)(r / Math.Max(boxWidth, 1e-9)), ry = (float)(r / Math.Max(boxHeight, 1e-9));
        const float k = 0.5522847f;
        Vector2[] corners = [new(-0.5f, -0.5f), new(0.5f, -0.5f), new(0.5f, 0.5f), new(-0.5f, 0.5f)];
        (Vector2 Start, Vector2 End)[] arcs =
        [
            (new(-0.5f, -0.5f + ry), new(-0.5f + rx, -0.5f)),
            (new(0.5f - rx, -0.5f), new(0.5f, -0.5f + ry)),
            (new(0.5f, 0.5f - ry), new(0.5f - rx, 0.5f)),
            (new(-0.5f + rx, 0.5f), new(-0.5f, 0.5f - ry)),
        ];
        var list = new List<MaskVertex>(8);
        for (int c = 0; c < 4; c++)
        {
            (Vector2 start, Vector2 end) = arcs[c];
            list.Add(new MaskVertex(start, Vector2.Zero, (corners[c] - start) * k));
            list.Add(new MaskVertex(end, (corners[c] - end) * k, Vector2.Zero));
        }

        return [.. list];
    }

    /// <summary>La elipse inscrita en la caja con cuatro vértices (constante κ ≈ 0,5523: error menor al 0,03 %).</summary>
    public static MaskVertex[] Ellipse()
    {
        const float k = 0.5f * 0.5522847f;
        return
        [
            new(new Vector2(0, -0.5f), new Vector2(-k, 0), new Vector2(k, 0)),
            new(new Vector2(0.5f, 0), new Vector2(0, -k), new Vector2(0, k)),
            new(new Vector2(0, 0.5f), new Vector2(k, 0), new Vector2(-k, 0)),
            new(new Vector2(-0.5f, 0), new Vector2(0, k), new Vector2(0, -k)),
        ];
    }

    /// <summary>
    /// De unos vértices en coordenadas de la imagen (0–1) a la máscara: su caja (centro y tamaño) y los vértices relativos a
    /// ella. La caja es la de los vértices (sin los manejadores), con un mínimo para no dividir por cero.
    /// </summary>
    public static (double X, double Y, double Width, double Height, MaskVertex[] Vertices) Fit(IReadOnlyList<MaskVertex> image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Count == 0)
        {
            return (0.5, 0.5, 0.5, 0.5, []);
        }

        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (MaskVertex v in image)
        {
            minX = Math.Min(minX, v.Point.X);
            minY = Math.Min(minY, v.Point.Y);
            maxX = Math.Max(maxX, v.Point.X);
            maxY = Math.Max(maxY, v.Point.Y);
        }

        float width = Math.Max(maxX - minX, 1e-3f);
        float height = Math.Max(maxY - minY, 1e-3f);
        var center = new Vector2((minX + maxX) / 2, (minY + maxY) / 2);
        var size = new Vector2(width, height);
        return (center.X, center.Y, width, height,
            [.. image.Select(v => new MaskVertex((v.Point - center) / size, v.In / size, v.Out / size, v.Weight))]);
    }
}

/// <summary>
/// Forma animada de una máscara: keyframes de los vértices en tiempo del origen. Entre dos keyframes con los mismos
/// vértices se interpola en línea recta; si cambia la cantidad, se mantiene el anterior hasta el siguiente.
/// </summary>
/// <remarks>
/// Texto: <c>ticks@vértices|ticks@vértices…</c> con los tiempos en orden estricto (<see cref="MaskPath.Format"/> para los
/// vértices). Un texto sin <c>@</c> es una forma fija: así el render lee igual los vértices sueltos.
/// </remarks>
public sealed class MaskShapeTrack
{
    public const int MaxTextLength = 262_144;

    public const int MaxKeys = 10_000;

    private readonly Time[] _times;
    private readonly MaskVertex[][] _shapes;
    private readonly double[] _seconds;

    private MaskShapeTrack(Time[] times, MaskVertex[][] shapes)
    {
        _times = times;
        _shapes = shapes;
        _seconds = [.. times.Select(t => t.ToSeconds())];
    }

    public int Count => _times.Length;

    public Time TimeAt(int index) => _times[index];

    public IReadOnlyList<MaskVertex> ShapeAt(int index) => _shapes[index];

    /// <summary>La mayor cantidad de vértices de los keyframes.</summary>
    public int MaxVertices => _shapes.Max(s => s.Length);

    public static MaskShapeTrack Static(IReadOnlyList<MaskVertex> vertices) => new([Time.Zero], [[.. vertices]]);

    /// <summary>Lee una forma animada o fija; nula si el texto no sirve o queda sin keyframes válidos.</summary>
    public static MaskShapeTrack? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        ReadOnlySpan<char> span = text;
        if (span.IndexOf('@') < 0)
        {
            MaskVertex[] fixedShape = MaskPath.Parse(span);
            return fixedShape.Length >= 2 ? Static(fixedShape) : null;
        }

        var times = new List<Time>();
        var shapes = new List<MaskVertex[]>();
        foreach (Range part in span.Split('|'))
        {
            ReadOnlySpan<char> key = span[part];
            int at = key.IndexOf('@');
            if (at <= 0 || times.Count == MaxKeys
                || !long.TryParse(key[..at], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks)
                || (times.Count > 0 && ticks <= times[^1].Ticks))
            {
                return null;
            }

            MaskVertex[] shape = MaskPath.Parse(key[(at + 1)..]);
            if (shape.Length < 2)
            {
                return null;
            }

            times.Add(Time.FromTicks(ticks));
            shapes.Add(shape);
        }

        return times.Count == 0 ? null : new MaskShapeTrack([.. times], [.. shapes]);
    }

    public static string Format(IEnumerable<(Time Time, IReadOnlyList<MaskVertex> Vertices)> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var text = new StringBuilder();
        foreach ((Time time, IReadOnlyList<MaskVertex> vertices) in keys.OrderBy(k => k.Time))
        {
            if (text.Length > 0)
            {
                text.Append('|');
            }

            text.Append(time.Ticks.ToString(CultureInfo.InvariantCulture)).Append('@');
            for (int i = 0; i < Math.Min(vertices.Count, MaskPath.MaxVertices); i++)
            {
                if (i > 0)
                {
                    text.Append(';');
                }

                MaskPath.Append(text, vertices[i]);
            }
        }

        return text.ToString();
    }

    public IEnumerable<(Time Time, IReadOnlyList<MaskVertex> Vertices)> Keys() =>
        _times.Select((t, i) => (t, (IReadOnlyList<MaskVertex>)_shapes[i]));

    /// <summary>La forma en <paramref name="time"/>.</summary>
    public MaskVertex[] Evaluate(Time time)
    {
        var output = new MaskVertex[MaxVertices];
        int count = Evaluate(time.ToSeconds(), output);
        return count == output.Length ? output : output[..count];
    }

    /// <summary>La forma a los <paramref name="seconds"/> del origen, en <paramref name="output"/> sin reservar memoria.</summary>
    /// <returns>Cuántos vértices escribió.</returns>
    public int Evaluate(double seconds, Span<MaskVertex> output)
    {
        int right = 0;
        while (right < _seconds.Length && _seconds[right] <= seconds)
        {
            right++;
        }

        if (right == 0 || right == _seconds.Length)
        {
            MaskVertex[] only = _shapes[right == 0 ? 0 : ^1];
            int n = Math.Min(only.Length, output.Length);
            only.AsSpan(0, n).CopyTo(output);
            return n;
        }

        MaskVertex[] a = _shapes[right - 1];
        MaskVertex[] b = _shapes[right];
        int count = Math.Min(a.Length, output.Length);
        if (a.Length != b.Length)
        {
            a.AsSpan(0, count).CopyTo(output);
            return count;
        }

        float t = (float)((seconds - _seconds[right - 1]) / (_seconds[right] - _seconds[right - 1]));
        for (int i = 0; i < count; i++)
        {
            output[i] = a[i].Lerp(b[i], t);
        }

        return count;
    }

    /// <summary>Índice del keyframe en <paramref name="time"/>, o −1.</summary>
    public int IndexOf(Time time) => Array.IndexOf(_times, time);

    /// <summary>Una copia con el keyframe de <paramref name="time"/> puesto (o cambiado).</summary>
    public MaskShapeTrack With(Time time, IReadOnlyList<MaskVertex> vertices)
    {
        var keys = Keys().Where(k => k.Time != time).ToList();
        keys.Add((time, [.. vertices]));
        keys.Sort((x, y) => x.Time.CompareTo(y.Time));
        return new MaskShapeTrack([.. keys.Select(k => k.Time)], [.. keys.Select(k => k.Vertices.ToArray())]);
    }

    /// <summary>Una copia sin el keyframe de <paramref name="time"/>; nula si no queda ninguno.</summary>
    public MaskShapeTrack? Without(Time time)
    {
        var keys = Keys().Where(k => k.Time != time).ToList();
        return keys.Count == 0 ? null : new MaskShapeTrack([.. keys.Select(k => k.Time)], [.. keys.Select(k => k.Vertices.ToArray())]);
    }

    public override string ToString() => Format(Keys());
}
