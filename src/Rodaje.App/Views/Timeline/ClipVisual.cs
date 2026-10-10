using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
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
    bool Dimmed,
    bool ShowEnvelope);

/// <summary>
/// Un clip dibujado, reciclable: bloque con su color, nombre, tira de película o forma de onda, keyframes y envolvente.
/// Las coordenadas son locales al bloque; el bloque solo cubre la parte del clip que cae en la ventana dibujada.
/// </summary>
internal sealed class ClipVisual
{
    private const double NameHeight = 15;
    private const double DetailMinWidth = 14;

    private readonly TimelineBrushes _brushes;
    // Solo fondos, sin trazo ni esquinas redondeadas: así se componen en la GPU; un trazo se rasteriza en cada cambio de tamaño.
    private readonly Border _body = new();
    private readonly Border _edge = new() { Height = 2 };
    private readonly Border _outline = new() { BorderThickness = new Thickness(2) };
    private readonly Rectangle _strip = new();
    private readonly LinearGradientBrush _stripBrush;
    private readonly TranslateTransform _stripShift = new();
    private readonly Canvas _thumbs = new();
    private readonly List<Image> _thumbPool = [];
    private readonly Path _wave = new();
    private readonly Polyline _envelope = new() { StrokeThickness = 1.5 };
    private readonly Path _keys = new();
    private readonly TextBlock _name = new() { FontSize = 11, TextWrapping = TextWrapping.NoWrap };
    private readonly RectangleGeometry _bounds = new();

    public ClipVisual(TimelineBrushes brushes)
    {
        _brushes = brushes;
        _stripBrush = brushes.CreateFilmstripBrush(_stripShift);
        _strip.Fill = _stripBrush;
        _wave.Fill = brushes.Waveform;
        _keys.Fill = brushes.Keyframe;
        _keys.Stroke = brushes.Background;
        _keys.StrokeThickness = 1;
        _envelope.Stroke = brushes.Text;
        _name.Foreground = brushes.Text;
        Root.Children.Add(_body);
        Root.Children.Add(_edge);
        Root.Children.Add(_strip);
        Root.Children.Add(_thumbs);
        Root.Children.Add(_wave);
        Root.Children.Add(_envelope);
        Root.Children.Add(_keys);
        Root.Children.Add(_name);
        Root.Children.Add(_outline);
        _outline.BorderBrush = brushes.Accent;
        _name.Clip = _bounds;
        Canvas.SetZIndex(Root, 1);
    }

    public Canvas Root { get; } = new();

    public string? ClipId { get; private set; }

    public void Hide()
    {
        ClipId = null;
        Root.Visibility = Visibility.Collapsed;
    }

    public void Update(in ClipDraw d, IClipVisuals? visuals)
    {
        ClipId = d.Clip.Id;
        Root.Visibility = Visibility.Visible;
        Canvas.SetLeft(Root, d.Left);
        Canvas.SetTop(Root, d.Top);
        Root.Width = d.Width;
        Root.Height = d.Height;
        Root.Opacity = d.Dimmed ? 0.45 : 1;

        _body.Width = d.Width;
        _body.Height = d.Height;
        _body.Background = _brushes.ClipFill(d.Kind);
        _edge.Width = d.Width;
        _edge.Background = _brushes.ClipEdge(d.Kind);
        _outline.Visibility = d.Selected ? Visibility.Visible : Visibility.Collapsed;
        if (d.Selected)
        {
            _outline.Width = d.Width;
            _outline.Height = d.Height;
        }

        bool detailed = d.Width >= DetailMinWidth;
        UpdateName(d);
        if (d.Kind == TrackKind.Video && detailed)
        {
            UpdateFilmstrip(d, visuals);
        }
        else
        {
            _strip.Visibility = Visibility.Collapsed;
            HideThumbs(0);
        }

        if (d.Kind == TrackKind.Audio && detailed)
        {
            UpdateWaveform(d, visuals?.GetWaveform(d.Clip.MediaId));
        }
        else
        {
            _wave.Visibility = Visibility.Collapsed;
        }

        UpdateEnvelope(d);
        UpdateKeyframes(d);
    }

