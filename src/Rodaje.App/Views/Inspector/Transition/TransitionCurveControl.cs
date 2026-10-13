using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Rodaje.App.Services.Format;
using Rodaje.App.Theme;
using Rodaje.Core.Model;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Rodaje.App.Views.Inspector.Transition;

/// <summary>
/// La curva del avance de una transición: tiempo en horizontal y avance en vertical, con la recta lineal de referencia, el
/// punto del cuadro que muestra la miniatura y, en Bézier, los dos manejadores. Arrastrar un manejador vuelve la curva
/// Bézier; cada arrastre es un paso del historial (lo funde el comando) y Esc no hace falta: se deshace con Ctrl+Z.
/// </summary>
internal sealed partial class TransitionCurveControl : Grid
{
    private const double Pad = 12;
    private const double Low = -0.35;
    private const double High = 1.35;
    private const double HandleRadius = 5;

    private readonly Canvas _canvas = new();
    private readonly Path _grid = new() { StrokeThickness = 1 };
    private readonly Line _linear = new() { StrokeThickness = 1, StrokeDashArray = [3, 3] };
    private readonly Polyline _curve = new() { StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
    private readonly Path _arms = new() { StrokeThickness = 1 };
    private readonly Ellipse _first = Handle();
    private readonly Ellipse _second = Handle();
    private readonly Ellipse _dot = new() { Width = 7, Height = 7, IsHitTestVisible = false };
    private readonly TextBlock _hint = new() { FontSize = ThemeResources.Size("MicroFontSize"), IsHitTestVisible = false, Margin = new Thickness(6, 2, 0, 0) };
    private Interpolation? _kind;
    private BezierEase _ease = BezierEase.Smooth;
    private int _dragging;
    private double _progress = -1;

    public TransitionCurveControl()
    {
        Height = 132;
        Background = Brush("BackgroundBrush");
        BorderBrush = Brush("ControlBorderBrush");
        BorderThickness = new Thickness(1);
        CornerRadius = (CornerRadius)Application.Current.Resources["ControlCornerRadius"];
        _grid.Stroke = Brush("StrokeBrush");
        _linear.Stroke = Brush("TextMutedBrush");
        _curve.Stroke = Brush("AccentBrush");
        _arms.Stroke = Brush("TextMutedBrush");
        _dot.Fill = Brush("TextBrush");
        _hint.Foreground = Brush("TextMutedBrush");
        _canvas.Children.Add(_grid);
        _canvas.Children.Add(_linear);
        _canvas.Children.Add(_arms);
        _canvas.Children.Add(_curve);
        _canvas.Children.Add(_dot);
        _canvas.Children.Add(_first);
        _canvas.Children.Add(_second);
        _canvas.Clip = new RectangleGeometry();
        Children.Add(_canvas);
        Children.Add(_hint);
        SizeChanged += (_, _) => Draw();
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += (_, e) => Release(e.Pointer);
        PointerCaptureLost += (_, _) => _dragging = 0;
        AutomationProperties.SetName(this, "Curva del avance de la transición");
        ToolTipService.SetToolTip(this, "Tiempo (horizontal) contra avance (vertical). Arrastra los manejadores para una curva Bézier propia.");
    }

    /// <summary>Se arrastra un manejador: la curva nueva (siempre Bézier).</summary>
    public event EventHandler<BezierEase>? Changing;

    /// <summary>Terminó el arrastre.</summary>
    public event EventHandler? Committed;

    public void Show(Interpolation? kind, BezierEase? ease)
    {
        if (_dragging != 0)
        {
            return;
        }

        _kind = kind is Interpolation.Linear ? null : kind;
        _ease = ease ?? BezierEase.Smooth;
        Draw();
    }

    /// <summary>El avance del tiempo que muestra la miniatura (de 0 a 1), o negativo para esconder el punto.</summary>
    public void ShowProgress(double linear)
    {
        _progress = linear;
        PlaceDot();
    }

    private static Ellipse Handle() => new()
    {
        Width = HandleRadius * 2,
        Height = HandleRadius * 2,
        StrokeThickness = 1.5,
        Fill = Brush("BackgroundBrush"),
        Stroke = Brush("AccentBrush"),
    };

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    private double PlotWidth => Math.Max(1, ActualWidth - (2 * Pad));

    private double PlotHeight => Math.Max(1, ActualHeight - (2 * Pad));

    private Point ToScreen(double t, double y) => new(Pad + (t * PlotWidth), Pad + ((High - y) / (High - Low) * PlotHeight));

    private (double T, double Y) FromScreen(Point p) =>
        (Math.Clamp((p.X - Pad) / PlotWidth, 0, 1), Math.Clamp(High - ((p.Y - Pad) / PlotHeight * (High - Low)), BezierEase.MinY, BezierEase.MaxY));

    private void Draw()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        ((RectangleGeometry)_canvas.Clip).Rect = new Rect(0, 0, ActualWidth, ActualHeight);
        var grid = new GeometryGroup();
        for (int i = 0; i <= 4; i++)
        {
            Point a = ToScreen(i / 4.0, Low);
            Point b = ToScreen(i / 4.0, High);
            grid.Children.Add(new LineGeometry { StartPoint = a, EndPoint = b });
        }

        foreach (double y in new[] { 0.0, 1.0 })
        {
            grid.Children.Add(new LineGeometry { StartPoint = ToScreen(0, y), EndPoint = ToScreen(1, y) });
        }

        _grid.Data = grid;
        Point zero = ToScreen(0, 0);
        Point one = ToScreen(1, 1);
        (_linear.X1, _linear.Y1, _linear.X2, _linear.Y2) = (zero.X, zero.Y, one.X, one.Y);

        var points = new PointCollection();
        const int Steps = 96;
        for (int i = 0; i <= Steps; i++)
        {
            double t = i / (double)Steps;
            points.Add(ToScreen(t, TransitionInstance.EaseProgress(_kind, _ease, t)));
        }

        _curve.Points = points;
        bool bezier = _kind == Interpolation.Bezier;
        _first.Visibility = _second.Visibility = _arms.Visibility = bezier ? Visibility.Visible : Visibility.Collapsed;
        if (bezier)
        {
            Point p1 = ToScreen(_ease.X1, _ease.Y1);
            Point p2 = ToScreen(_ease.X2, _ease.Y2);
            Place(_first, p1);
            Place(_second, p2);
            var arms = new GeometryGroup();
            arms.Children.Add(new LineGeometry { StartPoint = zero, EndPoint = p1 });
            arms.Children.Add(new LineGeometry { StartPoint = one, EndPoint = p2 });
            _arms.Data = arms;
        }

        _hint.Text = bezier
            ? string.Format(SpanishNumbers.Format, "Bézier {0:0.00}; {1:0.00}; {2:0.00}; {3:0.00}", _ease.X1, _ease.Y1, _ease.X2, _ease.Y2)
            : "";
        PlaceDot();
    }

