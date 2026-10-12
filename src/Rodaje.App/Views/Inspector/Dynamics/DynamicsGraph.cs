using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Rodaje.App.Services.Audio;
using Rodaje.App.Services.Format;
using Rodaje.Audio.Processing;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Rodaje.App.Views.Inspector.Dynamics;

/// <summary>
/// La curva de transferencia del compresor (entrada contra salida, de −60 a 0 dB) con el punto de lo que suena, y tres
/// medidores: entrada, reducción de ganancia y salida, con su pico retenido.
/// </summary>
internal sealed partial class DynamicsGraph : StackPanel
{
    private const double Side = 168;
    private const double BarWidth = 14;
    private const double BarGap = 10;

    private readonly Canvas _canvas = new() { Width = Side, Height = Side };
    private readonly Polyline _curve = new() { StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
    private readonly Line _threshold = new() { StrokeThickness = 1, StrokeDashArray = [3, 3] };
    private readonly Ellipse _dot = new() { Width = 8, Height = 8, Visibility = Visibility.Collapsed };
    private readonly Meter _input = new("Entrada", fromTop: false);
    private readonly Meter _reduction = new("Reducción", fromTop: true);
    private readonly Meter _output = new("Salida", fromTop: false);
    private readonly TextBlock _label = new() { FontSize = 11 };
    private long _last;

    public DynamicsGraph()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 14;
        Padding = new Thickness(12, 6, 6, 6);
        Brush stroke = Resource("StrokeBrush");
        Brush muted = Resource("TextMutedBrush");
        Brush accent = Resource("AccentBrush");
        var grid = new Path { Stroke = stroke, StrokeThickness = 1, Data = GridGeometry() };
        var unity = new Line { X1 = 0, Y1 = Side, X2 = Side, Y2 = 0, Stroke = stroke, StrokeThickness = 1, StrokeDashArray = [2, 4] };
        _curve.Stroke = accent;
        _threshold.Stroke = muted;
        _dot.Fill = Resource("TextBrush");
        _canvas.Children.Add(grid);
        _canvas.Children.Add(unity);
        _canvas.Children.Add(_threshold);
        _canvas.Children.Add(_curve);
        _canvas.Children.Add(_dot);
        _canvas.Clip = new RectangleGeometry { Rect = new Rect(0, 0, Side, Side) };
        AutomationProperties.SetName(_canvas, "Curva de transferencia");
        ToolTipService.SetToolTip(_canvas, "Entrada (horizontal) contra salida (vertical), de −60 a 0 dB. El punto es lo que suena ahora.");

        _label.Foreground = muted;
        var left = new StackPanel { Spacing = 4 };
        left.Children.Add(new Border
        {
            Width = Side + 2,
            Height = Side + 2,
            BorderThickness = new Thickness(1),
            BorderBrush = Resource("ControlBorderBrush"),
            Background = Resource("BackgroundBrush"),
            Child = _canvas,
        });
        left.Children.Add(_label);
        Children.Add(left);

        var meters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = BarGap };
        meters.Children.Add(_input.Root);
        meters.Children.Add(_reduction.Root);
        meters.Children.Add(_output.Root);
        Children.Add(meters);
    }

    /// <summary>Dibuja la curva con los valores que se ven en el cursor.</summary>
    public void SetCurve(double threshold, double ratio, double knee, double makeup)
    {
        var points = new PointCollection();
        foreach ((double input, double output) in TransferCurve.Points(threshold, ratio, knee, makeup))
        {
            points.Add(new Point(X(input), Y(output)));
        }

        _curve.Points = points;
        _threshold.X1 = _threshold.X2 = X(threshold);
        _threshold.Y1 = 0;
        _threshold.Y2 = Side;
        string ratioText = ratio >= CompressorSettings.InfiniteRatio ? "∞" : ratio.ToString("0.#", SpanishNumbers.Format);
        _label.Text = string.Create(SpanishNumbers.Format, $"Umbral {threshold:0.#} dB · {ratioText}:1 · rodilla {knee:0.#} dB");
    }

    /// <summary>Lo que suena (nulo en pausa): mueve el punto y los medidores con su caída.</summary>
    public void SetReading(DynamicsReading? reading, (double Threshold, double Ratio, double Knee, double Makeup) curve)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double seconds = _last == 0 ? 0 : System.Diagnostics.Stopwatch.GetElapsedTime(_last, now).TotalSeconds;
        _last = now;
        double input = reading is { } r ? Db(r.InputPeak) : -120;
        double output = reading is { } o ? Db(o.OutputPeak) : -120;
        double reduction = reading is { } g ? g.ReductionDb + g.LimiterReductionDb : 0;
        _input.Update(input, seconds);
        _output.Update(output, seconds);
        _reduction.Update(reduction, seconds);
        if (reading is null || input < -60)
        {
            _dot.Visibility = Visibility.Collapsed;
            return;
        }

        // El punto va sobre la curva estática: la reducción real (con ataque y relajación) la cuentan los medidores.
        double shown = TransferCurve.Output(input, curve.Threshold, curve.Ratio, curve.Knee, curve.Makeup);
        _dot.Visibility = Visibility.Visible;
        Canvas.SetLeft(_dot, X(input) - 4);
        Canvas.SetTop(_dot, Y(shown) - 4);
    }

    private static double X(double db) => (Math.Clamp(db, TransferCurve.Floor, 0) - TransferCurve.Floor) / -TransferCurve.Floor * Side;

    private static double Y(double db) => Side - X(db);

    private static double Db(float linear) => linear <= 1e-6f ? -120 : 20 * Math.Log10(linear);

    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];

    private static GeometryGroup GridGeometry()
    {
        var group = new GeometryGroup();
        for (double db = -48; db < 0; db += 12)
        {
            group.Children.Add(new LineGeometry { StartPoint = new Point(X(db), 0), EndPoint = new Point(X(db), Side) });
            group.Children.Add(new LineGeometry { StartPoint = new Point(0, Y(db)), EndPoint = new Point(Side, Y(db)) });
        }

        return group;
    }

    /// <summary>Una barra vertical: niveles de −60 a 0 dBFS desde abajo, o reducción de 0 a 24 dB desde arriba.</summary>
    private sealed class Meter
    {
        private const double Range = 60;
        private const double ReductionRange = 24;
        private const double FallDbPerSecond = 24;

        private readonly bool _fromTop;
        private readonly Rectangle _bar = new() { Width = BarWidth, Height = 0 };
        private readonly Rectangle _hold = new() { Width = BarWidth, Height = 2 };
        private readonly TextBlock _value = new() { FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center };
        private double _shown = double.NaN;
        private double _held = double.NaN;
        private double _heldFor;

        public Meter(string name, bool fromTop)
        {
            _fromTop = fromTop;
            var canvas = new Canvas { Width = BarWidth, Height = Side, Background = Resource("BackgroundBrush") };
            _bar.Fill = fromTop ? Resource("KeyframeBrush") : Resource("MeterLowBrush");
            _hold.Fill = Resource("TextBrush");
            canvas.Children.Add(_bar);
            canvas.Children.Add(_hold);
            _value.Foreground = Resource("TextMutedBrush");
            Root = new StackPanel { Spacing = 4, Width = 40 };
            var frame = new Border
            {
                BorderThickness = new Thickness(1),
                BorderBrush = Resource("ControlBorderBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = canvas,
            };
            Root.Children.Add(frame);
            Root.Children.Add(_value);
            Root.Children.Add(new TextBlock { Text = name, FontSize = 10, Foreground = Resource("TextMutedBrush"), HorizontalAlignment = HorizontalAlignment.Center });
            AutomationProperties.SetName(Root, name);
            ToolTipService.SetToolTip(Root, fromTop ? "Reducción de ganancia del compresor y del limitador, en dB" : name + ": pico en dBFS");
        }

        public StackPanel Root { get; }

        /// <summary>Sube al instante y baja a 24 dB por segundo; el pico queda 1,5 s.</summary>
        public void Update(double value, double seconds)
        {
            double level = _fromTop ? Math.Clamp(value, 0, ReductionRange) : Math.Clamp(value, -Range, 0);
            double floor = _fromTop ? 0 : -Range;
            _shown = double.IsNaN(_shown) || level >= _shown ? level : Math.Max(level, _shown - (FallDbPerSecond * seconds));
            _heldFor += seconds;
            if (double.IsNaN(_held) || level >= _held || _heldFor > 1.5)
            {
                _held = level;
                _heldFor = 0;
            }

            double fraction = _fromTop ? _shown / ReductionRange : (_shown - floor) / Range;
            double heldFraction = _fromTop ? _held / ReductionRange : (_held - floor) / Range;
            _bar.Height = Math.Max(0, fraction * Side);
            Canvas.SetTop(_bar, _fromTop ? 0 : Side - _bar.Height);
            Canvas.SetTop(_hold, _fromTop ? Math.Min(Side - 2, heldFraction * Side) : Math.Max(0, Side - (heldFraction * Side) - 1));
            _hold.Visibility = heldFraction > 0.001 ? Visibility.Visible : Visibility.Collapsed;
            if (!_fromTop)
            {
                _bar.Fill = Resource(_shown > -1 ? "MeterHighBrush" : _shown > -12 ? "MeterMidBrush" : "MeterLowBrush");
            }

            _value.Text = _fromTop
                ? string.Create(SpanishNumbers.Format, $"{(_held > 0.05 ? "−" : "")}{_held:0.0}")
                : (_held > -Range ? string.Create(SpanishNumbers.Format, $"{_held:0.0}").Replace('-', '−') : "−∞");
        }
    }
}
