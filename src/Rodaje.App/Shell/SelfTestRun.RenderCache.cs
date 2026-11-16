using Rodaje.App.Services;
using Rodaje.App.Services.RenderCache;
using Rodaje.Core.Timing;

namespace Rodaje.App.Shell;

internal static partial class SelfTestRun
{
    /// <summary>
    /// Render en caché (D-060): espera a que el servicio recorra la secuencia abierta (huellas y previsión de todos sus
    /// cuadros, en el exe AOT) y pinta la franja con los cuatro estados (captura <c>regla-cache</c>). En la autoprueba no
    /// se calcula nada de fondo.
    /// </summary>
    private static async Task RenderCacheAsync(MainWindow window)
    {
        EditorSession session = window.Session;
        RenderCacheHost cache = window.RenderCache;
        long frames = session.Sequence.Duration.ToFrames(session.Sequence.FrameRate);
        if (frames < 4 || session.IsSourceActive)
        {
            return;
        }

        for (int i = 0; i < 100 && !(cache.StripSequenceId == session.SequenceId && cache.Strip.Count > 0 && cache.Strip[^1].End == frames); i++)
        {
            await Task.Delay(100);
        }

        if (cache.StripSequenceId != session.SequenceId || cache.Strip.Count == 0 || cache.Strip[^1].End != frames)
        {
            throw new InvalidOperationException($"El render en caché no recorrió la secuencia: {cache.Strip.Count} tramos para {frames} cuadros.");
        }

        IReadOnlyList<CacheStripSpan> real = cache.Strip;
        Rational rate = cache.StripRate;
        long quarter = frames / 4;
        window.TimelinePane.SetCacheStrip([new(0, quarter, CacheStripState.Light), new(quarter, 2 * quarter, CacheStripState.Tight),
            new(2 * quarter, 3 * quarter, CacheStripState.Heavy), new(3 * quarter, frames, CacheStripState.Cached)], rate);
        await Task.Delay(250);
        IReadOnlyDictionary<CacheStripState, int> shown = window.TimelinePane.CacheStripCounts;
        await CaptureAsync(window, "regla-cache");
        window.TimelinePane.SetCacheStrip(real, rate);
        foreach (CacheStripState state in (CacheStripState[])[CacheStripState.Tight, CacheStripState.Heavy, CacheStripState.Cached])
        {
            if (shown.GetValueOrDefault(state) != 1)
            {
                throw new InvalidOperationException($"La franja del render en caché no pinta {state}: {string.Join(", ", shown.Select(p => $"{p.Key}={p.Value}"))}.");
            }
        }

        if (shown.ContainsKey(CacheStripState.Light))
        {
            throw new InvalidOperationException("La franja pinta lo ligero.");
        }
    }
}
