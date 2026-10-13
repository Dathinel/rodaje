using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;

namespace Rodaje.App.Views.Inspector.Transition;

/// <summary>
/// Rueda de dirección: la flecha apunta hacia donde avanza la transición (0° a la derecha, 90° hacia abajo, como en la
/// imagen). Arrastrar la gira de 15° en 15°; con Mayús, libre. Las flechas del teclado la giran de a 15°.
/// </summary>
internal sealed partial class AngleDial : Grid
{
    private const double Side = 44;
    private const double Snap = 15;
    private readonly Ellipse _ring = new() { Width = Side, Height = Side, StrokeThickness = 1.5 };
    private readonly Line _arrow = new() { StrokeThickness = 2.5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Triangle };
    private readonly Ellipse _tip = new() { Width = 8, Height = 8 };
    private readonly Canvas _canvas = new() { Width = Side, Height = Side };
    private double _degrees;
    private bool _dragging;

    public AngleDial()
    {
        Width = Side;
        Height = Side;
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        _ring.Stroke = Brush("ControlBorderBrush");
        _ring.Fill = Brush("BackgroundBrush");
        _arrow.Stroke = Brush("AccentBrush");
        _tip.Fill = Brush("AccentBrush");
        _canvas.Children.Add(_ring);
        _canvas.Children.Add(_arrow);
        _canvas.Children.Add(_tip);
        Children.Add(_canvas);
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += (_, e) => End(e.Pointer);
        PointerCaptureLost += (_, _) => _dragging = false;
        KeyDown += OnKeyDown;
        AutomationProperties.SetName(this, "Dirección");
        Draw();
    }

    public event EventHandler<double>? Changing;

    public event EventHandler? Committed;

    public double Degrees
    {
        get => _degrees;
        set
        {
            _degrees = value;
            Draw();
        }
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    private void Draw()
    {
        double radians = _degrees * Math.PI / 180;
        double c = Side / 2;
        double r = (Side / 2) - 7;
        (_arrow.X1, _arrow.Y1) = (c - (Math.Cos(radians) * r * 0.55), c - (Math.Sin(radians) * r * 0.55));
        (_arrow.X2, _arrow.Y2) = (c + (Math.Cos(radians) * r), c + (Math.Sin(radians) * r));
        Canvas.SetLeft(_tip, _arrow.X2 - 4);
        Canvas.SetTop(_tip, _arrow.Y2 - 4);
        ToolTipService.SetToolTip(this, $"{_degrees:0}° · arrastra para girar (Mayús: sin pasos de 15°)");
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = true;
        CapturePointer(e.Pointer);
        Focus(FocusState.Pointer);
        Update(e);
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragging)
        {
            Update(e);
            e.Handled = true;
        }
    }

    private void Update(PointerRoutedEventArgs e)
    {
        Point p = e.GetCurrentPoint(this).Position;
        double degrees = Math.Atan2(p.Y - (Side / 2), p.X - (Side / 2)) * 180 / Math.PI;
        bool free = (e.KeyModifiers & VirtualKeyModifiers.Shift) != 0;
        degrees = free ? Math.Round(degrees) : Math.Round(degrees / Snap) * Snap;
        if (degrees <= -180)
        {
            degrees += 360;
        }

        if (degrees != _degrees)
        {
            Degrees = degrees;
            Changing?.Invoke(this, degrees);
        }
    }

    private void End(Pointer pointer)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        ReleasePointerCapture(pointer);
        Committed?.Invoke(this, EventArgs.Empty);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        double step = e.Key switch
        {
            VirtualKey.Right or VirtualKey.Down => Snap,
            VirtualKey.Left or VirtualKey.Up => -Snap,
            _ => 0,
        };
        if (step == 0)
        {
            return;
        }

        double degrees = Math.Round((_degrees + step) / Snap) * Snap;
        degrees = degrees > 180 ? degrees - 360 : degrees <= -180 ? degrees + 360 : degrees;
        Degrees = degrees;
        Changing?.Invoke(this, degrees);
        Committed?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }
}
