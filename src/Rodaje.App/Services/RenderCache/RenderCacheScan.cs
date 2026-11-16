using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.RenderCache;
using Rodaje.Media.RenderCache;
using Rodaje.Playback.Composition;

namespace Rodaje.App.Services.RenderCache;

/// <summary>Cómo se ve un tramo en la franja sobre la regla (D-060).</summary>
public enum CacheStripState : byte
{
    /// <summary>Ligero: sin franja.</summary>
    Light,

    /// <summary>Sin caché, previsto a tiempo real con poco margen: amarillo.</summary>
    Tight,

    /// <summary>Sin caché, por encima del presupuesto o con refrescos tarde medidos: rojo.</summary>
    Heavy,

    /// <summary>Se ve desde el render en caché: verde.</summary>
    Cached,
}

/// <summary>Cuadros <c>[Start, End)</c> de la secuencia con el mismo estado en la franja.</summary>
public readonly record struct CacheStripSpan(long Start, long End, CacheStripState State);

/// <summary>Las huellas y la previsión de coste de cada cuadro de una revisión del plan.</summary>
/// <param name="Revision">La revisión del plan de la sesión de la que salen.</param>
/// <param name="Keys">La huella de cada cuadro, nula si no tiene receta válida (no se puede guardar).</param>
/// <param name="Classes">La previsión de coste de cada cuadro.</param>
public sealed record RenderCacheScan(long Revision, Rational FrameRate, RenderCacheKey?[] Keys, RenderCostClass[] Classes)
{
    /// <summary>
    /// Huellas y costes de toda la secuencia, en el hilo que llama.
    /// </summary>
    /// <remarks>
    /// shortcut: recorre la secuencia entera (unos 0,1 ms por cuadro con 8 capas: 10 s por hora a 30 fps); si las secuencias
    /// de horas lo piden, primero la ventana visible y 30 s alrededor del cursor, como dice el diseño.
    /// </remarks>
    public static RenderCacheScan Run(Project project, string sequenceId, long revision, RecipeBuilderOptions options, bool hasGpu,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        RecipeBuilder recipes = RecipeBuilder.Create(project, sequenceId, options);
        var keys = new RenderCacheKey?[recipes.FrameCount];
        for (long f = 0; f < keys.Length; f++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            keys[f] = recipes.KeyAt(f, cancellationToken);
        }

        Sequence sequence = project.FindSequence(sequenceId)!;
        return new RenderCacheScan(revision, recipes.FrameRate, keys, Classify(project, sequence, keys.Length, hasGpu));
    }

    /// <summary>
    /// Previsión por cuadro: cada clip visible de una pista de video suma su capa (decodificar, abrir el decodificador en su
    /// primer cuadro, efectos de clip y pista, e intermedio si es un compuesto) a sus cuadros.
    /// </summary>
    /// <remarks>
    /// shortcut: decodificar se estima por la resolución del medio con lo medido en F0 (unos 1,1 ms por megapíxel); si la
    /// franja se equivoca con medios reales, medir cada medio con <c>DecodeSpeedProbe</c>.
    /// </remarks>
    internal static RenderCostClass[] Classify(Project project, Sequence sequence, long frameCount, bool hasGpu)
    {
        var costs = new double[frameCount + 1];
        Rational rate = sequence.FrameRate;
        foreach (Track track in sequence.VideoTracks)
        {
            if (track.Hidden)
            {
                continue;
            }

            foreach (Clip clip in track.Clips)
            {
                if (!clip.Enabled)
                {
                    continue;
                }

                long first = Math.Clamp(clip.Start.ToFrames(rate), 0, frameCount);
                long end = Math.Clamp(clip.End.ToFrames(rate), first, frameCount);
                if (end <= first)
                {
                    continue;
                }

                double effects = RenderCostModel.EstimateEffectsCost(clip.Effects.Concat(track.Effects), sequence.Width, sequence.Height);
                LayerCostInput layer;
                if (clip.Generator is not null)
                {
                    layer = LayerCostInput.Title(effects, 0, sequence.Width, sequence.Height);
                }
                else
                {
                    MediaRef? media = project.FindMedia(clip.MediaId);
                    double megapixels = media is { Width: > 0, Height: > 0 } ? media.Width * (double)media.Height / 1e6 : sequence.Width * (double)sequence.Height / 1e6;
                    layer = new LayerCostInput(DecodeMsPerMegapixel * megapixels, false, effects, clip.SequenceId is null ? 0 : 1,
                        LayerCostInput.CalculateIntermediateMs(sequence.Width, sequence.Height));
                }

                double each = RenderCostModel.CalculateFrameCost([layer], hasGpu);
                costs[first] += each;
                costs[end] -= each;
                if (clip.Generator is null && OpensDecoder(project, clip))
                {
                    // El primer cuadro paga abrir el decodificador; solo ese cuadro, no todo el clip.
                    costs[first] += RenderCostCalibration.DecoderOpenMs;
                    if (first + 1 <= frameCount)
                    {
                        costs[first + 1] -= RenderCostCalibration.DecoderOpenMs;
                    }
                }
            }
        }

        double budget = RenderCostModel.CalculateBudget(rate);
        var classes = new RenderCostClass[frameCount];
        double running = 0;
        for (long f = 0; f < frameCount; f++)
        {
            running += costs[f];
            classes[f] = RenderCostModel.Classify(running, budget);
        }

        return classes;

        static bool OpensDecoder(Project project, Clip clip) => clip.SequenceId is null && project.FindMedia(clip.MediaId) is { IsStillImage: false };
    }

