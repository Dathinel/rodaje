using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Rodaje.App.Services.Media;
using Rodaje.App.Theme;
using Rodaje.App.Views.Shared;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Rodaje.App.Views.Timeline;

/// <summary>Lo que hace falta para dibujar un clip: su tramo ya recortado a la ventana y cómo pasar de x a tiempo.</summary>
internal readonly record struct ClipDraw(
    Clip Clip,
    TrackKind Kind,
    string Name,
    double Left,
    double Top,
    double Width,
    double Height,
    double ClipX,
    double PixelsPerSecond,
    double WindowLeft,
    double VisibleLeft,
    bool Selected,
    double Veil,
    bool ShowEnvelope,
    string? SyncText = null);

/// <summary>
/// Un clip dibujado, reciclable: bloque con su color, nombre, tira de película o forma de onda, keyframes y envolvente.
/// Las coordenadas son locales al bloque; el bloque solo cubre la parte del clip que cae en la ventana dibujada.
/// </summary>
internal sealed class ClipVisual
{
    private const double NameHeight = 15;
    private const double DetailMinWidth = 14;
    private const double BadgeCurveWidth = 18;

    // Con una curva, si el nombre del clip se quedaría con menos que esto, el indicador muestra solo la mini curva.
    private const double MinNameRoom = 40;

    /// <summary>Escala si todavía no se conoce la de la pantalla: las miniaturas se piden en píxeles físicos.</summary>
    private const double FallbackScale = 1.5;

    private readonly TimelineBrushes _brushes;
    // Solo fondos, sin trazo ni esquinas redondeadas: así se componen en la GPU; un trazo se rasteriza en cada cambio de tamaño.
    private readonly Border _body = new();
    private readonly Border _edge = new() { Height = 2 };
    private readonly Border _outline = new() { BorderThickness = new Thickness(2) };
    private readonly Rectangle _strip = new();
    private readonly LinearGradientBrush _stripBrush;
    private readonly TranslateTransform _stripShift = new();
    private readonly Canvas _thumbs = new();
    private readonly RectangleGeometry _thumbsBounds = new();
    private readonly List<Image> _thumbPool = [];
    private readonly Path _wave = new();
    private readonly RectangleGeometry _waveBounds = new();
    private (string MediaId, ClipTimeMap Map, Time Low, Time High, int Count, double Width, double Height)? _waveKey;
    private ClipDraw _last;
    private IClipVisuals? _visuals;
    private readonly Border _veil = new();
    private readonly Polyline _envelope = new() { StrokeThickness = 1.5 };
    private readonly Path _envelopePoints = new() { StrokeThickness = 1 };
    private readonly Path _keys = new();
    private readonly TextBlock _name = new() { FontSize = ThemeResources.TimelineSize("CaptionFontSize"), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Border _badge = new() { Padding = new Thickness(4, 0, 4, 1), Height = NameHeight - 1 };
    private readonly TextBlock _badgeText = new() { FontSize = ThemeResources.TimelineSize("MicroFontSize"), TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };

    // La mini curva, a la derecha del texto del indicador.
    private readonly Polyline _badgeCurve = new()
    {
        Width = BadgeCurveWidth,
        Height = NameHeight - 4,
        StrokeThickness = 1.25,
        StrokeLineJoin = PenLineJoin.Round,
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed,
    };

    private string? _badgeFullText;
    private double _badgeFullWidth;
    private IReadOnlyList<SpeedPoint>? _badgeCurvePoints;
    private (double From, double To) _badgeCurveWindow;
    private double _badgeWidth;
    private double _badgeLeft;
    private readonly RectangleGeometry _bounds = new();

    // Aviso de desfase con su enlazado («+3», en cuadros), a la derecha de la fila del nombre.
    private readonly Border _sync = new() { Padding = new Thickness(4, 0, 4, 1), Height = NameHeight - 1, Visibility = Visibility.Collapsed };
    private readonly TextBlock _syncText = new() { FontSize = ThemeResources.TimelineSize("MicroFontSize"), TextWrapping = TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center };

    public ClipVisual(TimelineBrushes brushes)
    {
        _brushes = brushes;
        _stripBrush = brushes.CreateFilmstripBrush(_stripShift);
        _strip.Fill = _stripBrush;
        _wave.Fill = brushes.Waveform;
        _keys.Fill = brushes.Keyframe;
        _keys.Stroke = brushes.Background;
        _keys.StrokeThickness = 1.5;
        _envelope.Stroke = brushes.Text;
        _envelopePoints.Fill = brushes.Text;
        _envelopePoints.Stroke = brushes.Background;
        _name.Foreground = brushes.Text;
        _badgeText.Foreground = brushes.Text;
        _badgeCurve.Stroke = brushes.Text;
        _badge.Background = brushes.Scrim;
        var badgeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        badgeRow.Children.Add(_badgeText);
        badgeRow.Children.Add(_badgeCurve);
        _badge.Child = badgeRow;
        // Medido fuera del árbol el indicador puede dar 0 y el nombre quedaría debajo: al conocer su ancho real se corre el nombre.
        _badge.SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Width - _badgeWidth) < 0.5 || _badge.Visibility != Visibility.Visible)
            {
                return;
            }

