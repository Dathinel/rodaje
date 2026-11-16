using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Rodaje.App.Services.RenderCache;
using Rodaje.Core.Timing;

namespace Rodaje.App.Views.Timeline;

internal sealed partial class TimelineRenderer
{
    private const double CacheStripHeight = 4;
    private readonly List<Rectangle> _cacheStrip = [];
    private IReadOnlyList<CacheStripSpan> _cacheSpans = [];
    private Rational _cacheRate = Rational.Fps30;

    /// <summary>Los tramos de la franja del render en caché; se pintan en el siguiente <see cref="Render"/>.</summary>
    public void SetCacheStrip(IReadOnlyList<CacheStripSpan> spans, Rational rate)
    {
        _cacheSpans = spans;
        _cacheRate = rate;
    }

    /// <summary>Cuántos trozos de la franja hay a la vista, por estado (para la autoprueba).</summary>
    public IReadOnlyDictionary<CacheStripState, int> CacheStripCounts =>
        _cacheStrip.Where(r => r.Visibility == Visibility.Visible).GroupBy(r => (CacheStripState)r.Tag).ToDictionary(g => g.Key, g => g.Count());

    private void RenderCacheStrip(TimelineViewport vp)
    {
        IReadOnlyList<CacheStripSegment> segments = CacheStripLayout.Segments(_cacheSpans, _cacheRate, vp.TimeToX, vp.Width);
        for (int i = 0; i < segments.Count; i++)
        {
            if (i == _cacheStrip.Count)
            {
                var created = new Rectangle { Height = CacheStripHeight };
                Canvas.SetTop(created, 0);
                _rulerCanvas.Children.Add(created);
                _cacheStrip.Add(created);
            }

            CacheStripSegment segment = segments[i];
            Rectangle rect = _cacheStrip[i];
            if (!Equals(rect.Tag, segment.State))
            {
                rect.Tag = segment.State;
                rect.Fill = CacheBrush(segment.State);
                ToolTipService.SetToolTip(rect, CacheStripLayout.Describe(segment.State));
            }

            rect.Visibility = Visibility.Visible;
            rect.Width = segment.Width;
            Canvas.SetLeft(rect, segment.X);
        }

        for (int i = segments.Count; i < _cacheStrip.Count; i++)
        {
            _cacheStrip[i].Visibility = Visibility.Collapsed;
        }
    }

    private Brush CacheBrush(CacheStripState state) => state switch
    {
        CacheStripState.Heavy => _brushes.Danger,
        CacheStripState.Tight => Resource("SystemFillColorCautionBrush"),
        _ => Resource("SystemFillColorSuccessBrush"),
    };

    private Brush Resource(string key) =>
        Application.Current?.Resources.TryGetValue(key, out object? found) == true && found is Brush brush ? brush : _brushes.Accent;
}