    private void PlaceDot()
    {
        if (_progress < 0 || ActualWidth <= 0)
        {
            _dot.Visibility = Visibility.Collapsed;
            return;
        }

        _dot.Visibility = Visibility.Visible;
        Point p = ToScreen(_progress, TransitionInstance.EaseProgress(_kind, _ease, _progress));
        Canvas.SetLeft(_dot, p.X - 3.5);
        Canvas.SetTop(_dot, p.Y - 3.5);
    }

    private static void Place(Ellipse handle, Point p)
    {
        Canvas.SetLeft(handle, p.X - HandleRadius);
        Canvas.SetTop(handle, p.Y - HandleRadius);
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_kind != Interpolation.Bezier)
        {
            return;
        }

        Point p = e.GetCurrentPoint(this).Position;
        double d1 = Distance(p, ToScreen(_ease.X1, _ease.Y1));
        double d2 = Distance(p, ToScreen(_ease.X2, _ease.Y2));
        double nearest = Math.Min(d1, d2);
        if (nearest > HandleRadius + 6)
        {
            return;
        }

        _dragging = d1 <= d2 ? 1 : 2;
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging == 0)
        {
            return;
        }

        (double t, double y) = FromScreen(e.GetCurrentPoint(this).Position);
        _ease = _dragging == 1 ? _ease with { X1 = Math.Round(t, 3), Y1 = Math.Round(y, 3) } : _ease with { X2 = Math.Round(t, 3), Y2 = Math.Round(y, 3) };
        Draw();
        Changing?.Invoke(this, _ease);
        e.Handled = true;
    }

    private void Release(Pointer? pointer)
    {
        if (_dragging == 0)
        {
            return;
        }

        _dragging = 0;
        if (pointer is not null)
        {
            ReleasePointerCapture(pointer);
        }

        Committed?.Invoke(this, EventArgs.Empty);
    }

    private static double Distance(Point a, Point b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
}
