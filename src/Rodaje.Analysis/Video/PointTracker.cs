using System.Numerics;
using System.Runtime.CompilerServices;
using Rodaje.Core.Effects;

namespace Rodaje.Analysis.Video;

/// <summary>Un punto que se empieza a seguir, en píxeles del cuadro: centro, lado del patrón y lado de la zona de búsqueda.</summary>
public readonly record struct PointTarget(Vector2 Position, Vector2 Pattern, Vector2 Search);

/// <summary>Cómo salió un punto en un cuadro.</summary>
public enum PointState
{
    /// <summary>Confianza en o sobre el umbral.</summary>
    Good,

    /// <summary>Por debajo del umbral pero encontrado: la posición vale, se marca para revisar.</summary>
    Doubtful,

    /// <summary>No se encontró (tapado, fuera de cuadro): la posición se estima y se sigue buscando.</summary>
    Lost,
}

/// <summary>Un punto en un cuadro: posición en píxeles del cuadro, confianza (correlación normalizada, 0 a 1) y estado.</summary>
public readonly record struct PointResult(Vector2 Position, float Confidence, PointState State)
{
    /// <summary>La deformación afín local del patrón (escala y giro que vio el seguidor): columnas (A11, A21) y (A12, A22).</summary>
    public Vector4 Shape { get; init; } = new(1, 0, 0, 1);
}

/// <summary>
/// Seguidor de puntos con subpíxel: cada punto lleva su patrón del cuadro de partida, que se busca en cada cuadro nuevo en
/// tres pasos. (1) Predicción con la velocidad (o, si está tapado, con el movimiento de los otros puntos). (2) Búsqueda
/// gruesa por correlación normalizada (ZNCC) en la zona de búsqueda, con la imagen vista a través de la deformación del
/// cuadro anterior y reducida a ½ o ¼ si la zona es grande. (3) Lucas y Kanade inverso compuesto afín (Baker y Matthews,
/// 2004) contra el patrón original, con la luz normalizada: el patrón no se actualiza, así no se acumula deriva. La
/// confianza es la ZNCC final; bajo el umbral el cuadro es dudoso y muy por debajo el punto se da por perdido: se
/// predice y la zona de búsqueda crece hasta reencontrarlo. Implementación propia.
/// </summary>
/// <remarks>Trabaja solo en ventanas alrededor de cada punto: el costo no depende del tamaño del cuadro (4K igual que 1080p).</remarks>
public sealed class PointTracker
{
    /// <summary>Cuadros seguidos sin ningún punto encontrado tras los que <see cref="IsLost"/> se enciende.</summary>
    public const int MaxLostFrames = 45;

    private const int MaxIterations = 40;
    private const int MaxWindowSide = 1600;

    private readonly int _width;
    private readonly int _height;
    private readonly float _threshold;
    private readonly float _lostThreshold;
    private Point[] _points = [];
    private int _allLostFrames;
    private float[] _window = [];

