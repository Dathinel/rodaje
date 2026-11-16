using Rodaje.Core.Timing;
using Rodaje.Engine.Timeline;
using Rodaje.Media.RenderCache;
using Rodaje.Playback.Composition;

namespace Rodaje.Engine.RenderCache;

/// <summary>Cuadros seguidos de la secuencia que se leen seguidos de un fragmento del render en caché.</summary>
/// <param name="FirstFrame">Primer cuadro de la secuencia.</param>
/// <param name="Count">Cuántos cuadros.</param>
/// <param name="MoviePath">El <c>.mov</c> del fragmento.</param>
/// <param name="FirstFragmentFrame">Cuadro del fragmento que corresponde a <paramref name="FirstFrame"/>.</param>
public readonly record struct CachedSpan(long FirstFrame, int Count, string MoviePath, int FirstFragmentFrame)
{
    public long EndFrame => FirstFrame + Count;
}

/// <summary>
/// Qué tramos de la secuencia se ven desde el render en caché (D-060), calculados con las recetas de una revisión del
/// plan. En esos tramos solo existe el carril del caché y en los demás solo los vivos: la selección es exclusiva para la
/// demanda y para la presentación porque los clips vivos se recortan fuera de lo cubierto.
/// </summary>
public sealed class CacheCoverage
{
    public CacheCoverage(long revision, IReadOnlyList<CachedSpan> spans)
    {
        ArgumentNullException.ThrowIfNull(spans);
        Revision = revision;
        Spans = spans;
    }

    /// <summary>La revisión del plan de la que salieron las recetas: solo vale para esa.</summary>
    public long Revision { get; }

    /// <summary>Los tramos, en orden y sin solaparse.</summary>
    public IReadOnlyList<CachedSpan> Spans { get; }

    /// <summary>
    /// Los aciertos del almacén para las huellas de cada cuadro (<paramref name="keys"/>[i] es la del cuadro i, nula si no
    /// tiene receta), agrupados en tramos de un mismo fragmento con cuadros seguidos. Como mucho una apertura nueva por
    /// segundo de la secuencia y tramos de al menos un cuarto de segundo: un acierto suelto o una sucesión de fragmentos
    /// inconexos se queda en vivo.
    /// </summary>
    public static CacheCoverage Find(long revision, IReadOnlyList<RenderCacheKey?> keys, IRenderCacheStore store, Rational frameRate)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(store);
        var found = new List<CachedSpan>();
        RenderCacheHit? previous = null;
        for (int f = 0; f < keys.Count; f++)
        {
            if (keys[f] is not { } key || !store.TryGet(key, previous, out RenderCacheHit hit))
            {
                previous = null;
                continue;
            }

            if (previous is { } last && found.Count > 0 && found[^1].EndFrame == f && last.Fragment.MoviePath == hit.Fragment.MoviePath
                && hit.FrameIndex == last.FrameIndex + 1)
            {
                found[^1] = found[^1] with { Count = found[^1].Count + 1 };
            }
            else
            {
                found.Add(new CachedSpan(f, 1, hit.Fragment.MoviePath, hit.FrameIndex));
            }

            previous = hit;
        }

        long second = Math.Max(1, Time.FromSeconds(1).ToFrames(frameRate));
        long shortest = ShortestSpan(frameRate);
        var spans = new List<CachedSpan>(found.Count);
        foreach (CachedSpan span in found)
        {
            // Abrir un decodificador para menos de un cuarto de segundo cuesta más que componerlo en vivo.
            if (span.Count >= shortest && (spans.Count == 0 || span.FirstFrame - spans[^1].FirstFrame >= second))
            {
                spans.Add(span);
            }
        }

