using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Rodaje.App.Services.Media;
using Rodaje.App.Services.Titles;
using Rodaje.App.Theme;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Rodaje.App.Views.Timeline;

/// <summary>Lo que se dibuja en un cuadro: el modelo, la selección y lo que deja el gesto en curso.</summary>
internal sealed class TimelineScene
{
    public required Project Project { get; init; }

    public required Sequence Sequence { get; init; }

    public Track? MainTrack { get; init; }

    public IReadOnlySet<string> Selection { get; init; } = new HashSet<string>();

    public Time Position { get; init; }

    public bool ShowEnvelopes { get; init; }

    public bool AutoCrossfade { get; init; }

    public string? SelectedTransitionId { get; init; }

    public IClipVisuals? Visuals { get; init; }
}

/// <summary>Lo que se dibuja encima de las pistas y cambia con cada movimiento del puntero.</summary>
internal sealed class TimelineOverlay
{
    /// <summary>Clip fantasma: dónde caería el clip arrastrado o el medio soltado.</summary>
    public (string TrackId, Time Start, Time End)? Ghost { get; set; }

    /// <summary>Segundo fantasma (el sonido enlazado de un medio que se suelta).</summary>
    public (string TrackId, Time Start, Time End)? LinkedGhost { get; set; }

    /// <summary>Corte de la pista magnética donde entraría el clip.</summary>
    public (string TrackId, Time At)? InsertLine { get; set; }

    public Time? SnapGuide { get; set; }

    /// <summary>Rectángulo de selección: x en el área visible, y en el contenido.</summary>
    public Rect? Marquee { get; set; }

    public (Time Start, Time End)? TimeRange { get; set; }

    /// <summary>Globo junto a un borde arrastrado (velocidad y duración al estirar), sobre la fila de su pista.</summary>
    public (string TrackId, Time At, string Text)? Balloon { get; set; }
}

/// <summary>
/// Dibuja la regla y las pistas con elementos de XAML reciclados. Solo se crean los clips de una ventana de tres anchos
/// de pantalla alrededor de lo visible; desplazarse dentro de ella solo mueve una transformación, y al salir (o al
/// cambiar el zoom o el proyecto) se vuelve a armar.
/// </summary>
internal sealed class TimelineRenderer
{
    public const double RulerHeight = 28;

