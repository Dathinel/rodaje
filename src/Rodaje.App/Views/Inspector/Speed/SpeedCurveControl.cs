using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Rodaje.App.Services.Inspector;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Rodaje.App.Views.Inspector.Speed;

/// <summary>
/// El editor de puntos de la curva de velocidad: la curva sobre la duración del clip (altura logarítmica de 0,1× a 10×),
/// la línea del cursor y los puntos. Arrastrar un punto cambia su velocidad (en vertical) y su posición (en horizontal);
/// doble clic añade o quita. Las ediciones van por <see cref="SpeedEditor"/>; aquí solo hay dibujo y puntero.
/// </summary>
internal sealed partial class SpeedCurveControl : Grid
{
    private const double Pad = 7;
    private const double HitRadius = 9;
    private const double PointRadius = 3.5;

    // Cerca de 1× (en píxeles) el punto se queda en 1×.
    private const double OneDetent = 4;

    private static readonly double[] GuideRates = [0.1, 0.5, 1, 2, 10];

    private readonly Canvas _canvas = new();
    private readonly Rectangle _before = new();
    private readonly Rectangle _after = new();
    private readonly Path _guides = new() { StrokeThickness = 1 };
    private readonly Rectangle _oneLine = new() { Height = 1 };
    private readonly Polyline _curve = new() { StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
    private readonly Path _points = new() { StrokeThickness = 1.5 };
    private readonly Ellipse _active = new() { Width = 2 * (PointRadius + 1.5), Height = 2 * (PointRadius + 1.5), Visibility = Visibility.Collapsed };
    private readonly Rectangle _playhead = new() { Width = 1 };
    private readonly TextBlock _readout = new() { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 8, 0), IsHitTestVisible = false };
    private readonly TextBlock[] _labels = new TextBlock[3];

    private SpeedEditor? _editor;
    private Clip? _clip;
    private CurveState? _state;
    private double? _playheadAt;
    private Point _pressAt;
    private Point _pointAtPress;
    private int? _hover;
    private uint? _pointerId;

    public SpeedCurveControl()
    {
        Height = 128;
        Background = Brush("BackgroundBrush");
        BorderBrush = Brush("ControlBorderBrush");
        BorderThickness = new Thickness(1);
        CornerRadius = (CornerRadius)Application.Current.Resources["ControlCornerRadius"];
        _before.Fill = _after.Fill = Brush("PanelBrush");
        _before.Opacity = _after.Opacity = 0.75;
        _guides.Stroke = Brush("StrokeBrush");
        _oneLine.Fill = Brush("ControlBorderBrush");
        _curve.Stroke = Brush("VideoClipEdgeBrush");
        _points.Fill = Brush("TextBrush");
        _points.Stroke = Brush("BackgroundBrush");
        _active.Fill = Brush("AccentBrush");
        _active.Stroke = Brush("BackgroundBrush");
        _active.StrokeThickness = 1.5;
        _playhead.Fill = Brush("AccentBrush");
        _readout.Foreground = Brush("TextBrush");
        string[] texts = ["10×", "1×", "0,1×"];
        for (int i = 0; i < _labels.Length; i++)
        {
            _labels[i] = new TextBlock { Text = texts[i], FontSize = 10, Foreground = Brush("TextMutedBrush"), IsHitTestVisible = false };
            _canvas.Children.Add(_labels[i]);
        }

        _canvas.Children.Insert(0, _guides);
        _canvas.Children.Insert(1, _oneLine);
        _canvas.Children.Add(_before);
        _canvas.Children.Add(_after);
        _canvas.Children.Add(_curve);
        _canvas.Children.Add(_playhead);
        _canvas.Children.Add(_points);
        _canvas.Children.Add(_active);
        Children.Add(_canvas);
        Children.Add(_readout);
        // El recorte de la Grid no alcanza a lo que se dibuja en el Canvas.
        _canvas.Clip = new RectangleGeometry();

        SizeChanged += (_, _) => Draw();
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += (_, e) => EndDrag(e.Pointer);
        PointerCaptureLost += (_, _) => EndDrag(null);
        PointerExited += (_, _) =>
        {
            if (_pointerId is null)
            {
                _hover = null;
                ShowReadout(null);
                Draw();
            }
        };
        DoubleTapped += OnDoubleTapped;
        AutomationProperties.SetName(this, "Curva de velocidad");
        ToolTipService.SetToolTip(this, "Arrastra un punto: arriba y abajo cambia la velocidad, a los lados su posición. Doble clic añade o quita un punto.");
    }