        return new CacheCoverage(revision, spans);
    }

    /// <summary>Cuadros que debe tener como mínimo un tramo para verse desde el caché (un cuarto de segundo).</summary>
    public static long ShortestSpan(Rational frameRate) => Math.Max(1, Math.Max(1, Time.FromSeconds(1).ToFrames(frameRate)) / 4);

    /// <summary>
    /// El plan con el carril del caché encima de todo y los carriles vivos recortados fuera de lo cubierto. Sin tramos,
    /// el mismo plan.
    /// </summary>
    internal TimelinePlan Apply(TimelinePlan plan)
    {
        if (Spans.Count == 0)
        {
            return plan;
        }

        Rational rate = plan.FrameRate;
        List<(Time Start, Time End)> covered = [.. Spans.Select(s => (Time.FromFrames(s.FirstFrame, rate), Time.FromFrames(s.EndFrame, rate)))];
        var trimmed = new Dictionary<VideoLayerPlan, VideoLayerPlan>(ReferenceEqualityComparer.Instance);

        // Los carriles de un compuesto van detrás de su capa: de atrás adelante, cada grupo encuentra ya recortados los suyos.
        for (int i = plan.Layers.Count - 1; i >= 0; i--)
        {
            VideoLayerPlan layer = plan.Layers[i];
            trimmed[layer] = Trim(layer, covered, trimmed);
        }

        var cache = new List<VideoClipPlan>(Spans.Count);
        foreach (CachedSpan span in Spans)
        {
            Time start = Time.FromFrames(span.FirstFrame, rate);
            Time end = Time.FromFrames(span.EndFrame, rate);
            Time anchor = Time.FromFrames(span.FirstFragmentFrame, rate);
            ClipTimeMap map = ClipTimeMap.Create(start, end - start, anchor, null);
            cache.Add(new VideoClipPlan($"{TimelinePlan.CacheTrackId}:{span.FirstFrame}", new SourceKey(span.MoviePath, span.MoviePath), map, start, end,
                Time.FromFrames(span.FirstFragmentFrame + span.Count, rate), rate, rate, null, plan.Width, plan.Height));
        }

        int top = plan.Layers.Count == 0 ? 0 : plan.Layers.Max(l => l.TrackIndex) + 1;
        List<VideoLayerPlan> layers = [.. plan.Layers.Select(l => trimmed[l]), new VideoLayerPlan(top, TimelinePlan.CacheTrackId, cache)];
        return plan.WithLayers(layers);
    }

    private static VideoLayerPlan Trim(VideoLayerPlan layer, List<(Time Start, Time End)> covered, Dictionary<VideoLayerPlan, VideoLayerPlan> trimmed) =>
        new(layer.TrackIndex, layer.TrackId, [.. layer.Clips.SelectMany(c => Pieces(c.Start, c.End, covered).Select(p => c.Trim(p.Start, p.End)))])
        {
            Companion = layer.Companion is { } companion ? Trim(companion, covered, trimmed) : null,
            Generated = layer.Generated,
            Groups = Groups(layer.Groups, covered, trimmed),
            IncomingGroups = Groups(layer.IncomingGroups, covered, trimmed),
            IsNested = layer.IsNested,
            Covered = covered,
        };

    private static List<GroupClipPlan> Groups(IReadOnlyList<GroupClipPlan> groups, List<(Time Start, Time End)> covered,
        Dictionary<VideoLayerPlan, VideoLayerPlan> trimmed) =>
        [.. groups.SelectMany(g => Pieces(g.Start, g.End, covered)
            .Select(p => g.Trim(p.Start, p.End, [.. g.Lanes.Select(l => trimmed.GetValueOrDefault(l) ?? l)])))];

    /// <summary>Lo que queda de <c>[start, end)</c> fuera de los tramos cubiertos (en orden y sin solaparse).</summary>
    internal static List<(Time Start, Time End)> Pieces(Time start, Time end, IReadOnlyList<(Time Start, Time End)> covered)
    {
        var pieces = new List<(Time, Time)>();
        Time from = start;
        foreach ((Time s, Time e) in covered)
        {
            if (e <= from)
            {
                continue;
            }

            if (s >= end)
            {
                break;
            }

            if (s > from)
            {
                pieces.Add((from, s));
            }

            from = Time.Max(from, e);
        }

        if (from < end)
        {
            pieces.Add((from, end));
        }

        return pieces;
    }
}
