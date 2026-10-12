using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Rodaje.Core.Effects;
using Windows.Foundation;
using O = Rodaje.Core.Model.BuiltInEffects.CurvesOptions;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Rodaje.App.Views.Inspector.Filters;

/// <summary>
/// El editor de curvas: general (RGB) y por canal. Arrastrar un punto lo mueve entre sus vecinos; un clic fuera de los puntos
/// añade uno y lo arrastra; doble clic o clic derecho sobre un punto lo quita (quedan al menos dos). Cada cambio sale en
/// <see cref="CurveChanged"/> con la opción y el texto de la curva (<see cref="ToneCurve.Format"/>).
/// </summary>
internal sealed partial class ToneCurveControl : StackPanel
{
    private const double Side = 200;
    private const double HitRadius = 8;
    private const double MinGap = 0.01;

    private static readonly (string Option, string Label, Windows.UI.Color Color)[] Channels =
    [
        (O.Master, "RGB", Colors.White),
        (O.Red, "R", ColorHelper.FromArgb(255, 240, 80, 80)),
        (O.Green, "G", ColorHelper.FromArgb(255, 80, 210, 100)),
        (O.Blue, "B", ColorHelper.FromArgb(255, 90, 140, 255)),
    ];

    private readonly Canvas _canvas = new() { Width = Side, Height = Side };
    private readonly Polyline _curve = new() { StrokeThickness = 2 };
    private readonly Path _points = new() { StrokeThickness = 1.5 };
    private readonly ToggleButton[] _tabs = new ToggleButton[4];
    private readonly Dictionary<string, List<Vector2>> _curves = [];
    private int _channel;
    private int? _dragging;

    public ToneCurveControl()
    {
        Spacing = 4;
        Padding = new Thickness(12, 4, 6, 6);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        for (int i = 0; i < Channels.Length; i++)
        {
            int index = i;
            _tabs[i] = new ToggleButton { Content = Channels[i].Label, MinWidth = 40, Height = 26, Padding = new Thickness(6, 0, 6, 0), IsChecked = i == 0 };
            _tabs[i].Click += (_, _) => SelectChannel(index);
            AutomationProperties.SetName(_tabs[i], "Curva " + Channels[i].Label);
            tabs.Children.Add(_tabs[i]);
        }

        Children.Add(tabs);
        var frame = new Border
        {
            Width = Side + 2,
            Height = Side + 2,
            HorizontalAlignment = HorizontalAlignment.Left,
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["ControlBorderBrush"],
            Background = (Brush)Application.Current.Resources["BackgroundBrush"],
            Child = _canvas,
        };
        var grid = new Path { Stroke = (Brush)Application.Current.Resources["StrokeBrush"], StrokeThickness = 1, Data = GridGeometry() };
        _canvas.Children.Add(grid);
        _canvas.Children.Add(_curve);
        _canvas.Children.Add(_points);
        _points.Stroke = new SolidColorBrush(Colors.Black);
        _canvas.Background = new SolidColorBrush(Colors.Transparent);
        _canvas.Clip = new RectangleGeometry { Rect = new Rect(0, 0, Side, Side) };
        _canvas.PointerPressed += OnPressed;
        _canvas.PointerMoved += OnMoved;
        _canvas.PointerReleased += OnReleased;
        _canvas.PointerCaptureLost += OnReleased;
        _canvas.DoubleTapped += OnDoubleTapped;
        AutomationProperties.SetName(_canvas, "Curva");
        ToolTipService.SetToolTip(_canvas, "Arrastra un punto; clic en otro lugar añade uno; doble clic o clic derecho lo quita.");
        Children.Add(frame);
    }

    /// <summary>Cambió la curva de una opción: (opción, texto, si terminó el arrastre).</summary>
    public event EventHandler<(string Option, string Text, bool Final)>? CurveChanged;

    /// <summary>Las curvas del efecto (texto de cada opción); no toca la que se está arrastrando.</summary>
    public void Show(IReadOnlyDictionary<string, string> options)
    {
        if (_dragging is not null)
        {
            return;
        }

        foreach ((string option, _, _) in Channels)
        {
            ToneCurve curve = ToneCurve.Parse(options.GetValueOrDefault(option));
            _curves[option] = curve.Points.Count >= 2 ? [.. curve.Points] : [new(0, 0), new(1, 1)];
        }

        Draw();
    }

