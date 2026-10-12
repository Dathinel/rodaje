using System.Numerics;

namespace Rodaje.Analysis.Video;

/// <summary>Lo que hizo una región entre el cuadro de partida y el actual.</summary>
/// <param name="Motion">
/// Lleva un punto del cuadro de partida al actual, en píxeles del origen (<c>x' = A·x − B·y + X</c>, <c>y' = B·x + A·y + Y</c>).
/// </param>
/// <param name="Anchors">Dónde están ahora los puntos sueltos que se pidieron seguir (por ejemplo los vértices de una máscara).</param>
/// <param name="Inliers">Esquinas que coinciden con el movimiento de este cuadro.</param>
/// <param name="Lost">Con muy pocas esquinas no hay movimiento fiable: la región se queda donde estaba.</param>
public sealed record RegionStep(Similarity Motion, Vector2[] Anchors, int Inliers, bool Lost);

/// <summary>
/// Sigue una región (un polígono) de cuadro en cuadro con el estimador propio: esquinas de Shi y Tomasi dentro de la
/// región, Lucas y Kanade piramidal con comprobación de ida y vuelta, y una similitud (posición, escala y giro) por RANSAC.
/// Los puntos sueltos (<c>anchors</c>) siguen la similitud más el movimiento propio de las esquinas cercanas, así una
/// máscara puede deformarse con lo que sigue. Sirve igual hacia adelante que hacia atrás: el orden lo da quien empuja los
/// cuadros.
/// </summary>
/// <remarks>No es seguro entre hilos; por dentro reparte el seguimiento de las esquinas entre los núcleos.</remarks>
public sealed class RegionTracker
{
    /// <summary>Lado mayor de la imagen de análisis: más que la estabilización, porque las regiones (caras) son chicas.</summary>
    public const int AnalysisSide = 960;

    /// <summary>Con menos esquinas coincidentes que esto, solo se sigue la traslación.</summary>
    public const int MinSimilarityInliers = 8;

    private const int Cell = 6;
    private const float Tolerance = 1.0f;
    private const float RoundTrip = 0.75f;

    private readonly FeatureTracker _detector;
    private readonly List<Vector2> _candidates = [];
    private readonly List<Vector2> _features = [];
    private Pyramid _previous;
    private Pyramid _next;
    private Vector2[] _region = [];
    private Vector2[] _anchors = [];
    private Similarity _total = Similarity.Identity;
    private bool _started;