    /// <param name="width">Ancho del cuadro en píxeles.</param>
    /// <param name="height">Alto del cuadro.</param>
    /// <param name="threshold">Confianza (0–1) bajo la que el cuadro es dudoso; perdido bajo un 80 % de ella.</param>
    public PointTracker(int width, int height, float threshold = 0.7f)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 16);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 16);
        _width = width;
        _height = height;
        _threshold = Math.Clamp(threshold, 0.05f, 0.99f);
        _lostThreshold = _threshold * 0.8f;
    }

    public int Count => _points.Length;

    /// <summary>Todos los puntos llevan <see cref="MaxLostFrames"/> cuadros perdidos: seguir no tiene sentido.</summary>
    public bool IsLost => _allLostFrames >= MaxLostFrames;

    /// <summary>Empieza en <paramref name="frame"/> con los puntos dados.</summary>
    public void Start(LumaFrame frame, ReadOnlySpan<PointTarget> points)
    {
        Check(frame);
        Start(new Source(frame), points);
    }

    /// <summary>Como <see cref="Start(LumaFrame, ReadOnlySpan{PointTarget})"/> con una imagen gris en <c>float</c> (pruebas).</summary>
    internal void Start(float[] image, ReadOnlySpan<PointTarget> points) => Start(new Source(image, _width, _height), points);

    /// <summary>Sigue los puntos al cuadro siguiente (en el sentido en que se empujen).</summary>
    public PointResult[] Step(LumaFrame frame)
    {
        Check(frame);
        return Step(new Source(frame));
    }

    internal PointResult[] Step(float[] image) => Step(new Source(image, _width, _height));

    private void Start(Source source, ReadOnlySpan<PointTarget> targets)
    {
        if (targets.Length is < 1 or > PointTrack.MaxPoints)
        {
            throw new ArgumentException("Se siguen de 1 a 4 puntos.", nameof(targets));
        }

        _points = new Point[targets.Length];
        for (int i = 0; i < targets.Length; i++)
        {
            _points[i] = new Point(targets[i], source);
        }

        _allLostFrames = 0;
    }

    private PointResult[] Step(Source source)
    {
        if (_points.Length == 0)
        {
            throw new InvalidOperationException("Primero hay que llamar a Start.");
        }

        var results = new PointResult[_points.Length];
        for (int i = 0; i < _points.Length; i++)
        {
            results[i] = Track(_points[i], source);
        }

        RejectStrayReacquisitions(results);
        PredictLost(results);
        bool allLost = true;
        for (int i = 0; i < _points.Length; i++)
        {
            Point p = _points[i];
            Vector2 previous = p.Center;
            p.Center = results[i].Position;
            if (results[i].State == PointState.Lost)
            {
                p.Lost++;
                p.Velocity *= 0.9f;
            }
            else
            {
                allLost = false;
                p.Velocity = p.Lost > 0 ? p.Center - previous : (0.5f * p.Velocity) + (0.5f * (p.Center - previous));
                p.Lost = 0;
            }
        }

        _allLostFrames = allLost ? _allLostFrames + 1 : 0;
        return results;
    }

    // Un punto que vuelve a aparecer lejos de donde lo ponen los demás (encontrados sin pausa) es otro parecido: sigue perdido.
    private void RejectStrayReacquisitions(PointResult[] results)
    {
        for (int i = 0; i < results.Length; i++)
        {
            if (_points[i].Lost == 0 || results[i].State == PointState.Lost)
            {
                continue;
            }

            var anchors = new List<int>();
            for (int k = 0; k < results.Length; k++)
            {
                if (k != i && _points[k].Lost == 0 && results[k].State != PointState.Lost)
                {
                    anchors.Add(k);
                }
            }

            if (anchors.Count == 0)
            {
                continue;
            }

            Homography motion = Motion(anchors, results);
            float tolerance = Math.Max(6, 0.5f * Math.Max(_points[i].Rx, _points[i].Ry));
            if (Vector2.Distance(motion.Apply(_points[i].Origin), results[i].Position) > tolerance)
            {
                results[i] = new PointResult(_points[i].Center + _points[i].Velocity, 0, PointState.Lost) { Shape = _points[i].Warp.Shape };
            }
        }
    }

    private Homography Motion(List<int> anchors, PointResult[] results)
    {
        int a = anchors[0];
        return anchors.Count > 1
            ? Homography.SimilarityFrom(_points[a].Origin, _points[anchors[1]].Origin, results[a].Position, results[anchors[1]].Position)
            : Homography.Translation(results[a].Position.X - _points[a].Origin.X, results[a].Position.Y - _points[a].Origin.Y);
    }

    // Los perdidos se llevan con el movimiento de los encontrados desde la partida: dos o más dan una similitud, uno, el desplazamiento.
    private void PredictLost(PointResult[] results)
    {
        int lost = Array.FindAll(results, r => r.State == PointState.Lost).Length;
        if (lost == 0 || lost == results.Length)
        {
            return;
        }

        Homography motion = Motion([.. Enumerable.Range(0, results.Length).Where(k => results[k].State != PointState.Lost)], results);
        for (int i = 0; i < results.Length; i++)
        {
            if (results[i].State == PointState.Lost)
            {
                results[i] = results[i] with { Position = motion.Apply(_points[i].Origin) };
            }
        }
    }

    private PointResult Track(Point p, Source source)
    {
        // 1. Predicción.
        Vector2 predicted = p.Center + p.Velocity;
        float grow = p.Lost > 0 ? Math.Min(3f, 1 + (0.5f * p.Lost)) : 1;
        Vector2 search = p.Search * grow;
        Affine warp = p.Warp with { X = predicted.X, Y = predicted.Y };

        // La ventana de imagen que cubre la búsqueda con el patrón deformado.
        float reach = warp.MaxScale * (Math.Max(p.Rx, p.Ry) + 2);
        int halfX = (int)MathF.Ceiling(Math.Min(search.X + reach + 3, MaxWindowSide / 2));
        int halfY = (int)MathF.Ceiling(Math.Min(search.Y + reach + 3, MaxWindowSide / 2));
        var window = new Window((int)MathF.Floor(predicted.X) - halfX, (int)MathF.Floor(predicted.Y) - halfY, (2 * halfX) + 2, (2 * halfY) + 2);
        if (_window.Length < window.Width * window.Height)
        {
            _window = new float[window.Width * window.Height];
        }

        window.Pixels = _window;
        source.Read(window);

        // 2. Búsqueda gruesa: la imagen vista a través de la deformación (J), comparada con el patrón por desplazamientos enteros.
        float minScale = Math.Max(0.25f, warp.MinScale);
        int sx = (int)MathF.Ceiling(search.X / minScale), sy = (int)MathF.Ceiling(search.Y / minScale);
        Vector2 shift = Coarse(p, window, warp, sx, sy);
        Affine start = warp with { X = predicted.X + (warp.A11 * shift.X) + (warp.A12 * shift.Y), Y = predicted.Y + (warp.A21 * shift.X) + (warp.A22 * shift.Y) };

        // 3. Lucas y Kanade afín con subpíxel, contra el patrón original.
        Affine refined = Refine(p, window, start, out bool converged);
        float confidence = converged ? Correlation(p, window, refined) : -1;
        var center = new Vector2(refined.X, refined.Y);
        bool shapeOk = refined.Determinant is > 0.2f and < 5f;
        if (!converged || !shapeOk || confidence < _lostThreshold || !Inside(center))
        {
            return new PointResult(predicted, 0, PointState.Lost) { Shape = p.Warp.Shape };
        }

        // Tras una pérdida se vuelve a dar por encontrado solo con una confianza alta (al menos 0,85).
        if (p.Lost > 0 && confidence < Math.Max(_threshold, 0.85f))
        {
            return new PointResult(predicted, 0, PointState.Lost) { Shape = p.Warp.Shape };
        }

        p.Warp = refined;
        return new PointResult(center, Math.Clamp(confidence, 0, 1), confidence >= _threshold ? PointState.Good : PointState.Doubtful)
        {
            Shape = refined.Shape,
        };
    }

    private bool Inside(Vector2 c) => c.X >= 0 && c.Y >= 0 && c.X <= _width - 1 && c.Y <= _height - 1;

    /// <summary>El desplazamiento (en unidades del patrón) de mayor ZNCC dentro de ±(sx, sy), primero reducido y luego a escala 1.</summary>
    private static Vector2 Coarse(Point p, Window window, in Affine warp, int sx, int sy)
    {
        int factor = Math.Max(sx, sy) > 40 ? 4 : Math.Max(sx, sy) > 10 ? 2 : 1;
        int jx = sx + p.Rx + factor, jy = sy + p.Ry + factor;
        int jw = (2 * jx) + 1, jh = (2 * jy) + 1;
        float[] j = new float[jw * jh];
        for (int v = 0; v < jh; v++)
        {
            for (int u = 0; u < jw; u++)
            {
                j[(v * jw) + u] = window.Sample(warp.Map(u - jx, v - jy));
            }
        }

        int bestX = 0, bestY = 0;
        if (factor > 1)
        {
            int cw = jw / factor, ch = jh / factor;
            float[] coarse = Reduce(j, jw, jh, factor, cw, ch);
            int tw = p.PatternWidth / factor, th = p.PatternHeight / factor;
            float[] template = Normalize(Reduce(p.Template, p.PatternWidth, p.PatternHeight, factor, tw, th));
            // Con la esquina del patrón reducido en la celda c, su centro cae en el desplazamiento c·f + R − j de J.
            int cx = (jx - p.Rx) / factor, cy = (jy - p.Ry) / factor;
            int rangeX = (sx / factor) + 1, rangeY = (sy / factor) + 1;
            float best = float.MinValue;
            for (int dy = -rangeY; dy <= rangeY; dy++)
            {
                for (int dx = -rangeX; dx <= rangeX; dx++)
                {
                    float score = Zncc(coarse, cw, ch, template, tw, th, cx + dx, cy + dy);
                    if (score > best)
                    {
                        best = score;
                        bestX = ((cx + dx) * factor) + p.Rx - jx;
                        bestY = ((cy + dy) * factor) + p.Ry - jy;
                    }
                }
            }
        }

        // A escala 1 alrededor de lo grueso (o en toda la zona si no hubo reducción).
        int rx = factor > 1 ? factor : sx, ry = factor > 1 ? factor : sy;
        float bestFine = float.MinValue;
        int fx = bestX, fy = bestY;
        for (int dy = -ry; dy <= ry; dy++)
        {
            for (int dx = -rx; dx <= rx; dx++)
            {
                int ox = bestX + dx, oy = bestY + dy;
                if (Math.Abs(ox) > sx + factor || Math.Abs(oy) > sy + factor)
                {
                    continue;
                }

                float score = Zncc(j, jw, jh, p.Normalized, p.PatternWidth, p.PatternHeight, jx + ox - p.Rx, jy + oy - p.Ry);
                if (score > bestFine)
                {
                    bestFine = score;
                    fx = ox;
                    fy = oy;
                }
            }
        }

        return new Vector2(fx, fy);
    }

    /// <summary>ZNCC entre el patrón (ya de media 0 y desvío 1) y la imagen con su esquina en (x0, y0).</summary>
    private static float Zncc(float[] image, int width, int height, float[] template, int tw, int th, int x0, int y0)
    {
        if (x0 < 0 || y0 < 0 || x0 + tw > width || y0 + th > height)
        {
            return float.MinValue;
        }

        float dot = 0, sum = 0, sum2 = 0;
        for (int y = 0; y < th; y++)
        {
            ReadOnlySpan<float> row = image.AsSpan(((y0 + y) * width) + x0, tw);
            ReadOnlySpan<float> t = template.AsSpan(y * tw, tw);
            int x = 0;
            if (Vector.IsHardwareAccelerated)
            {
                var vd = Vector<float>.Zero;
                var vs = Vector<float>.Zero;
                var vs2 = Vector<float>.Zero;
                for (; x <= tw - Vector<float>.Count; x += Vector<float>.Count)
                {
                    var r = new Vector<float>(row[x..]);
                    vd += r * new Vector<float>(t[x..]);
                    vs += r;
                    vs2 += r * r;
                }

                dot += Vector.Sum(vd);
                sum += Vector.Sum(vs);
                sum2 += Vector.Sum(vs2);
            }

            for (; x < tw; x++)
            {
                float r = row[x];
                dot += r * t[x];
                sum += r;
                sum2 += r * r;
            }
        }

        int n = tw * th;
        float variance = (sum2 / n) - ((sum / n) * (sum / n));
        return variance > 1e-4f ? dot / (n * MathF.Sqrt(variance)) : 0;
    }

    private static float[] Reduce(float[] image, int width, int height, int factor, int outWidth, int outHeight)
    {
        float[] result = new float[outWidth * outHeight];
        float scale = 1f / (factor * factor);
        for (int y = 0; y < outHeight; y++)
        {
            for (int x = 0; x < outWidth; x++)
            {
                float sum = 0;
                for (int dy = 0; dy < factor; dy++)
                {
                    int row = (((y * factor) + dy) * width) + (x * factor);
                    for (int dx = 0; dx < factor; dx++)
                    {
                        sum += image[row + dx];
                    }
                }

                result[(y * outWidth) + x] = sum * scale;
            }
        }

        return result;
    }

    /// <summary>Media 0 y desvío 1 (un patrón liso queda en 0).</summary>
    internal static float[] Normalize(float[] values)
    {
        float mean = 0;
        foreach (float v in values)
        {
            mean += v;
        }

        mean /= values.Length;
        float variance = 0;
        foreach (float v in values)
        {
            variance += (v - mean) * (v - mean);
        }

        float scale = variance > 1e-4f * values.Length ? 1 / MathF.Sqrt(variance / values.Length) : 0;
        float[] result = new float[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            result[i] = (values[i] - mean) * scale;
        }

        return result;
    }

    // Lucas y Kanade inverso compuesto: el jacobiano y la hessiana salen del patrón una sola vez (Point).
    private static Affine Refine(Point p, Window window, Affine warp, out bool converged)
    {
        int n = p.PatternWidth * p.PatternHeight;
        Span<float> image = n <= 4096 ? stackalloc float[n] : new float[n];
        Span<double> b = stackalloc double[6];
        Span<double> delta = stackalloc double[6];
        converged = false;
        for (int iteration = 0; iteration < MaxIterations; iteration++)
        {
            if (!SampleWarped(p, window, warp, image))
            {
                return warp;
            }

            NormalizeInPlace(image);
            b.Clear();
            for (int k = 0; k < n; k++)
            {
                float error = image[k] - p.Normalized[k];
                int s = k * 6;
                for (int q = 0; q < 6; q++)
                {
                    b[q] += p.Steepest[s + q] * error;
                }
            }

            for (int r = 0; r < 6; r++)
            {
                double sum = 0;
                for (int c = 0; c < 6; c++)
                {
                    sum += p.InverseHessian[(r * 6) + c] * b[c];
                }

                delta[r] = sum;
            }

            if (!double.IsFinite(delta[4]) || !double.IsFinite(delta[5]))
            {
                return warp;
            }

            warp = warp.ComposeInverse(delta);
            double step = Math.Abs(delta[4]) + Math.Abs(delta[5]) + (p.Rx * (Math.Abs(delta[0]) + Math.Abs(delta[1])))
                + (p.Ry * (Math.Abs(delta[2]) + Math.Abs(delta[3])));
            if (step < 2e-4)
            {
                converged = true;
                break;
            }

            converged = iteration == MaxIterations - 1 && step < 0.05;
        }

        return warp;
    }

    private static float Correlation(Point p, Window window, in Affine warp)
    {
        int n = p.PatternWidth * p.PatternHeight;
        float[] image = new float[n];
        if (!SampleWarped(p, window, warp, image))
        {
            return -1;
        }

        NormalizeInPlace(image);
        float dot = 0;
        for (int k = 0; k < n; k++)
        {
            dot += image[k] * p.Normalized[k];
        }

        return dot / n;
    }

    private static bool SampleWarped(Point p, Window window, in Affine warp, Span<float> output)
    {
        int k = 0;
        for (int y = -p.Ry; y <= p.Ry; y++)
        {
            for (int x = -p.Rx; x <= p.Rx; x++)
            {
                Vector2 at = warp.Map(x, y);
                if (!window.Contains(at))
                {
                    return false;
                }

                output[k++] = window.Sample(at);
            }
        }

        return true;
    }

    private static void NormalizeInPlace(Span<float> values)
    {
        float mean = 0;
        foreach (float v in values)
        {
            mean += v;
        }

        mean /= values.Length;
        float variance = 0;
        foreach (float v in values)
        {
            variance += (v - mean) * (v - mean);
        }

        float scale = variance > 1e-6f ? 1 / MathF.Sqrt(variance / values.Length) : 0;
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = (values[i] - mean) * scale;
        }
    }

    private void Check(LumaFrame frame)
    {
        if (frame.Width != _width || frame.Height != _height)
        {
            throw new ArgumentException($"El cuadro mide {frame.Width}×{frame.Height} y el seguimiento empezó con {_width}×{_height}.", nameof(frame));
        }

        int bytes = frame.Format == LumaFormat.Gray8 ? 1 : 2;
        if (frame.Stride < _width * bytes || frame.Data.Length < ((long)(_height - 1) * frame.Stride) + (_width * bytes))
        {
            throw new ArgumentException("El plano es más chico que su tamaño declarado.", nameof(frame));
        }
    }

    /// <summary>Afín que lleva un punto del patrón (centrado en 0) a la imagen: <c>(A11·x + A12·y + X, A21·x + A22·y + Y)</c>.</summary>
    private readonly record struct Affine(float A11, float A12, float A21, float A22, float X, float Y)
    {
        public static Affine At(Vector2 center) => new(1, 0, 0, 1, center.X, center.Y);

        public Vector2 Map(float x, float y) => new((A11 * x) + (A12 * y) + X, (A21 * x) + (A22 * y) + Y);

        public float Determinant => (A11 * A22) - (A12 * A21);

        public Vector4 Shape => new(A11, A21, A12, A22);

        public float MaxScale => MathF.Max(MathF.Sqrt((A11 * A11) + (A21 * A21)), MathF.Sqrt((A12 * A12) + (A22 * A22)));

        public float MinScale => MathF.Min(MathF.Sqrt((A11 * A11) + (A21 * A21)), MathF.Sqrt((A12 * A12) + (A22 * A22)));

        /// <summary>W ∘ ΔW⁻¹ con ΔW = [[1 + d0, d2, d4], [d1, 1 + d3, d5]].</summary>
        public Affine ComposeInverse(ReadOnlySpan<double> d)
        {
            double a = 1 + d[0], b = d[2], c = d[1], e = 1 + d[3];
            double det = (a * e) - (b * c);
            if (Math.Abs(det) < 1e-9)
            {
                return this;
            }

            double ia = e / det, ib = -b / det, ic = -c / det, ie = a / det;
            double itx = -((ia * d[4]) + (ib * d[5])), ity = -((ic * d[4]) + (ie * d[5]));
            return new Affine(
                (float)((A11 * ia) + (A12 * ic)), (float)((A11 * ib) + (A12 * ie)),
                (float)((A21 * ia) + (A22 * ic)), (float)((A21 * ib) + (A22 * ie)),
                (float)((A11 * itx) + (A12 * ity) + X), (float)((A21 * itx) + (A22 * ity) + Y));
        }
    }

    /// <summary>Un trozo del cuadro en <c>float</c>, con el borde del cuadro repetido afuera.</summary>
    private sealed class Window(int left, int top, int width, int height)
    {
        public int Left { get; } = left;

        public int Top { get; } = top;

        public int Width { get; } = width;

        public int Height { get; } = height;

        public float[] Pixels { get; set; } = [];

        public bool Contains(Vector2 p) =>
            p.X >= Left && p.Y >= Top && p.X < Left + Width - 1 && p.Y < Top + Height - 1;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float Sample(Vector2 p)
        {
            float x = Math.Clamp(p.X - Left, 0, Width - 1.001f), y = Math.Clamp(p.Y - Top, 0, Height - 1.001f);
            int x0 = (int)x, y0 = (int)y;
            float fx = x - x0, fy = y - y0;
            int i = (y0 * Width) + x0;
            float[] px = Pixels;
            float top = px[i] + (fx * (px[i + 1] - px[i]));
            float bottom = px[i + Width] + (fx * (px[i + Width + 1] - px[i + Width]));
            return top + (fy * (bottom - top));
        }
    }

    /// <summary>De dónde se leen los píxeles: el plano de luma del decodificador o una imagen de prueba.</summary>
    private readonly ref struct Source
    {
        private readonly ReadOnlySpan<byte> _data;
        private readonly float[]? _image;
        private readonly int _stride;
        private readonly LumaFormat _format;
        private readonly int _width;
        private readonly int _height;

        public Source(LumaFrame frame)
        {
            _data = frame.Data;
            _stride = frame.Stride;
            _format = frame.Format;
            _width = frame.Width;
            _height = frame.Height;
            _image = null;
        }

        public Source(float[] image, int width, int height)
        {
            if (image.Length < width * height)
            {
                throw new ArgumentException("La imagen es más chica que el cuadro.", nameof(image));
            }

            _image = image;
            _width = width;
            _height = height;
            _data = default;
            _stride = 0;
            _format = LumaFormat.Gray8;
        }

        public void Read(Window window)
        {
            float[] output = window.Pixels;
            for (int y = 0; y < window.Height; y++)
            {
                int sy = Math.Clamp(window.Top + y, 0, _height - 1);
                int row = y * window.Width;
                for (int x = 0; x < window.Width; x++)
                {
                    output[row + x] = Get(Math.Clamp(window.Left + x, 0, _width - 1), sy);
                }
            }
        }

        public float Bilinear(Vector2 p)
        {
            float x = Math.Clamp(p.X, 0, _width - 1.001f), y = Math.Clamp(p.Y, 0, _height - 1.001f);
            int x0 = (int)x, y0 = (int)y;
            float fx = x - x0, fy = y - y0;
            float top = Get(x0, y0) + (fx * (Get(x0 + 1, y0) - Get(x0, y0)));
            float bottom = Get(x0, y0 + 1) + (fx * (Get(x0 + 1, y0 + 1) - Get(x0, y0 + 1)));
            return top + (fy * (bottom - top));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float Get(int x, int y)
        {
            if (_image is not null)
            {
                return _image[(y * _width) + x];
            }

            int offset = y * _stride;
            return _format switch
            {
                LumaFormat.Gray8 => _data[offset + x],
                LumaFormat.Gray16High => Unsafe.ReadUnaligned<ushort>(in _data[offset + (2 * x)]) * (1f / 256),
                LumaFormat.Gray16Low12 => Unsafe.ReadUnaligned<ushort>(in _data[offset + (2 * x)]) * (1f / 16),
                _ => Unsafe.ReadUnaligned<ushort>(in _data[offset + (2 * x)]) * (1f / 4),
            };
        }
    }

    /// <summary>Estado de un punto: patrón original con su jacobiano y hessiana, deformación actual, velocidad y pérdidas.</summary>
    private sealed class Point
    {
        public Point(PointTarget target, Source source)
        {
            Origin = target.Position;
            Center = target.Position;
            Rx = Math.Max(3, (int)MathF.Round(target.Pattern.X / 2));
            Ry = Math.Max(3, (int)MathF.Round(target.Pattern.Y / 2));
            Search = Vector2.Max(new Vector2(2), target.Search / 2);
            PatternWidth = (2 * Rx) + 1;
            PatternHeight = (2 * Ry) + 1;
            Warp = Affine.At(target.Position);

            // El patrón con un píxel de borde para sus derivadas centradas.
            int bw = PatternWidth + 2, bh = PatternHeight + 2;
            float[] bordered = new float[bw * bh];
            for (int y = 0; y < bh; y++)
            {
                for (int x = 0; x < bw; x++)
                {
                    bordered[(y * bw) + x] = source.Bilinear(target.Position + new Vector2(x - Rx - 1, y - Ry - 1));
                }
            }

            Template = new float[PatternWidth * PatternHeight];
            for (int y = 0; y < PatternHeight; y++)
            {
                Array.Copy(bordered, ((y + 1) * bw) + 1, Template, y * PatternWidth, PatternWidth);
            }

            Normalized = Normalize(Template);
            float mean = Template.Average();
            float deviation = MathF.Sqrt(Template.Sum(v => (v - mean) * (v - mean)) / Template.Length);
            float scale = deviation > 1e-2f ? 1 / deviation : 0;

            // Columnas del jacobiano de W respecto de (d0…d5) en (x, y): [x·Tx, x·Ty, y·Tx, y·Ty, Tx, Ty].
            Steepest = new float[PatternWidth * PatternHeight * 6];
            Span<double> h = stackalloc double[36];
            int k = 0;
            for (int y = 0; y < PatternHeight; y++)
            {
                for (int x = 0; x < PatternWidth; x++, k++)
                {
                    int c = ((y + 1) * bw) + x + 1;
                    float tx = 0.5f * (bordered[c + 1] - bordered[c - 1]) * scale;
                    float ty = 0.5f * (bordered[c + bw] - bordered[c - bw]) * scale;
                    float px = x - Rx, py = y - Ry;
                    Span<float> sd = Steepest.AsSpan(k * 6, 6);
                    sd[0] = px * tx;
                    sd[1] = px * ty;
                    sd[2] = py * tx;
                    sd[3] = py * ty;
                    sd[4] = tx;
                    sd[5] = ty;
                    for (int r = 0; r < 6; r++)
                    {
                        for (int q = 0; q < 6; q++)
                        {
                            h[(r * 6) + q] += sd[r] * sd[q];
                        }
                    }
                }
            }

            InverseHessian = Invert6(h);
        }

        public Vector2 Origin { get; }

        public Vector2 Center { get; set; }

        public Vector2 Velocity { get; set; }

        public int Lost { get; set; }

        public int Rx { get; }

        public int Ry { get; }

        public int PatternWidth { get; }

        public int PatternHeight { get; }

        public Vector2 Search { get; }

        public Affine Warp { get; set; }

        public float[] Template { get; }

        public float[] Normalized { get; }

        public float[] Steepest { get; }

        public double[] InverseHessian { get; }

        // Gauss–Jordan con un poco de regularización: un patrón sin textura en una dirección no explota.
        private static double[] Invert6(ReadOnlySpan<double> h)
        {
            const int n = 6;
            double[] m = new double[n * 2 * n];
            double trace = 0;
            for (int i = 0; i < n; i++)
            {
                trace += h[(i * n) + i];
            }

            double ridge = Math.Max(1e-9, 1e-7 * trace);
            for (int r = 0; r < n; r++)
            {
                for (int c = 0; c < n; c++)
                {
                    m[(r * 2 * n) + c] = h[(r * n) + c] + (r == c ? ridge : 0);
                }

                m[(r * 2 * n) + n + r] = 1;
            }

            for (int col = 0; col < n; col++)
            {
                int pivot = col;
                for (int r = col + 1; r < n; r++)
                {
                    if (Math.Abs(m[(r * 2 * n) + col]) > Math.Abs(m[(pivot * 2 * n) + col]))
                    {
                        pivot = r;
                    }
                }

                for (int c = 0; c < 2 * n; c++)
                {
                    (m[(col * 2 * n) + c], m[(pivot * 2 * n) + c]) = (m[(pivot * 2 * n) + c], m[(col * 2 * n) + c]);
                }

                double d = m[(col * 2 * n) + col];
                for (int c = 0; c < 2 * n; c++)
                {
                    m[(col * 2 * n) + c] /= d;
                }

                for (int r = 0; r < n; r++)
                {
                    if (r == col)
                    {
                        continue;
                    }

                    double f = m[(r * 2 * n) + col];
                    for (int c = 0; c < 2 * n; c++)
                    {
                        m[(r * 2 * n) + c] -= f * m[(col * 2 * n) + c];
                    }
                }
            }

            double[] inverse = new double[n * n];
            for (int r = 0; r < n; r++)
            {
                Array.Copy(m, (r * 2 * n) + n, inverse, r * n, n);
            }

            return inverse;
        }
    }
}