    public void Attach(SpeedEditor editor) => _editor = editor;

    /// <summary>Lo que se dibuja: el clip, su curva y dónde está el cursor (en diezmilésimas de la curva, o nulo).</summary>
    public void Show(Clip clip, CurveState state, double? playheadAt)
    {
        _clip = clip;
        _state = state;
        _playheadAt = playheadAt;
        Draw();
    }

    /// <summary>Solo se movió el cursor.</summary>
    public void ShowPlayhead(double? playheadAt)
    {
        if (_playheadAt != playheadAt)
        {
            _playheadAt = playheadAt;
            DrawPlayhead();
        }
    }

    private double PlotWidth => Math.Max(1, ActualWidth - (2 * Pad) - 2);

    private double PlotHeight => Math.Max(1, Height - (2 * Pad) - 2);

    private IReadOnlyList<SpeedPoint>? Points => _state?.Points;

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    private double X(double at) => Pad + (at / SpeedCurve.AtScale * PlotWidth);

    private double Y(double level) => Pad + ((1 - level) * PlotHeight);

    private void Draw()
    {
        double width = ActualWidth - 2;
        if (width <= 0)
        {
            return;
        }

        ((RectangleGeometry)_canvas.Clip).Rect = new Rect(0, 0, width, Height - 2);
        var guides = new GeometryGroup();
        foreach (double rate in GuideRates)
        {
            double y = Math.Round(Y(SpeedCurveMath.Level(rate))) + 0.5;
            if (rate != 1)
            {
                guides.Children.Add(new LineGeometry { StartPoint = new Point(Pad, y), EndPoint = new Point(Pad + PlotWidth, y) });
            }
        }

        _guides.Data = guides;
        _oneLine.Width = PlotWidth;
        Canvas.SetLeft(_oneLine, Pad);
        Canvas.SetTop(_oneLine, Math.Round(Y(0.5)));
        Canvas.SetLeft(_labels[0], Pad + 2);
        Canvas.SetTop(_labels[0], Y(1) + 1);
        Canvas.SetLeft(_labels[1], Pad + 2);
        Canvas.SetTop(_labels[1], Y(0.5) - 14);
        Canvas.SetLeft(_labels[2], Pad + 2);
        Canvas.SetTop(_labels[2], Y(0) - 14);

        IReadOnlyList<SpeedPoint> points = Points ?? SpeedCurveMath.Flat(1);
        bool editable = Points is not null && _state is { CanEdit: true };
        _curve.Opacity = Points is null ? 0.4 : 1;
        var line = new PointCollection();
        foreach ((double x, double level) in SpeedCurveMath.Shape(points, 0, SpeedCurve.AtScale, 12))
        {
            line.Add(new Point(Pad + (x * PlotWidth), Y(level)));
        }

        _curve.Points = line;
        DrawWindow();

        var dots = new GeometryGroup();
        if (editable)
        {
            foreach (SpeedPoint p in points)
            {
                dots.Children.Add(new EllipseGeometry { Center = new Point(X(p.At), Y(SpeedCurveMath.Level(p.Hundredths / 100.0))), RadiusX = PointRadius, RadiusY = PointRadius });
            }
        }

        _points.Data = dots;
        int? active = _editor?.PointDragState?.Index ?? _hover;
        if (editable && active is { } index && index < points.Count)
        {
            SpeedPoint p = points[index];
            Canvas.SetLeft(_active, X(p.At) - (_active.Width / 2));
            Canvas.SetTop(_active, Y(SpeedCurveMath.Level(p.Hundredths / 100.0)) - (_active.Height / 2));
            _active.Visibility = Visibility.Visible;
        }
        else
        {
            _active.Visibility = Visibility.Collapsed;
        }

        DrawPlayhead();
    }