            _badgeWidth = e.NewSize.Width;
            if (_badgeText.Visibility == Visibility.Visible)
            {
                _badgeFullWidth = e.NewSize.Width;
            }

            PlaceName(_badgeLeft + _badgeWidth + 2, Root.Width);
        };
        Root.Children.Add(_body);
        Root.Children.Add(_edge);
        Root.Children.Add(_strip);
        Root.Children.Add(_thumbs);
        Root.Children.Add(_wave);
        Root.Children.Add(_envelope);
        Root.Children.Add(_envelopePoints);
        Root.Children.Add(_name);
        Root.Children.Add(_badge);
        _sync.Child = _syncText;
        _sync.Background = brushes.Danger;
        _syncText.Foreground = brushes.Text;
        Root.Children.Add(_sync);
        Root.Children.Add(_veil);
        Root.Children.Add(_outline);
        // Encima del velo y del contorno: los rombos se ven igual en cualquier estado del clip o de la pista.
        Root.Children.Add(_keys);
        _veil.Background = brushes.Background;
        _thumbs.Clip = _thumbsBounds;
        _wave.Clip = _waveBounds;
        _outline.BorderBrush = brushes.Accent;
        _name.Clip = _bounds;
        Canvas.SetZIndex(Root, 1);
    }

    public Canvas Root { get; } = new();

    public string? ClipId { get; private set; }

    public string? MediaId => ClipId is null ? null : _last.Clip.MediaId;

    /// <summary>Vuelve a dibujar el clip con lo último que se le pidió (llegaron miniaturas o picos de su medio).</summary>
    public void Refresh()
    {
        _waveKey = null;
        Update(_last, _visuals);
    }

    public void Hide()
    {
        ClipId = null;
        Root.Visibility = Visibility.Collapsed;
    }

    public void Update(in ClipDraw d, IClipVisuals? visuals)
    {
        ClipId = d.Clip.Id;
        _last = d;
        _visuals = visuals;
        // El tamaño de fuente de la línea de tiempo se cambia en Preferencias sin reiniciar.
        double nameSize = ThemeResources.TimelineSize("CaptionFontSize");
        if (_name.FontSize != nameSize)
        {
            _name.FontSize = nameSize;
            _badgeText.FontSize = _syncText.FontSize = ThemeResources.TimelineSize("MicroFontSize");
        }

        ClipTimeMap map = d.Clip.TimeMap;
        Root.Visibility = Visibility.Visible;
        Canvas.SetLeft(Root, d.Left);
        Canvas.SetTop(Root, d.Top);
        Root.Width = d.Width;
        Root.Height = d.Height;
        UpdateVeil(d);

        _body.Width = d.Width;
        _body.Height = d.Height;
        bool title = d.Clip.Generator is not null;
        bool noMedia = title || d.Clip.IsCompound;
        var label = _brushes.Marked(d.Clip.Color);
        _body.Background = label is { } tint ? tint.Line : title ? _brushes.TextClip : _brushes.ClipFill(d.Kind);
        _edge.Width = d.Width;
        _edge.Background = label is { } mark ? mark.Solid : title ? _brushes.TextClipEdge : _brushes.ClipEdge(d.Kind);
        UpdateSync(d);
        _outline.Visibility = d.Selected ? Visibility.Visible : Visibility.Collapsed;
        if (d.Selected)
        {
            _outline.Width = d.Width;
            _outline.Height = d.Height;
        }

        bool detailed = d.Width >= DetailMinWidth;
        UpdateName(d);
        // Un título o un compuesto no tienen medio: ni miniaturas ni forma de onda, solo su color y su nombre.
        if (d.Kind == TrackKind.Video && detailed && !noMedia)
        {
            UpdateFilmstrip(d, map, visuals);
        }
        else
        {
            _strip.Visibility = Visibility.Collapsed;
            HideThumbs(0);
        }

        if (d.Kind == TrackKind.Audio && detailed && !noMedia)
        {
            UpdateWaveform(d, map, visuals);
        }
        else
        {
            _wave.Visibility = Visibility.Collapsed;
        }

        UpdateEnvelope(d, map);
        UpdateKeyframes(d);
    }

    // Un velo encima en vez de la opacidad del bloque: cambia con cada estado de la pista sin depender de cómo compone
    // WinUI un grupo con imágenes, y vela por igual miniaturas, onda y nombre.
    private void UpdateVeil(in ClipDraw d)
    {
        if (d.Veil <= 0)
        {
            _veil.Visibility = Visibility.Collapsed;
            return;
        }

        _veil.Width = d.Width;
        _veil.Height = d.Height;
        _veil.Opacity = d.Veil;
        _veil.Visibility = Visibility.Visible;
    }

    /// <summary>La x local de un instante de la línea de tiempo.</summary>
    private static double LocalX(in ClipDraw d, Time time) => (time.ToSeconds() * d.PixelsPerSecond) - d.WindowLeft - d.Left;

    /// <summary>El instante de la línea de tiempo bajo una x local.</summary>
    private static Time TimeAt(in ClipDraw d, double x) => Time.FromSeconds(Math.Max(0, (x + d.Left + d.WindowLeft) / d.PixelsPerSecond));

    private void UpdateName(in ClipDraw d)
    {
        double left = UpdateBadge(d, Math.Max(5, d.VisibleLeft - d.Left + 5));
        double room = d.Width - left - 4;
        if (room < 18)
        {
            _name.Visibility = Visibility.Collapsed;
            return;
        }

        _name.Visibility = Visibility.Visible;
        if (!string.Equals(_name.Text, d.Name, StringComparison.Ordinal))
        {
            _name.Text = d.Name;
        }

        PlaceName(left, d.Width);
    }

    private void UpdateSync(in ClipDraw d)
    {
        if (d.SyncText is not { } text || d.Width < 48)
        {
            _sync.Visibility = Visibility.Collapsed;
            return;
        }

        if (!string.Equals(_syncText.Text, text, StringComparison.Ordinal))
        {
            _syncText.Text = text;
        }

        _sync.Visibility = Visibility.Visible;
        _sync.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(_sync, Math.Max(2, d.Width - _sync.DesiredSize.Width - 3));
        Canvas.SetTop(_sync, 1);
    }

    private void PlaceName(double left, double width)
    {
        double room = Math.Max(0, width - left - 4);

        // Puntos suspensivos en vez de un corte seco; se vuelve a medir solo si el hueco cambia de verdad (no en cada paso del zoom estirado).
        if (Math.Abs(_name.MaxWidth - room) >= 1)
        {
            _name.MaxWidth = room;
        }

        _bounds.Rect = new Rect(0, 0, room, NameHeight + 2);
        Canvas.SetLeft(_name, left);
        Canvas.SetTop(_name, 0);
    }

    /// <summary>El indicador de velocidad delante del nombre; devuelve dónde empieza el nombre.</summary>
    private double UpdateBadge(in ClipDraw d, double left)
    {
        string? text = SpeedBadge.Text(d.Clip);
        if (text is null)
        {
            _badge.Visibility = Visibility.Collapsed;
            return left;
        }

        bool curveChanged = UpdateBadgeCurve(SpeedBadge.CurveShape(d.Clip));
        bool hasCurve = _badgeCurve.Visibility == Visibility.Visible;
        if (curveChanged || !string.Equals(_badgeFullText, text, StringComparison.Ordinal))
        {
            _badgeFullText = text;
            _badgeText.Text = text;
            _badgeText.Visibility = Visibility.Visible;
            _badge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _badgeFullWidth = _badge.DesiredSize.Width;
        }

        // El nombre del clip no queda tapado: con poco sitio el texto del indicador cede y queda solo la mini curva.
        double room = d.Width - left - 4;
        bool compact = hasCurve && room - _badgeFullWidth < MinNameRoom;
        _badgeText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        _badgeWidth = compact ? _badge.Padding.Left + _badge.Padding.Right + BadgeCurveWidth : _badgeFullWidth;
        if (room < _badgeWidth)
        {
            _badge.Visibility = Visibility.Collapsed;
            return left;
        }

        _badge.Visibility = Visibility.Visible;
        _badgeLeft = left;
        Canvas.SetLeft(_badge, left - 2);
        Canvas.SetTop(_badge, 1);
        return left + _badgeWidth + 2;
    }

    /// <summary>Dibuja la mini curva si cambió; devuelve si cambió su visibilidad o su forma.</summary>
    private bool UpdateBadgeCurve((IReadOnlyList<SpeedPoint> Points, double From, double To)? shape)
    {
        if (shape is not { } s)
        {
            bool wasVisible = _badgeCurve.Visibility == Visibility.Visible;
            _badgeCurve.Visibility = Visibility.Collapsed;
            _badgeCurvePoints = null;
            return wasVisible;
        }

        if (ReferenceEquals(s.Points, _badgeCurvePoints) && _badgeCurveWindow == (s.From, s.To))
        {
            return false;
        }

        _badgeCurvePoints = s.Points;
        _badgeCurveWindow = (s.From, s.To);
        _badgeCurve.Points = CurveGlyph.Line(s.Points, s.From, s.To, _badgeCurve.Width, _badgeCurve.Height);
        _badgeCurve.Visibility = Visibility.Visible;
        return true;
    }

    private void UpdateFilmstrip(in ClipDraw d, ClipTimeMap map, IClipVisuals? visuals)
    {
        double top = NameHeight;
        double height = d.Height - NameHeight - 2;
        if (height < 8)
        {
            _strip.Visibility = Visibility.Collapsed;
            HideThumbs(0);
            return;
        }

        double tile = Math.Max(24, height * 16 / 9);
        double origin = d.ClipX - d.Left;

        // Un solo rectángulo con un degradado que se repite cada cuadro: sin geometrías nuevas al desplazar o hacer zoom.
        _stripBrush.EndPoint = new Point(tile, 0);
        _stripShift.X = origin;
        _strip.Width = d.Width;
        _strip.Height = height;
        Canvas.SetTop(_strip, top);

        // Las miniaturas van en la rejilla del clip entero: la última (o la primera, si se recortó el inicio o el clip
        // empieza antes de la ventana) se corta en el borde del bloque.
        _thumbsBounds.Rect = new Rect(0, top, d.Width, height);
        int thumbs = 0;
        if (visuals is not null)
        {
            // La separación entre miniaturas decide la rejilla de instantes: un mismo zoom reutiliza las mismas miniaturas.
            Time spacing = ClipMapping.SourceSpacing(map, Time.FromSeconds(tile / d.PixelsPerSecond));
            int pixels = (int)Math.Ceiling(height * (Root.XamlRoot?.RasterizationScale ?? FallbackScale));
            int first = (int)Math.Max(0, Math.Floor(-origin / tile));
            int last = (int)Math.Ceiling((d.Width - origin) / tile);
            for (int k = first; k <= last; k++)
            {
                double x = origin + (k * tile);
                if (x >= d.Width || x + tile <= 0)
                {
                    continue;
                }

                Time sourceTime = map.Source(TimeAt(d, x + (tile / 2)));
                if (visuals.GetNearestThumbnail(d.Clip.MediaId, sourceTime, spacing, pixels) is { } image)
                {
                    Image view = Thumb(thumbs++);
                    view.Source = BgraBitmaps.From(image);
                    view.Width = tile - 2;
                    view.Height = height;
                    Canvas.SetLeft(view, x + 1);
                    Canvas.SetTop(view, top);
                }
            }
        }

        _strip.Visibility = Visibility.Visible;
        HideThumbs(thumbs);
    }

    private Image Thumb(int index)
    {
        if (index == _thumbPool.Count)
        {
            var image = new Image { Stretch = Stretch.UniformToFill };
            _thumbPool.Add(image);
            _thumbs.Children.Add(image);
        }

        Image view = _thumbPool[index];
        view.Visibility = Visibility.Visible;
        return view;
    }

    private void HideThumbs(int from)
    {
        for (int i = from; i < _thumbPool.Count; i++)
        {
            _thumbPool[i].Visibility = Visibility.Collapsed;
            _thumbPool[i].Source = null;
        }
    }

    private void UpdateWaveform(in ClipDraw d, ClipTimeMap map, IClipVisuals? visuals)
    {
        double top = NameHeight - 2;
        double half = (d.Height - top - 3) / 2;
        if (half < 3)
        {
            _wave.Visibility = Visibility.Collapsed;
            return;
        }

        // Puntos repartidos justo entre los dos bordes del bloque: la onda no pide ni dibuja audio de fuera del recorte.
        int count = (int)Math.Ceiling(d.Width / Math.Max(2, d.Width / 160)) + 1;
        double step = d.Width / (count - 1);
        double center = top + half + 1;
        var upper = new Point[count];
        Clip clip = d.Clip;
        _waveBounds.Rect = new Rect(0, 0, d.Width, d.Height);

        // Se pide el tramo del origen que se ve y cada punto toma el suyo con el mapeo: a otra velocidad, en reversa o
        // con una curva la onda sigue al material. Mientras se calcula (o sin sonido) se dibuja el marcador de posición.
        (Time low, Time high) = ClipMapping.VisibleSource(map, TimeAt(d, 0), TimeAt(d, d.Width));
        var key = (clip.MediaId, map, low, high, count, d.Width, d.Height);
        if (_waveKey == key)
        {
            // La misma onda completa que ya está dibujada: no se vuelve a pedir ni a construir su geometría.
            _wave.Visibility = Visibility.Visible;
            return;
        }

        WaveformSlice? slice = visuals?.GetWaveform(clip.MediaId, low, high, count);
        bool real = slice is { IsComplete: true, HasAudio: true } && slice.Buckets == count;
        _waveKey = real ? key : null;
        for (int i = 0; i < count; i++)
        {
            double x = i == count - 1 ? d.Width : i * step;
            Time at = TimeAt(d, x);
            int bucket = map.IsIdentity ? i : ClipMapping.BucketOf(map, at, low, high, count);
            float peak = real
                ? Math.Clamp(Math.Max(-slice!.Min[bucket], slice.Max[bucket]), 0, 1)
                : ClipDecorations.PlaceholderPeak(clip.MediaId, map.Source(at));
            upper[i] = new Point(x, center - (peak * half));
        }

        var segment = new PolyLineSegment();
        foreach (Point p in upper)
        {
            segment.Points.Add(p);
        }

        for (int i = count - 1; i >= 0; i--)
        {
            segment.Points.Add(new Point(upper[i].X, (2 * center) - upper[i].Y));
        }

        var figure = new PathFigure { StartPoint = new Point(0, center), IsClosed = true, IsFilled = true };
        figure.Segments.Add(segment);
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        _wave.Data = geometry;
        _wave.Visibility = Visibility.Visible;
    }

    private void UpdateEnvelope(in ClipDraw d, ClipTimeMap map)
    {
        if (!d.ShowEnvelope || d.Kind != TrackKind.Audio || d.Width < DetailMinWidth)
        {
            _envelope.Visibility = Visibility.Collapsed;
            _envelopePoints.Visibility = Visibility.Collapsed;
            return;
        }

        double top = 4;
        double range = d.Height - 8;
        var points = new PointCollection();
        bool animated = ClipDecorations.HasAnimatedGain(d.Clip);
        double step = animated ? 6 : d.Width;
        for (double x = 0; ; x += step)
        {
            double clamped = Math.Min(x, d.Width);
            double gain = ClipDecorations.GainDbAt(d.Clip, map, TimeAt(d, clamped));
            points.Add(new Point(clamped, top + ((1 - ClipDecorations.EnvelopeFraction(gain)) * range)));
            if (clamped >= d.Width)
            {
                break;
            }
        }

        _envelope.Points = points;
        _envelope.Visibility = Visibility.Visible;

        const double r = 3;
        var squares = new GeometryGroup();
        foreach ((Time time, double gainDb) in ClipDecorations.GainPoints(d.Clip))
        {
            double x = LocalX(d, time);
            if (x >= -r && x <= d.Width + r)
            {
                double y = top + ((1 - ClipDecorations.EnvelopeFraction(gainDb)) * range);
                squares.Children.Add(new RectangleGeometry { Rect = new Rect(x - r, y - r, 2 * r, 2 * r) });
            }
        }

        _envelopePoints.Data = squares;
        _envelopePoints.Visibility = squares.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateKeyframes(in ClipDraw d)
    {
        List<Time> times = d.Width >= DetailMinWidth ? ClipDecorations.KeyframeTimes(d.Clip) : [];
        if (times.Count == 0)
        {
            _keys.Visibility = Visibility.Collapsed;
            return;
        }

        const double r = 5;
        double y = d.Height - r - 3;
        var group = new GeometryGroup();
        foreach (Time time in times)
        {
            double x = LocalX(d, time);
            if (x < -r || x > d.Width + r)
            {
                continue;
            }

            var segment = new PolyLineSegment();
            segment.Points.Add(new Point(x + r, y));
            segment.Points.Add(new Point(x, y + r));
            segment.Points.Add(new Point(x - r, y));
            var figure = new PathFigure { StartPoint = new Point(x, y - r), IsClosed = true, IsFilled = true };
            figure.Segments.Add(segment);
            var diamond = new PathGeometry();
            diamond.Figures.Add(figure);
            group.Children.Add(diamond);
        }

        _keys.Data = group;
        _keys.Visibility = Visibility.Visible;
    }
}