    /// <summary>La x local de un instante de la línea de tiempo.</summary>
    private static double LocalX(in ClipDraw d, Time time) => (time.ToSeconds() * d.PixelsPerSecond) - d.WindowLeft - d.Left;

    /// <summary>El instante de la línea de tiempo bajo una x local.</summary>
    private static Time TimeAt(in ClipDraw d, double x) => Time.FromSeconds(Math.Max(0, (x + d.Left + d.WindowLeft) / d.PixelsPerSecond));

    private void UpdateName(in ClipDraw d)
    {
        double left = Math.Max(5, d.VisibleLeft - d.Left + 5);
        double room = d.Width - left - 4;
        if (room < 18)
        {
            _name.Visibility = Visibility.Collapsed;
            return;
        }

        _name.Visibility = Visibility.Visible;
        if (!string.Equals(_name.Text, d.Name, StringComparison.Ordinal))
        {
            // Sin recorte con puntos: el bloque ya recorta el texto y así cambiar el zoom no vuelve a medirlo.
            _name.Text = d.Name;
        }

        _bounds.Rect = new Rect(0, 0, room, NameHeight + 2);
        Canvas.SetLeft(_name, left);
        Canvas.SetTop(_name, 0);
    }

    private void UpdateFilmstrip(in ClipDraw d, IClipVisuals? visuals)
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
        int thumbs = 0;
        if (visuals is not null)
        {
            int first = (int)Math.Max(0, Math.Floor(-origin / tile));
            int last = (int)Math.Ceiling((d.Width - origin) / tile);
            for (int k = first; k <= last; k++)
            {
                double x = origin + (k * tile);
                if (x >= d.Width || x + tile <= 0)
                {
                    continue;
                }

                Time sourceTime = d.Clip.ToSourceTime(TimeAt(d, x + (tile / 2)));
                if (visuals.GetThumbnail(d.Clip.MediaId, sourceTime, height) is { } image)
                {
                    Image view = Thumb(thumbs++);
                    view.Source = image;
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

    private void UpdateWaveform(in ClipDraw d, WaveformPeaks? peaks)
    {
        double top = NameHeight - 2;
        double half = (d.Height - top - 3) / 2;
        if (half < 3)
        {
            _wave.Visibility = Visibility.Collapsed;
            return;
        }

        double step = Math.Max(2, d.Width / 160);
        double center = top + half + 1;
        int count = (int)Math.Ceiling(d.Width / step) + 1;
        var upper = new Point[count];
        Clip clip = d.Clip;
        for (int i = 0; i < count; i++)
        {
            double x = Math.Min(d.Width, i * step);
            Time from = clip.ToSourceTime(TimeAt(d, x));
            Time to = clip.ToSourceTime(TimeAt(d, x + step));
            float peak = peaks is null ? ClipDecorations.PlaceholderPeak(clip.MediaId, from) : peaks.Max(from, to);
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

    private void UpdateEnvelope(in ClipDraw d)
    {
        if (!d.ShowEnvelope || d.Kind != TrackKind.Audio || d.Width < DetailMinWidth)
        {
            _envelope.Visibility = Visibility.Collapsed;
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
            double gain = ClipDecorations.GainAt(d.Clip, TimeAt(d, clamped));
            points.Add(new Point(clamped, top + ((1 - (gain / ClipDecorations.MaxGain)) * range)));
            if (clamped >= d.Width)
            {
                break;
            }
        }

        _envelope.Points = points;
        _envelope.Visibility = Visibility.Visible;
    }

    private void UpdateKeyframes(in ClipDraw d)
    {
        List<Time> times = d.Width >= DetailMinWidth ? ClipDecorations.KeyframeTimes(d.Clip) : [];
        if (times.Count == 0)
        {
            _keys.Visibility = Visibility.Collapsed;
            return;
        }

        const double r = 4;
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
