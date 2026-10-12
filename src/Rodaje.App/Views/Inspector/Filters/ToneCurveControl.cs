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
/// El editor de curvas: general (RGB) y por canal, y las de ajuste (tono contra tono, tono contra saturación y luma contra
/// saturación, con la línea neutra al medio y la escala de tonos o de luma debajo). Arrastrar un punto lo mueve entre sus
/// vecinos; un clic fuera de los puntos añade uno y lo arrastra; doble clic o clic derecho sobre un punto lo quita (en las de
/// tono quedan al menos dos; en las de ajuste, ninguno es la curva neutra). Cada cambio sale en <see cref="CurveChanged"/>
/// con la opción y el texto de la curva.
/// </summary>
internal sealed partial class ToneCurveControl : StackPanel
{
    private const double Side = 200;
    private const double HitRadius = 8;
    private const double MinGap = 0.01;

    private static readonly (string Option, string Label, Windows.UI.Color Color, string Tip)[] Channels =
    [
        (O.Master, "RGB", Colors.White, "Curva general"),
        (O.Red, "R", ColorHelper.FromArgb(255, 240, 80, 80), "Curva del rojo"),
        (O.Green, "G", ColorHelper.FromArgb(255, 80, 210, 100), "Curva del verde"),
        (O.Blue, "B", ColorHelper.FromArgb(255, 90, 140, 255), "Curva del azul"),
        (O.HueHue, "Tono › tono", ColorHelper.FromArgb(255, 242, 209, 107), "Tono contra tono: gira el tono de cada color (arriba o abajo del medio)"),
        (O.HueSaturation, "Tono › sat.", ColorHelper.FromArgb(255, 242, 209, 107), "Tono contra saturación: satura o apaga cada color"),
        (O.LumaSaturation, "Luma › sat.", ColorHelper.FromArgb(255, 242, 209, 107), "Luma contra saturación: satura o apaga según el brillo (por ejemplo, las sombras)"),
    ];

    /// <summary>Las curvas de ajuste empiezan en la cuarta pestaña.</summary>
    private const int FirstAdjust = 4;

    private readonly Rectangle _scale = new() { Width = Side, Height = 8 };
    private readonly Line _guide = new();

    private readonly Canvas _canvas = new() { Width = Side, Height = Side };
    private readonly Polyline _curve = new() { StrokeThickness = 2 };
    private readonly Path _points = new() { StrokeThickness = 1.5 };
    private readonly ToggleButton[] _tabs = new ToggleButton[7];
    private readonly Dictionary<string, List<Vector2>> _curves = [];
    private int _channel;
    private int? _dragging;

    public ToneCurveControl()
    {
        Spacing = 4;
        Padding = new Thickness(12, 4, 6, 6);
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var adjust = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        for (int i = 0; i < Channels.Length; i++)
        {
            int index = i;
            _tabs[i] = new ToggleButton { Content = Channels[i].Label, MinWidth = 40, Height = 26, Padding = new Thickness(6, 0, 6, 0), IsChecked = i == 0 };
            _tabs[i].Click += (_, _) => SelectChannel(index);
            AutomationProperties.SetName(_tabs[i], Channels[i].Tip);
            ToolTipService.SetToolTip(_tabs[i], Channels[i].Tip);
            (i < FirstAdjust ? tabs : adjust).Children.Add(_tabs[i]);
        }

        Children.Add(tabs);
        Children.Add(adjust);
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
        _guide.Stroke = (Brush)Application.Current.Resources["ControlBorderBrush"];
        _guide.StrokeThickness = 1;
        _canvas.Children.Add(_guide);
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
        Children.Add(_scale);
    }

    private bool IsAdjust => _channel >= FirstAdjust;

    private bool IsPeriodic => Channels[_channel].Option is O.HueHue or O.HueSaturation;

    private int MinPoints => IsAdjust ? 0 : 2;

    /// <summary>Cambió la curva de una opción: (opción, texto, si terminó el arrastre).</summary>
    public event EventHandler<(string Option, string Text, bool Final)>? CurveChanged;

    /// <summary>Las curvas del efecto (texto de cada opción); no toca la que se está arrastrando.</summary>
    public void Show(IReadOnlyDictionary<string, string> options)
    {
        if (_dragging is not null)
        {
            return;
        }

        foreach ((string option, _, _, _) in Channels)
        {
            if (option is O.HueHue or O.HueSaturation or O.LumaSaturation)
            {
                _curves[option] = [.. HueCurve.Parse(options.GetValueOrDefault(option), option != O.LumaSaturation).Points];
                continue;
            }

            ToneCurve curve = ToneCurve.Parse(options.GetValueOrDefault(option));
            _curves[option] = curve.Points.Count >= 2 ? [.. curve.Points] : [new(0, 0), new(1, 1)];
        }

        Draw();
    }

    private List<Vector2> Current => _curves.TryGetValue(Channels[_channel].Option, out List<Vector2>? p) ? p
        : _curves[Channels[_channel].Option] = IsAdjust ? [] : [new(0, 0), new(1, 1)];

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
            if (hit is { } remove && points.Count > MinPoints)
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
        if (HitPoint(e.GetPosition(_canvas)) is { } i && points.Count > MinPoints)
        {
            points.RemoveAt(i);
            Publish(final: true);
        }
    }

    private void Publish(bool final)
    {
        Draw();
        string option = Channels[_channel].Option;
        string text = IsAdjust ? HueCurve.From(Current, IsPeriodic).Format() : ToneCurve.From(Current).Format();
        CurveChanged?.Invoke(this, (option, text, final));
    }

    private void Draw()
    {
        if (_curves.Count == 0)
        {
            return;
        }

        List<Vector2> points = Current;
        Func<double, double> evaluate = IsAdjust ? HueCurve.From(points, IsPeriodic).Evaluate : ToneCurve.From(points).Evaluate;
        var line = new PointCollection();
        for (int s = 0; s <= 64; s++)
        {
            double x = s / 64.0;
            line.Add(ToCanvas(x, evaluate(x)));
        }

        _scale.Visibility = IsAdjust ? Visibility.Visible : Visibility.Collapsed;
        // La guía es la curva que no cambia nada: la diagonal en las de tono, la línea del medio en las de ajuste.
        (_guide.X1, _guide.Y1, _guide.X2, _guide.Y2) = IsAdjust ? (0, Side / 2, Side, Side / 2) : (0, Side, Side, 0);
        if (IsAdjust)
        {
            var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            if (IsPeriodic)
            {
                for (int h = 0; h <= 6; h++)
                {
                    gradient.GradientStops.Add(new GradientStop { Offset = h / 6.0, Color = HueColor(h * 60) });
                }
            }
            else
            {
                gradient.GradientStops.Add(new GradientStop { Offset = 0, Color = Colors.Black });
                gradient.GradientStops.Add(new GradientStop { Offset = 1, Color = Colors.White });
            }

            _scale.Fill = gradient;
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

    private static Windows.UI.Color HueColor(double degrees)
    {
        double h = degrees % 360 / 60;
        byte x = (byte)Math.Round(255 * (1 - Math.Abs((h % 2) - 1)));
        return (int)h switch
        {
            0 => ColorHelper.FromArgb(255, 255, x, 0),
            1 => ColorHelper.FromArgb(255, x, 255, 0),
            2 => ColorHelper.FromArgb(255, 0, 255, x),
            3 => ColorHelper.FromArgb(255, 0, x, 255),
            4 => ColorHelper.FromArgb(255, x, 0, 255),
            _ => ColorHelper.FromArgb(255, 255, 0, x),
        };
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

        return group;
    }
}