    private List<Vector2> Current => _curves.TryGetValue(Channels[_channel].Option, out List<Vector2>? p) ? p : _curves[Channels[_channel].Option] = [new(0, 0), new(1, 1)];

    private void SelectChannel(int index)
    {
        _channel = index;
        for (int i = 0; i < _tabs.Length; i++)
        {
            _tabs[i].IsChecked = i == index;
        }

        Draw();
    }

    private static Vector2 ToCurve(Point p) => new((float)Math.Clamp(p.X / Side, 0, 1), (float)Math.Clamp(1 - (p.Y / Side), 0, 1));

    private static Point ToCanvas(double x, double y) => new(x * Side, (1 - y) * Side);

    private int? HitPoint(Point p)
    {
        List<Vector2> points = Current;
        for (int i = 0; i < points.Count; i++)
        {
            Point c = ToCanvas(points[i].X, points[i].Y);
            if (Math.Abs(c.X - p.X) <= HitRadius && Math.Abs(c.Y - p.Y) <= HitRadius)
            {
                return i;
            }
        }

        return null;
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(_canvas);
        List<Vector2> points = Current;
        int? hit = HitPoint(point.Position);
        if (point.Properties.IsRightButtonPressed)
        {
            if (hit is { } remove && points.Count > 2)
            {
                points.RemoveAt(remove);
                Publish(final: true);
            }

            e.Handled = true;
            return;
        }

        if (hit is null)
        {
            Vector2 added = ToCurve(point.Position);
            int index = points.FindIndex(p => p.X > added.X);
            index = index < 0 ? points.Count : index;
            if (points.Count >= ToneCurve.MaxPoints || (index > 0 && added.X - points[index - 1].X < MinGap)
                || (index < points.Count && points[index].X - added.X < MinGap))
            {
                return;
            }

            points.Insert(index, added);
            hit = index;
        }

        _dragging = hit;
        _canvas.CapturePointer(e.Pointer);
        Publish(final: false);
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging is not { } i)
        {
            return;
        }

        List<Vector2> points = Current;
        Vector2 p = ToCurve(e.GetCurrentPoint(_canvas).Position);
        float lo = i == 0 ? 0 : points[i - 1].X + (float)MinGap;
        float hi = i == points.Count - 1 ? 1 : points[i + 1].X - (float)MinGap;
        points[i] = new Vector2(Math.Clamp(p.X, lo, Math.Max(lo, hi)), p.Y);
        Publish(final: false);
        e.Handled = true;
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging is null)
        {
            return;
        }

        _dragging = null;
        _canvas.ReleasePointerCaptures();
        Publish(final: true);
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        List<Vector2> points = Current;
        if (HitPoint(e.GetPosition(_canvas)) is { } i && points.Count > 2)
        {
            points.RemoveAt(i);
            Publish(final: true);
        }
    }

    private void Publish(bool final)
    {
        Draw();
        string option = Channels[_channel].Option;
        CurveChanged?.Invoke(this, (option, ToneCurve.From(Current).Format(), final));
    }

    private void Draw()
    {
        if (_curves.Count == 0)
        {
            return;
        }

        List<Vector2> points = Current;
        ToneCurve curve = ToneCurve.From(points);
        var line = new PointCollection();
        for (int s = 0; s <= 64; s++)
        {
            double x = s / 64.0;
            line.Add(ToCanvas(x, curve.Evaluate(x)));
        }

        var brush = new SolidColorBrush(Channels[_channel].Color);
        _curve.Points = line;
        _curve.Stroke = brush;
        var dots = new GeometryGroup();
        foreach (Vector2 p in points)
        {
            dots.Children.Add(new EllipseGeometry { Center = ToCanvas(p.X, p.Y), RadiusX = 4, RadiusY = 4 });
        }

        _points.Data = dots;
        _points.Fill = brush;
    }

    private static GeometryGroup GridGeometry()
    {
        var group = new GeometryGroup();
        for (int i = 1; i < 4; i++)
        {
            double at = i * Side / 4;
            group.Children.Add(new LineGeometry { StartPoint = new Point(at, 0), EndPoint = new Point(at, Side) });
            group.Children.Add(new LineGeometry { StartPoint = new Point(0, at), EndPoint = new Point(Side, at) });
        }

        group.Children.Add(new LineGeometry { StartPoint = new Point(0, Side), EndPoint = new Point(Side, 0) });
        return group;
    }
}