    // Lo que queda fuera del clip (un trozo de un clip dividido) se vela.
    private void DrawWindow()
    {
        (double from, double to) = _state is { } s ? (s.From, s.To) : (0, SpeedCurve.AtScale);
        double left = X(from);
        double right = X(to);
        _before.Width = Math.Max(0, left - Pad);
        _before.Height = PlotHeight + (2 * Pad);
        Canvas.SetLeft(_before, Pad);
        _after.Width = Math.Max(0, Pad + PlotWidth - right);
        _after.Height = PlotHeight + (2 * Pad);
        Canvas.SetLeft(_after, right);
    }

    private void DrawPlayhead()
    {
        if (_playheadAt is not { } at || Points is null)
        {
            _playhead.Visibility = Visibility.Collapsed;
            return;
        }

        _playhead.Height = Height - 2;
        Canvas.SetLeft(_playhead, Math.Round(X(at)));
        _playhead.Visibility = Visibility.Visible;
    }

    private void ShowReadout(double? at, int? hundredths = null)
    {
        if (at is not { } position || Points is not { } points || _clip is not { } clip)
        {
            _readout.Text = "";
            return;
        }

        double rate = hundredths is { } h ? h / 100.0 : SpeedCurveMath.RateAt(points, position);
        Time time = SpeedCurveMath.ClipTimeOf(clip, position);
        _readout.Text = $"{SpeedMath.FormatDuration(time)} · {SpeedMath.FormatRate(rate)}";
    }

    private (int At, int Hundredths) Under(Point grabbed)
    {
        int at = (int)Math.Round((grabbed.X - Pad) / PlotWidth * SpeedCurve.AtScale);
        double y = Math.Abs(grabbed.Y - Y(0.5)) <= OneDetent ? Y(0.5) : grabbed.Y;
        return (at, SpeedCurveMath.HundredthsAt(1 - ((y - Pad) / PlotHeight)));
    }

    private int? HitPoint(Point p) =>
        Points is { } points && _state is { CanEdit: true }
            ? SpeedCurveMath.Hit(points, p.X - Pad, p.Y - Pad, PlotWidth, PlotHeight, HitRadius)
            : null;

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Point p = e.GetCurrentPoint(_canvas).Position;
        if (_editor is not { } editor || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || HitPoint(p) is not { } index
            || Points is not { } points || !editor.BeginPointDrag(index))
        {
            return;
        }

        _pressAt = p;
        _pointAtPress = new Point(X(points[index].At), Y(SpeedCurveMath.Level(points[index].Hundredths / 100.0)));
        _pointerId = e.Pointer.PointerId;
        CapturePointer(e.Pointer);
        e.Handled = true;
        ShowReadout(points[index].At, points[index].Hundredths);
        Draw();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        Point p = e.GetCurrentPoint(_canvas).Position;
        if (_pointerId is not null && _editor is { PointDragState: { } drag } editor)
        {
            var grabbed = new Point(_pointAtPress.X + (p.X - _pressAt.X), _pointAtPress.Y + (p.Y - _pressAt.Y));
            (int at, int hundredths) = Under(grabbed);
            editor.DragPoint(at, hundredths);
            SpeedPoint moved = SpeedCurveMath.Move(drag.StartPoints, drag.Index, at, hundredths)[drag.Index];
            ShowReadout(moved.At, moved.Hundredths);
            e.Handled = true;
            return;
        }

        int? hover = HitPoint(p);
        if (hover != _hover)
        {
            _hover = hover;
            Draw();
        }

        if (hover is { } index && Points is { } points)
        {
            ShowReadout(points[index].At, points[index].Hundredths);
        }
        else
        {
            ShowReadout(Math.Clamp((p.X - Pad) / PlotWidth * SpeedCurve.AtScale, 0, SpeedCurve.AtScale));
        }
    }

    private void EndDrag(Pointer? pointer)
    {
        if (_pointerId is null)
        {
            return;
        }

        _pointerId = null;
        if (pointer is not null)
        {
            ReleasePointerCapture(pointer);
        }

        _editor?.EndPointDrag();
        Draw();
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_editor is not { } editor || Points is null || _state is not { CanEdit: true })
        {
            return;
        }

        Point p = e.GetPosition(_canvas);
        int at = (int)Math.Round((p.X - Pad) / PlotWidth * SpeedCurve.AtScale);
        editor.AddOrRemovePoint(at, HitPoint(p));
        e.Handled = true;
    }
}