    public RegionTracker(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 32);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 32);
        SourceWidth = width;
        SourceHeight = height;
        Factor = Math.Max(1, (int)Math.Ceiling(Math.Max(width, height) / (double)AnalysisSide));
        AnalysisWidth = width / Factor;
        AnalysisHeight = height / Factor;
        int levels = 1;
        while (levels < 4 && Math.Min(AnalysisWidth, AnalysisHeight) >> levels >= 40)
        {
            levels++;
        }

        _previous = new Pyramid(AnalysisWidth, AnalysisHeight, levels);
        _next = new Pyramid(AnalysisWidth, AnalysisHeight, levels);
        _detector = new FeatureTracker(AnalysisWidth, AnalysisHeight);
    }

    public int SourceWidth { get; }

    public int SourceHeight { get; }

    /// <summary>Cuántos píxeles del origen promedia cada píxel de análisis por lado.</summary>
    public int Factor { get; }

    public int AnalysisWidth { get; }

    public int AnalysisHeight { get; }

    /// <summary>La región en el cuadro actual, en píxeles del origen.</summary>
    public IReadOnlyList<Vector2> Region => [.. _region.Select(p => p * Factor)];

    /// <summary>Empieza en <paramref name="frame"/> con la región y los puntos sueltos dados en píxeles del origen.</summary>
    public void Start(LumaFrame frame, ReadOnlySpan<Vector2> region, ReadOnlySpan<Vector2> anchors = default)
    {
        Check(frame);
        GrayImage.Downsample(frame, Factor, _previous.Base);
        StartPrepared(region, anchors);
    }

    /// <summary>Como <see cref="Start"/>, con la imagen de análisis ya escrita (para pruebas).</summary>
    internal void StartAnalysis(ReadOnlySpan<float> pixels, ReadOnlySpan<Vector2> region, ReadOnlySpan<Vector2> anchors = default)
    {
        pixels.CopyTo(_previous.Base.Pixels);
        StartPrepared(region, anchors);
    }

    /// <summary>Sigue la región al cuadro siguiente (en el sentido en que se empujen).</summary>
    public RegionStep Step(LumaFrame frame)
    {
        Check(frame);
        if (!_started)
        {
            throw new InvalidOperationException("Primero hay que llamar a Start.");
        }

        GrayImage.Downsample(frame, Factor, _next.Base);
        return StepPrepared();
    }

    internal RegionStep StepAnalysis(ReadOnlySpan<float> pixels)
    {
        pixels.CopyTo(_next.Base.Pixels);
        return StepPrepared();
    }

    private void StartPrepared(ReadOnlySpan<Vector2> region, ReadOnlySpan<Vector2> anchors)
    {
        if (region.Length < 3)
        {
            throw new ArgumentException("La región necesita al menos tres puntos.", nameof(region));
        }

        _region = new Vector2[region.Length];
        for (int i = 0; i < region.Length; i++)
        {
            _region[i] = region[i] / Factor;
        }

        _anchors = new Vector2[anchors.Length];
        for (int i = 0; i < anchors.Length; i++)
        {
            _anchors[i] = anchors[i] / Factor;
        }

        _total = Similarity.Identity;
        _previous.Build();
        DetectInRegion(_previous);
        _started = true;
    }

    private RegionStep StepPrepared()
    {
        _next.Build();
        int n = _features.Count;
        var from = new Vector2[n];
        var to = new Vector2[n];
        var ok = new bool[n];
        Pyramid previous = _previous, next = _next;
        List<Vector2> features = _features;
        Parallel.For(0, n, i =>
        {
            // Ida y vuelta: la esquina tiene que volver a menos de RoundTrip píxeles de donde salió.
            ok[i] = FeatureTracker.Track(previous, next, features[i], out Vector2 found, out float error) && error < 24
                && FeatureTracker.Track(next, previous, found, out Vector2 back, out _)
                && Vector2.Distance(back, features[i]) < RoundTrip;
            from[i] = features[i];
            to[i] = found;
        });

        int tracked = 0;
        for (int i = 0; i < n; i++)
        {
            if (ok[i])
            {
                from[tracked] = from[i];
                to[tracked] = to[i];
                tracked++;
            }
        }

        Similarity step = Similarity.Identity;
        int inliers = tracked >= 2
            ? SimilarityFit.Fit(from.AsSpan(0, tracked), to.AsSpan(0, tracked), Tolerance, tracked * 7919, out step)
            : 0;
        bool lost = false;
        if (inliers < MinSimilarityInliers || !step.IsFinite)
        {
            // Pocas esquinas: solo la traslación, con la mediana de los desplazamientos.
            if (tracked >= 3)
            {
                Vector2 shift = Median(from.AsSpan(0, tracked), to.AsSpan(0, tracked));
                step = new Similarity(1, 0, shift.X, shift.Y);
                inliers = tracked;
            }
            else
            {
                step = Similarity.Identity;
                lost = true;
            }
        }

        for (int i = 0; i < _region.Length; i++)
        {
            _region[i] = Apply(step, _region[i]);
        }

        MoveAnchors(step, from.AsSpan(0, tracked), to.AsSpan(0, tracked));
        _total = step.After(_total);
        (_previous, _next) = (_next, _previous);
        DetectInRegion(_previous);
        Similarity total = Scaled(_total, Factor);
        return new RegionStep(total, [.. _anchors.Select(a => a * Factor)], inliers, lost);
    }

    // Cada punto suelto: la similitud más el resto promedio de las esquinas cercanas (peso gaussiano por distancia).
    private void MoveAnchors(Similarity step, ReadOnlySpan<Vector2> from, ReadOnlySpan<Vector2> to)
    {
        if (_anchors.Length == 0)
        {
            return;
        }

        float size = RegionSize();
        float sigma = Math.Max(6, 0.2f * size);
        float k = -0.5f / (sigma * sigma);
        for (int a = 0; a < _anchors.Length; a++)
        {
            Vector2 anchor = _anchors[a];
            Vector2 moved = Apply(step, anchor);
            Vector2 sum = Vector2.Zero;
            float total = 0;
            for (int i = 0; i < from.Length; i++)
            {
                Vector2 residual = to[i] - Apply(step, from[i]);
                if (residual.LengthSquared() > 144)
                {
                    continue;
                }

                float w = MathF.Exp(k * Vector2.DistanceSquared(from[i], anchor));
                sum += w * residual;
                total += w;
            }

            // Con poco peso cerca, el resto se atenúa hacia el movimiento de la región.
            _anchors[a] = moved + (total > 1e-3f ? sum / (total + 0.5f) : Vector2.Zero);
        }
    }

    private void DetectInRegion(Pyramid pyramid)
    {
        _detector.Detect(pyramid, Cell, _candidates);
        _features.Clear();
        foreach (Vector2 p in _candidates)
        {
            if (Inside(p))
            {
                _features.Add(p);
            }
        }

        // Una región sin esquinas propias (lisa) usa una rejilla: Lucas y Kanade descarta lo que no tiene textura.
        if (_features.Count < 4)
        {
            (Vector2 min, Vector2 max) = Bounds();
            for (float y = min.Y; y <= max.Y; y += Cell)
            {
                for (float x = min.X; x <= max.X; x += Cell)
                {
                    var p = new Vector2(x, y);
                    if (Inside(p))
                    {
                        _features.Add(p);
                    }
                }
            }
        }
    }

    private bool Inside(Vector2 p)
    {
        bool inside = false;
        for (int i = 0, j = _region.Length - 1; i < _region.Length; j = i++)
        {
            Vector2 a = _region[i], b = _region[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < ((b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y)) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private (Vector2 Min, Vector2 Max) Bounds()
    {
        Vector2 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (Vector2 p in _region)
        {
            min = Vector2.Min(min, p);
            max = Vector2.Max(max, p);
        }

        return (Vector2.Max(min, Vector2.Zero), Vector2.Min(max, new Vector2(AnalysisWidth - 1, AnalysisHeight - 1)));
    }

    private float RegionSize()
    {
        (Vector2 min, Vector2 max) = Bounds();
        return Math.Max(max.X - min.X, max.Y - min.Y);
    }

    private static Vector2 Apply(Similarity s, Vector2 p)
    {
        (double x, double y) = s.Apply(p.X, p.Y);
        return new Vector2((float)x, (float)y);
    }

    // La misma similitud en píxeles del origen: el giro y la escala no cambian; la traslación, por el factor.
    private static Similarity Scaled(Similarity s, int factor) => s with { X = s.X * factor, Y = s.Y * factor };

    private static Vector2 Median(ReadOnlySpan<Vector2> from, ReadOnlySpan<Vector2> to)
    {
        float[] xs = new float[from.Length], ys = new float[from.Length];
        for (int i = 0; i < from.Length; i++)
        {
            xs[i] = to[i].X - from[i].X;
            ys[i] = to[i].Y - from[i].Y;
        }

        Array.Sort(xs);
        Array.Sort(ys);
        return new Vector2(xs[xs.Length / 2], ys[ys.Length / 2]);
    }

    private void Check(LumaFrame frame)
    {
        if (frame.Width != SourceWidth || frame.Height != SourceHeight)
        {
            throw new ArgumentException($"El cuadro mide {frame.Width}×{frame.Height} y el seguimiento empezó con {SourceWidth}×{SourceHeight}.",
                nameof(frame));
        }

        int bytes = frame.Format == LumaFormat.Gray8 ? 1 : 2;
        if (frame.Stride < SourceWidth * bytes || frame.Data.Length < ((long)(SourceHeight - 1) * frame.Stride) + (SourceWidth * bytes))
        {
            throw new ArgumentException("El plano es más chico que su tamaño declarado.", nameof(frame));
        }
    }
}
