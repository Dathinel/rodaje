using System.Numerics;

namespace Rodaje.Analysis.Video;

/// <summary>Movimiento del contenido entre un cuadro y el siguiente.</summary>
/// <param name="Motion">Lleva una posición del cuadro anterior a la de este (coordenadas de <see cref="Similarity"/>).</param>
/// <param name="Features">Esquinas seguidas.</param>
/// <param name="Inliers">Las que coinciden con <paramref name="Motion"/>; con pocas (un corte, un cuadro liso) el movimiento es la identidad.</param>
public readonly record struct FrameMotion(Similarity Motion, int Features, int Inliers)
{
    public static readonly FrameMotion None = new(Similarity.Identity, 0, 0);
}

/// <summary>
/// Estima el movimiento de cámara cuadro a cuadro a partir del plano de luma: lo reduce a <see cref="AnalysisSide"/> px
/// de lado mayor, busca esquinas en una rejilla, las sigue con Lucas y Kanade piramidal y ajusta una similitud con RANSAC.
/// Guarda solo dos pirámides, no los cuadros.
/// </summary>
/// <remarks>No es seguro entre hilos; por dentro reparte el seguimiento de las esquinas entre los núcleos.</remarks>
public sealed class MotionEstimator
{
    /// <summary>Lado mayor de la imagen de análisis.</summary>
    public const int AnalysisSide = 640;

    /// <summary>Menos esquinas coincidentes que esto y el movimiento del cuadro se toma como nulo.</summary>
    public const int MinInliers = 8;

    private const int Cell = 24;
    private const float TolerancePixels = 1.25f;

    private readonly FeatureTracker _tracker;
    private readonly List<Vector2> _features = [];
    private readonly List<FrameMotion> _motions = [];
    private Pyramid _previous;
    private Pyramid _next;
    private Vector2[] _from = [];
    private Vector2[] _to = [];
    private bool[] _ok = [];
    private bool _hasPrevious;

    public MotionEstimator(int width, int height)
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
        _tracker = new FeatureTracker(AnalysisWidth, AnalysisHeight);
    }

    public int SourceWidth { get; }

    public int SourceHeight { get; }

    /// <summary>Cuántos píxeles del origen promedia cada píxel de análisis por lado.</summary>
    public int Factor { get; }

    public int AnalysisWidth { get; }

    public int AnalysisHeight { get; }

    /// <summary>Un elemento por cuadro empujado; el primero es <see cref="FrameMotion.None"/>.</summary>
    public IReadOnlyList<FrameMotion> Motions => _motions;

    /// <summary>Analiza el cuadro siguiente y devuelve el movimiento desde el anterior.</summary>
    public FrameMotion Push(LumaFrame frame)
    {
        if (frame.Width != SourceWidth || frame.Height != SourceHeight)
        {
            throw new ArgumentException($"El cuadro mide {frame.Width}×{frame.Height} y el análisis empezó con {SourceWidth}×{SourceHeight}.", nameof(frame));
        }

        int bytes = frame.Format == LumaFormat.Gray8 ? 1 : 2;
        if (frame.Stride < SourceWidth * bytes || frame.Data.Length < ((long)(SourceHeight - 1) * frame.Stride) + (SourceWidth * bytes))
        {
            throw new ArgumentException("El plano es más chico que su tamaño declarado.", nameof(frame));
        }

        GrayImage.Downsample(frame, Factor, _next.Base);
        return PushPrepared();
    }

    /// <summary>Como <see cref="Push"/>, con la imagen de análisis ya escrita (para pruebas): <c>AnalysisWidth × AnalysisHeight</c> valores 0–255.</summary>
    internal FrameMotion PushAnalysis(ReadOnlySpan<float> pixels)
    {
        pixels.CopyTo(_next.Base.Pixels);
        return PushPrepared();
    }

    private FrameMotion PushPrepared()
    {
        _next.Build();
        FrameMotion motion = _hasPrevious ? Estimate() : FrameMotion.None;
        _motions.Add(motion);
        _tracker.Detect(_next, Cell, _features);
        (_previous, _next) = (_next, _previous);
        _hasPrevious = true;
        return motion;
    }

    private FrameMotion Estimate()
    {
        int n = _features.Count;
        if (_from.Length < n)
        {
            _from = new Vector2[n];
            _to = new Vector2[n];
            _ok = new bool[n];
        }

        Pyramid previous = _previous;
        Pyramid next = _next;
        Vector2[] from = _from;
        Vector2[] to = _to;
        bool[] ok = _ok;
        List<Vector2> features = _features;
        Parallel.For(0, n, i =>
        {
            ok[i] = FeatureTracker.Track(previous, next, features[i], out Vector2 found, out float error) && error < 24;
            to[i] = found;
        });

        float halfWidth = AnalysisWidth / 2f;
        float halfHeight = AnalysisHeight / 2f;
        float unit = AnalysisHeight;
        int tracked = 0;
        for (int i = 0; i < n; i++)
        {
            if (!ok[i])
            {
                continue;
            }

            Vector2 p = features[i];
            Vector2 q = to[i];
            from[tracked] = new Vector2((p.X + 0.5f - halfWidth) / unit, (p.Y + 0.5f - halfHeight) / unit);
            to[tracked] = new Vector2((q.X + 0.5f - halfWidth) / unit, (q.Y + 0.5f - halfHeight) / unit);
            tracked++;
        }

        int inliers = SimilarityFit.Fit(from.AsSpan(0, tracked), to.AsSpan(0, tracked), TolerancePixels / unit, _motions.Count, out Similarity motion);
        bool reliable = inliers >= MinInliers && inliers * 4 >= tracked && motion.IsFinite;
        return new FrameMotion(reliable ? motion : Similarity.Identity, tracked, reliable ? inliers : 0);
    }
}