    /// <summary>Lo que muestra la franja: el caché manda; lo demás, la previsión, y rojo donde hubo refrescos tarde.</summary>
    public IReadOnlyList<CacheStripSpan> Strip(CacheCoverage coverage, IReadOnlySet<long> lateFrames)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        ArgumentNullException.ThrowIfNull(lateFrames);
        var spans = new List<CacheStripSpan>();
        int next = 0;
        IReadOnlyList<CachedSpan> cached = coverage.Spans;
        for (long f = 0; f < Classes.Length; f++)
        {
            while (next < cached.Count && cached[next].EndFrame <= f)
            {
                next++;
            }

            CacheStripState state = next < cached.Count && cached[next].FirstFrame <= f ? CacheStripState.Cached
                : lateFrames.Contains(f) ? CacheStripState.Heavy
                : (CacheStripState)Classes[f];
            if (spans.Count > 0 && spans[^1].State == state && spans[^1].End == f)
            {
                spans[^1] = spans[^1] with { End = f + 1 };
            }
            else
            {
                spans.Add(new CacheStripSpan(f, f + 1, state));
            }
        }

        return spans;
    }

    /// <summary>Lo que el servicio puede calcular: rojo o amarillo en la franja y con huella.</summary>
    public IReadOnlyList<RenderCostSpan> Work(IReadOnlyList<CacheStripSpan> strip)
    {
        ArgumentNullException.ThrowIfNull(strip);
        var work = new List<RenderCostSpan>();
        foreach (CacheStripSpan span in strip)
        {
            if (span.State is not (CacheStripState.Heavy or CacheStripState.Tight))
            {
                continue;
            }

            RenderCostClass cls = span.State == CacheStripState.Heavy ? RenderCostClass.Heavy : RenderCostClass.Tight;
            long start = -1;
            for (long f = span.Start; f <= span.End; f++)
            {
                bool usable = f < span.End && Keys[f] is not null;
                if (usable && start < 0)
                {
                    start = f;
                }
                else if (!usable && start >= 0)
                {
                    work.Add(new RenderCostSpan(start, f, cls));
                    start = -1;
                }
            }
        }

        return work;
    }

    /// <summary>Las huellas de <c>[first, first + count)</c>, o nulo si alguna falta.</summary>
    public RenderCacheKey[]? KeysOf(long first, int count)
    {
        if (first < 0 || first + count > Keys.Length)
        {
            return null;
        }

        var keys = new RenderCacheKey[count];
        for (int i = 0; i < count; i++)
        {
            if (Keys[first + i] is not { } key)
            {
                return null;
            }

            keys[i] = key;
        }

        return keys;
    }

    private const double DecodeMsPerMegapixel = 1.1;
}