    private readonly TimelineBrushes _brushes;
    private readonly Canvas _rowsCanvas;
    private readonly Canvas _clipsCanvas;
    private readonly Canvas _overlayCanvas;
    private readonly Canvas _rulerCanvas;
    private readonly Canvas _playheadLayer;
    private readonly Canvas _rulerContent = new();
    private readonly CompositeTransform _clipsShift = new();
    private readonly TranslateTransform _rowsShift = new();
    private readonly CompositeTransform _rulerShift = new();
    private readonly List<ClipVisual> _clips = [];
    private readonly List<Rectangle> _rows = [];
    private readonly List<Rectangle> _regionBands = [];
    private readonly List<Rectangle> _markerLines = [];
    private readonly List<Path> _crossfades = [];
    private readonly List<Border> _transitions = [];
    private readonly List<TextBlock> _rulerLabels = [];
    private readonly List<Path> _markerFlags = [];
    private readonly List<TextBlock> _markerNames = [];
    private readonly List<Rectangle> _regionRulerBands = [];
    private readonly Path _ticks = new();
    private readonly Rectangle _playheadLine = new() { Width = 1 };
    private readonly Path _playheadHead = new();
    private readonly TextBlock _balloonText = new() { FontSize = ThemeResources.Size("CaptionFontSize"), TextWrapping = TextWrapping.NoWrap };
    private readonly Border _balloon = new() { Padding = new Thickness(6, 2, 6, 3), IsHitTestVisible = false };
    private readonly TextBlock _cursorBadgeText = new() { FontSize = ThemeResources.Size("CaptionFontSize"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.NoWrap };
    private readonly Border _cursorBadge = new() { Padding = new Thickness(4, 0, 4, 0), CornerRadius = ThemeResources.Corner("ControlCornerRadius"), IsHitTestVisible = false };
    private readonly Border _ghost = new() { CornerRadius = ThemeResources.Corner("ControlCornerRadius"), BorderThickness = new Thickness(1) };
    private readonly Border _linkedGhost = new() { CornerRadius = ThemeResources.Corner("ControlCornerRadius"), BorderThickness = new Thickness(1) };
    private readonly Rectangle _insertLine = new() { Width = 2 };
    private readonly Rectangle _snapGuide = new() { Width = 1 };
    private readonly Border _marquee = new() { BorderThickness = new Thickness(1) };
    private readonly Rectangle _timeRange = new();
    private readonly Rectangle _timeRangeRuler = new() { Height = 4 };
    private double _windowLeft;
    private double _windowRight;
    private double _realizedPixelsPerSecond;
    private double _realizedWidth;
    private bool _contentDirty = true;

    public TimelineRenderer(TimelineBrushes brushes, Canvas rows, Canvas clips, Canvas overlay, Canvas ruler, Canvas playheadLayer)
    {
        _brushes = brushes;
        _rowsCanvas = rows;
        _clipsCanvas = clips;
        _overlayCanvas = overlay;
        _rulerCanvas = ruler;
        _playheadLayer = playheadLayer;
        _rowsCanvas.RenderTransform = _rowsShift;
        _clipsCanvas.RenderTransform = _clipsShift;
        _rulerContent.RenderTransform = _rulerShift;
        _rulerCanvas.Children.Add(_rulerContent);
        _ticks.Stroke = brushes.TextDisabled;
        _ticks.StrokeThickness = 1;
        _rulerContent.Children.Add(_ticks);
        _timeRangeRuler.Fill = brushes.Text;
        _timeRangeRuler.Opacity = 0.5;
        _rulerCanvas.Children.Add(_timeRangeRuler);

        _playheadLine.Fill = brushes.Accent;
        _playheadHead.Fill = brushes.Accent;
        _playheadHead.Data = PlayheadHeadGeometry();
        _playheadLayer.Children.Add(_playheadLine);
        _playheadLayer.Children.Add(_playheadHead);

        _ghost.Background = brushes.Ghost;
        _ghost.BorderBrush = brushes.Accent;
        _linkedGhost.Background = brushes.Ghost;
        _linkedGhost.BorderBrush = brushes.Accent;
        _insertLine.Fill = brushes.Accent;
        _snapGuide.Fill = brushes.Keyframe;
        _marquee.Background = brushes.AccentSubtle;
        _marquee.BorderBrush = brushes.Accent;
        _timeRange.Fill = brushes.TimeRange;
        _balloon.Background = brushes.Panel;
        _balloon.BorderBrush = brushes.Accent;
        _balloon.BorderThickness = new Thickness(1);
        _balloonText.Foreground = brushes.Text;
        _balloon.Child = _balloonText;
        _cursorBadge.Background = brushes.Accent;
        _cursorBadgeText.Foreground = brushes.Panel;
        _cursorBadge.Child = _cursorBadgeText;
        foreach (UIElement element in (UIElement[])[_timeRange, _ghost, _linkedGhost, _insertLine, _snapGuide, _marquee, _balloon, _cursorBadge])
        {
            element.Visibility = Visibility.Collapsed;
            _overlayCanvas.Children.Add(element);
        }
    }

    public TimelineViewport Viewport { get; } = new();

    public TrackLayout? Layout { get; set; }

    public double ScrollY { get; set; }

    /// <summary>Alto visible del área de pistas.</summary>
    public double LanesHeight { get; set; }

    /// <summary>Cuántas veces se armó la ventana de elementos (para medir).</summary>
    public int Realizations { get; private set; }

    /// <summary>Lo que tardó el último <see cref="Render"/>, en milisegundos.</summary>
    public double LastRenderMs { get; private set; }

    /// <summary>Factor de zoom máximo que se muestra estirando lo dibujado antes de rearmarlo.</summary>
    public const double MaxPreviewScale = 1.6;

    /// <summary>Hay un zoom continuo en curso (rueda, deslizador): se puede estirar en vez de rearmar en cada paso.</summary>
    public bool ZoomPreview { get; set; }

    /// <summary>Lo dibujado está estirado: falta rearmarlo con el zoom final.</summary>
    public bool IsPreviewing { get; private set; }

    /// <summary>Cambió el proyecto, la selección o algo de lo que se dibuja en los clips: la próxima vez se rearma.</summary>
    public void InvalidateContent() => _contentDirty = true;

    public void Render(TimelineScene scene, TimelineOverlay overlay)
    {
        long start = Stopwatch.GetTimestamp();
        if (Layout is null)
        {
            return;
        }

        TimelineViewport vp = Viewport;
        double scale = vp.PixelsPerSecond / _realizedPixelsPerSecond;
        bool outside = vp.ScrollX < _windowLeft * scale || vp.ScrollX + vp.Width > _windowRight * scale;
        bool zoomed = _realizedPixelsPerSecond != vp.PixelsPerSecond;
        bool preview = zoomed && ZoomPreview && scale is >= 1 / MaxPreviewScale and <= MaxPreviewScale;
        if (_contentDirty || outside || (zoomed && !preview) || _realizedWidth != vp.Width)
        {
            Realize(scene);
            scale = 1;
        }

        // Durante un zoom continuo lo ya dibujado se estira; al terminar (o al pasar de cierto factor) se rearma nítido.
        bool previewing = scale != 1;
        if (previewing != IsPreviewing)
        {
            // Mientras se estira, lo dibujado va como una sola imagen: escalarla no vuelve a rasterizar formas ni texto.
            _clipsCanvas.CacheMode = previewing ? new BitmapCache() : null;
            _rulerContent.CacheMode = previewing ? new BitmapCache() : null;
        }

        IsPreviewing = previewing;
        _clipsShift.ScaleX = scale;
        _clipsShift.TranslateX = (_windowLeft * scale) - vp.ScrollX;
        _clipsShift.TranslateY = -ScrollY;
        _rowsShift.Y = -ScrollY;
        _rulerShift.ScaleX = scale;
        _rulerShift.TranslateX = (_windowLeft * scale) - vp.ScrollX;
        RenderOverlay(overlay);
        RenderPlayhead(scene.Position);
        LastRenderMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    public void RenderPlayhead(Time position)
    {
        double x = Math.Round(Viewport.TimeToX(position));
        bool visible = x >= -6 && x <= Viewport.Width + 6;
        _playheadLine.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _playheadHead.Visibility = _playheadLine.Visibility;
        _playheadLine.Height = RulerHeight + LanesHeight;
        Canvas.SetLeft(_playheadLine, x);
        Canvas.SetLeft(_playheadHead, x);
    }

    private static PathGeometry PlayheadHeadGeometry()
    {
        var segment = new PolyLineSegment();
        segment.Points.Add(new Point(6, 0));
        segment.Points.Add(new Point(6, 9));
        segment.Points.Add(new Point(0.5, 15));
        segment.Points.Add(new Point(-5, 9));
        segment.Points.Add(new Point(-5, 0));
        var figure = new PathFigure { StartPoint = new Point(-5, 0), IsClosed = true, IsFilled = true };
        figure.Segments.Add(segment);
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    /// <summary>«+3», «−0,5»: el desfase en cuadros de cada clip desincronizado de su enlazado.</summary>
    private static Dictionary<string, string> SyncBadges(Sequence sequence)
    {
        var badges = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (SyncOffset offset in LinkSync.Find(sequence))
        {
            badges[offset.ClipId] = ClipLabels.SyncFrames(offset.Frames(sequence.FrameRate));
        }

        return badges;
    }

    private void Realize(TimelineScene scene)
    {
        TimelineViewport vp = Viewport;
        TrackLayout layout = Layout!;
        _contentDirty = false;
        _realizedPixelsPerSecond = vp.PixelsPerSecond;
        _realizedWidth = vp.Width;
        _windowLeft = Math.Max(0, vp.ScrollX - vp.Width);
        _windowRight = vp.ScrollX + (2 * vp.Width);
        Realizations++;

        double pps = vp.PixelsPerSecond;
        double windowWidth = _windowRight - _windowLeft;
        Time from = Time.FromSeconds(_windowLeft / pps);
        Time to = Time.FromSeconds(_windowRight / pps);
        double visibleLeft = vp.ScrollX - _windowLeft;

        RealizeRows(scene, layout, vp.Width);

        Dictionary<string, string> sync = SyncBadges(scene.Sequence);
        int used = 0;
        foreach (TrackRow row in layout.Rows)
        {
            List<Clip> clips = row.Track.Clips;
            for (int i = TimelineHitTest.FirstEndingAfter(clips, from); i < clips.Count && clips[i].Start < to; i++)
            {
                Clip clip = clips[i];
                double x0 = (clip.Start.ToSeconds() * pps) - _windowLeft;
                double x1 = (clip.End.ToSeconds() * pps) - _windowLeft;
                double left = Math.Max(x0, -4);
                double right = Math.Min(x1 - 1, windowWidth + 4);
                string name = ClipLabels.Name(scene.Project, clip);
                var draw = new ClipDraw(
                    clip,
                    row.Kind,
                    name,
                    left,
                    row.Top + 2,
                    Math.Max(1, right - left),
                    row.Height - 4,
                    x0,
                    pps,
                    _windowLeft,
                    visibleLeft,
                    scene.Selection.Contains(clip.Id),
                    ClipDecorations.Veil(clip, row.Track),
                    scene.ShowEnvelopes,
                    sync.GetValueOrDefault(clip.Id));
                ClipAt(used++).Update(draw, scene.Visuals);
            }
        }

        for (int i = used; i < _clips.Count; i++)
        {
            _clips[i].Hide();
        }

        RealizeTransitions(scene, layout, from, to);
        RealizeCrossfades(scene, layout, from, to);
        RealizeMarkers(scene, layout, from, to);
        RealizeRuler(scene, from, to);
    }

    private ClipVisual ClipAt(int index)
    {
        if (index == _clips.Count)
        {
            var visual = new ClipVisual(_brushes);
            _clips.Add(visual);
            _clipsCanvas.Children.Add(visual.Root);
        }

        return _clips[index];
    }

    private void RealizeRows(TimelineScene scene, TrackLayout layout, double width)
    {
        for (int i = 0; i < layout.Rows.Count; i++)
        {
            TrackRow row = layout.Rows[i];
            Rectangle rect = Pooled(_rows, _rowsCanvas, i);
            rect.Width = width;
            rect.Height = row.Height;
            rect.Fill = row.IsMain ? _brushes.MainLane : _brushes.Panel;
            rect.Opacity = row.Track.Locked ? 0.6 : 1;
            Canvas.SetTop(rect, row.Top);
        }

        HideFrom(_rows, layout.Rows.Count);
        _ = scene;
    }

    private void RealizeCrossfades(TimelineScene scene, TrackLayout layout, Time from, Time to)
    {
        int used = 0;
        if (scene.AutoCrossfade && scene.MainTrack is { } main && layout.Find(main.Id) is { } row)
        {
            foreach ((Clip _, Time cut) in TransitionPlanning.OpenCuts(main))
            {
                if (cut < from || cut > to)
                {
                    continue;
                }

                Path mark = Pooled(_crossfades, _clipsCanvas, used++, () => new Path
                {
                    Data = CrossfadeGeometry(),
                    Stroke = _brushes.Accent,
                    StrokeThickness = 1.5,
                    Fill = _brushes.Background,
                });
                Canvas.SetZIndex(mark, 2);
                Canvas.SetLeft(mark, (cut.ToSeconds() * Viewport.PixelsPerSecond) - _windowLeft);
                Canvas.SetTop(mark, row.Top + (row.Height / 2));
            }
        }

        HideFrom(_crossfades, used);
    }

    /// <summary>Cada transición es un bloque sobre su corte con una X y una manija en cada borde.</summary>
    private void RealizeTransitions(TimelineScene scene, TrackLayout layout, Time from, Time to)
    {
        double pps = Viewport.PixelsPerSecond;
        int used = 0;
        foreach (TrackRow row in layout.Rows)
        {
            Track track = row.Track;
            foreach (TransitionInstance transition in track.Transitions)
            {
                if (TransitionPlanning.CutOf(track, transition) is not { } cut || cut + transition.After < from || cut - transition.Before > to)
                {
                    continue;
                }

                double left = ((cut - transition.Before).ToSeconds() * pps) - _windowLeft;
                double right = ((cut + transition.After).ToSeconds() * pps) - _windowLeft;
                if (right - left < TransitionPlanning.MinBlockWidth)
                {
                    double center = (cut.ToSeconds() * pps) - _windowLeft;
                    left = center - (TransitionPlanning.MinBlockWidth / 2);
                    right = center + (TransitionPlanning.MinBlockWidth / 2);
                }

                (double top, double bottom) = TransitionPlanning.Band(row);
                bool selected = transition.Id == scene.SelectedTransitionId;
                Border block = Pooled(_transitions, _clipsCanvas, used++, CreateTransitionBlock);
                block.Width = right - left;
                block.Height = bottom - top;
                block.BorderThickness = new Thickness(selected ? 2 : 1);
                block.BorderBrush = selected ? _brushes.Text : _brushes.Accent;
                Canvas.SetZIndex(block, 2);
                Canvas.SetLeft(block, left);
                Canvas.SetTop(block, top);
            }
        }

        HideFrom(_transitions, used);
    }

    private Border CreateTransitionBlock()
    {
        var grid = new Grid();
        grid.Children.Add(new Path
        {
            Data = new GeometryGroup
            {
                Children =
                {
                    new LineGeometry { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) },
                    new LineGeometry { StartPoint = new Point(0, 1), EndPoint = new Point(1, 0) },
                },
            },
            Stretch = Stretch.Fill,
            Stroke = _brushes.Accent,
            StrokeThickness = 1,
            Margin = new Thickness(4, 3, 4, 3),
        });
        grid.Children.Add(new Rectangle { Width = 3, Fill = _brushes.Accent, HorizontalAlignment = HorizontalAlignment.Left });
        grid.Children.Add(new Rectangle { Width = 3, Fill = _brushes.Accent, HorizontalAlignment = HorizontalAlignment.Right });
        return new Border { Background = _brushes.AccentSubtle, Child = grid };
    }

    private static PathGeometry CrossfadeGeometry()
    {
        // Un rombo con una X: el símbolo del fundido cruzado sobre el corte.
        var geometry = new PathGeometry();
        var rim = new PathFigure { StartPoint = new Point(0, -8), IsClosed = true, IsFilled = true };
        var rimSegment = new PolyLineSegment();
        rimSegment.Points.Add(new Point(8, 0));
        rimSegment.Points.Add(new Point(0, 8));
        rimSegment.Points.Add(new Point(-8, 0));
        rim.Segments.Add(rimSegment);
        geometry.Figures.Add(rim);
        foreach ((Point a, Point b) in new[] { (new Point(-3.5, -3.5), new Point(3.5, 3.5)), (new Point(-3.5, 3.5), new Point(3.5, -3.5)) })
        {
            var line = new PathFigure { StartPoint = a, IsClosed = false, IsFilled = false };
            line.Segments.Add(new LineSegment { Point = b });
            geometry.Figures.Add(line);
        }

        return geometry;
    }

    private void RealizeMarkers(TimelineScene scene, TrackLayout layout, Time from, Time to)
    {
        double pps = Viewport.PixelsPerSecond;
        int bands = 0;
        int lines = 0;
        int flags = 0;
        int names = 0;
        int rulerBands = 0;
        foreach (Region region in scene.Sequence.Regions)
        {
            if (region.End < from || region.Start > to)
            {
                continue;
            }

            double x0 = Math.Max(-4, (region.Start.ToSeconds() * pps) - _windowLeft);
            double x1 = Math.Min(_windowRight - _windowLeft + 4, (region.End.ToSeconds() * pps) - _windowLeft);
            var own = _brushes.Marked(region.Color);
            Rectangle band = Pooled(_regionBands, _clipsCanvas, bands++);
            band.Fill = own?.Band ?? _brushes.Region;
            band.Width = Math.Max(1, x1 - x0);
            band.Height = layout.TotalHeight;
            Canvas.SetZIndex(band, 0);
            Canvas.SetLeft(band, x0);
            Canvas.SetTop(band, 0);

            Rectangle ruler = Pooled(_regionRulerBands, _rulerContent, rulerBands++);
            ruler.Fill = own?.Ruler ?? _brushes.RegionRuler;
            ruler.Width = Math.Max(1, x1 - x0);
            ruler.Height = 9;
            Canvas.SetLeft(ruler, x0);
            Canvas.SetTop(ruler, 0);
            Label(ref names, region.Name, x0 + 4, -1, _brushes.Text);
        }

        List<Marker> sortedMarkers = [.. scene.Sequence.Markers.OrderBy(m => m.Time)];
        for (int i = 0; i < sortedMarkers.Count; i++)
        {
            Marker marker = sortedMarkers[i];
            if (marker.Time < from || marker.Time > to)
            {
                continue;
            }

            double x = Math.Round((marker.Time.ToSeconds() * pps) - _windowLeft);
            var color = _brushes.Marked(marker.Color);
            Rectangle line = Pooled(_markerLines, _clipsCanvas, lines++);
            line.Fill = color?.Line ?? _brushes.MarkerLine;
            line.Width = 1;
            line.Height = layout.TotalHeight;
            Canvas.SetZIndex(line, 3);
            Canvas.SetLeft(line, x);
            Canvas.SetTop(line, 0);

            Path flag = Pooled(_markerFlags, _rulerContent, flags++, () => new Path { Data = MarkerGeometry() });
            flag.Fill = color?.Solid ?? _brushes.Keyframe;
            flag.IsHitTestVisible = true;
            Canvas.SetLeft(flag, x);
            Canvas.SetTop(flag, 0);
            ToolTipService.SetToolTip(flag, string.IsNullOrEmpty(marker.Name) ? null : marker.Name);

            double? nextX = i + 1 < sortedMarkers.Count
                ? Math.Round((sortedMarkers[i + 1].Time.ToSeconds() * pps) - _windowLeft)
                : null;
            double available = MarkerLabelLayout.AvailableWidth(x, nextX);
            string? text = MarkerLabelLayout.Truncate(marker.Name, available);
            if (text is not null)
            {
                Label(ref names, text, x + 8, -1, color?.Solid ?? _brushes.Keyframe);
            }
        }

        HideFrom(_regionBands, bands);
        HideFrom(_markerLines, lines);
        HideFrom(_markerFlags, flags);
        HideFrom(_markerNames, names);
        HideFrom(_regionRulerBands, rulerBands);

        void Label(ref int index, string text, double x, double y, Brush brush)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            TextBlock label = Pooled(_markerNames, _rulerContent, index++, () => new TextBlock { FontSize = ThemeResources.Size("MicroFontSize"), IsHitTestVisible = false });
            label.Text = text;
            label.Foreground = brush;
            Canvas.SetLeft(label, x);
            Canvas.SetTop(label, y);
        }
    }

    private static PathGeometry MarkerGeometry()
    {
        var segment = new PolyLineSegment();
        segment.Points.Add(new Point(7, 0));
        segment.Points.Add(new Point(7, 6));
        segment.Points.Add(new Point(0.5, 11));
        segment.Points.Add(new Point(0, 11));
        var figure = new PathFigure { StartPoint = new Point(0, 0), IsClosed = true, IsFilled = true };
        figure.Segments.Add(segment);
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private void RealizeRuler(TimelineScene scene, Time from, Time to)
    {
        TimelineViewport vp = Viewport;
        RulerStep step = RulerScale.ChooseStep(vp.PixelsPerSecond, scene.Sequence.FrameRate);
        var window = new TimelineViewport { PixelsPerSecond = vp.PixelsPerSecond, ScrollX = _windowLeft, Width = _windowRight - _windowLeft };
        List<RulerTick> ticks = RulerScale.Ticks(window, step);
        var group = new GeometryGroup();
        int labels = 0;
        foreach (RulerTick tick in ticks)
        {
            double x = Math.Round(tick.X) + 0.5;
            double height = tick.IsMajor ? 10 : 5;
            group.Children.Add(new LineGeometry { StartPoint = new Point(x, RulerHeight - height), EndPoint = new Point(x, RulerHeight) });
            if (tick.IsMajor)
            {
                TextBlock label = Pooled(_rulerLabels, _rulerContent, labels++, () => new TextBlock
                {
                    FontSize = ThemeResources.Size("MicroFontSize"),
                    Foreground = _brushes.TextMuted,
                    IsHitTestVisible = false,
                });
                label.Text = RulerScale.FormatLabel(tick.Time, step, scene.Sequence.FrameRate);
                Canvas.SetLeft(label, x + 3);
                Canvas.SetTop(label, 9);
            }
        }

        _ticks.Data = group;
        HideFrom(_rulerLabels, labels);
        _ = from;
        _ = to;
    }

    private void RenderOverlay(TimelineOverlay overlay)
    {
        TimelineViewport vp = Viewport;
        TrackLayout layout = Layout!;
        PlaceGhost(_ghost, overlay.Ghost, layout);
        PlaceGhost(_linkedGhost, overlay.LinkedGhost, layout);

        if (overlay.InsertLine is { } insert && layout.Find(insert.TrackId) is { } insertRow)
        {
            _insertLine.Visibility = Visibility.Visible;
            _insertLine.Height = insertRow.Height + 6;
            Canvas.SetLeft(_insertLine, Math.Round(vp.TimeToX(insert.At)) - 1);
            Canvas.SetTop(_insertLine, insertRow.Top - 3 - ScrollY);
        }
        else
        {
            _insertLine.Visibility = Visibility.Collapsed;
        }

        if (overlay.SnapGuide is { } guide)
        {
            _snapGuide.Visibility = Visibility.Visible;
            _snapGuide.Height = Math.Max(LanesHeight, 0);
            Canvas.SetLeft(_snapGuide, Math.Round(vp.TimeToX(guide)));
            Canvas.SetTop(_snapGuide, 0);
        }
        else
        {
            _snapGuide.Visibility = Visibility.Collapsed;
        }

        PlaceBalloon(overlay.Balloon, layout);

        if (overlay.Marquee is { } m)
        {
            _marquee.Visibility = Visibility.Visible;
            _marquee.Width = m.Width;
            _marquee.Height = m.Height;
            Canvas.SetLeft(_marquee, m.X);
            Canvas.SetTop(_marquee, m.Y - ScrollY);
        }
        else
        {
            _marquee.Visibility = Visibility.Collapsed;
        }

        if (overlay.TimeRange is { } range && range.End > range.Start)
        {
            double x0 = vp.TimeToX(range.Start);
            double x1 = vp.TimeToX(range.End);
            double left = Math.Max(x0, -2);
            double width = Math.Max(1, Math.Min(x1, vp.Width + 2) - left);
            _timeRange.Visibility = Visibility.Visible;
            _timeRange.Width = width;
            _timeRange.Height = Math.Max(LanesHeight, 0);
            Canvas.SetLeft(_timeRange, left);
            _timeRangeRuler.Visibility = Visibility.Visible;
            _timeRangeRuler.Width = width;
            Canvas.SetLeft(_timeRangeRuler, left);
            Canvas.SetTop(_timeRangeRuler, RulerHeight - 4);
        }
        else
        {
            _timeRange.Visibility = Visibility.Collapsed;
            _timeRangeRuler.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// La marca de la herramienta junto al puntero («]|[» roll, «[↔]» slide): con la flecha del sistema forma el cursor
    /// propio de cada una. <c>null</c> la quita. Va aparte del dibujo para no rearmar nada en cada movimiento.
    /// </summary>
    public void PlaceCursorBadge(string? text, double x, double y)
    {
        if (text is null)
        {
            _cursorBadge.Visibility = Visibility.Collapsed;
            return;
        }

        if (!string.Equals(_cursorBadgeText.Text, text, StringComparison.Ordinal))
        {
            _cursorBadgeText.Text = text;
        }

        _cursorBadge.Visibility = Visibility.Visible;
        Canvas.SetLeft(_cursorBadge, x + 12);
        Canvas.SetTop(_cursorBadge, y + 14);
    }

    private void PlaceBalloon((string TrackId, Time At, string Text)? balloon, TrackLayout layout)
    {
        if (balloon is not { } b || layout.Find(b.TrackId) is not { } row)
        {
            _balloon.Visibility = Visibility.Collapsed;
            return;
        }

        if (!string.Equals(_balloonText.Text, b.Text, StringComparison.Ordinal))
        {
            _balloonText.Text = b.Text;
        }

        _balloon.Visibility = Visibility.Visible;
        _balloon.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = _balloon.DesiredSize.Width;
        double x = Viewport.TimeToX(b.At) - (width / 2);
        Canvas.SetLeft(_balloon, Math.Clamp(x, 2, Math.Max(2, Viewport.Width - width - 2)));
        // Encima de la fila; si es la primera, dentro de ella para no tapar la regla.
        double top = row.Top - ScrollY - _balloon.DesiredSize.Height - 2;
        Canvas.SetTop(_balloon, top >= 0 ? top : row.Top - ScrollY + 2);
    }

    private void PlaceGhost(Border ghost, (string TrackId, Time Start, Time End)? place, TrackLayout layout)
    {
        if (place is not { } g || layout.Find(g.TrackId) is not { } row)
        {
            ghost.Visibility = Visibility.Collapsed;
            return;
        }

        double x0 = Viewport.TimeToX(g.Start);
        double x1 = Viewport.TimeToX(g.End);
        double left = Math.Max(x0, -4);
        ghost.Visibility = Visibility.Visible;
        ghost.Width = Math.Max(2, Math.Min(x1, Viewport.Width + 4) - left);
        ghost.Height = row.Height - 4;
        Canvas.SetLeft(ghost, left);
        Canvas.SetTop(ghost, row.Top + 2 - ScrollY);
    }

    private static T Pooled<T>(List<T> pool, Canvas parent, int index, Func<T>? create = null)
        where T : FrameworkElement, new()
    {
        if (index == pool.Count)
        {
            T element = create is null ? new T() : create();
            element.IsHitTestVisible = false;
            pool.Add(element);
            parent.Children.Add(element);
        }

        T item = pool[index];
        item.Visibility = Visibility.Visible;
        return item;
    }

    private static void HideFrom<T>(List<T> pool, int from)
        where T : UIElement
    {
        for (int i = from; i < pool.Count; i++)
        {
            pool[i].Visibility = Visibility.Collapsed;
        }
    }
}
