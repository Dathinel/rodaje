using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Rodaje.App.Services.Inspector;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Rodaje.App.Views.Inspector.Filters;

/// <summary>
/// Tres ruedas (sombras, medios y luces): el punto marca el tinte de la zona. Arrastrarlo cambia el tinte (un paso del
/// historial por arrastre, lo junta quien escucha) y el doble clic lo vuelve al centro. La cuenta está en <see cref="WheelMath"/>.
/// </summary>
internal sealed partial class ColorWheelsControl : Grid
{
    private const double Size = 92;
    private const int Wedges = 48;
    private static readonly string[] Labels = ["Sombras", "Medios", "Luces"];

    private readonly Ellipse[] _pucks = new Ellipse[3];
    private readonly Grid[] _wheels = new Grid[3];
    private int? _dragging;

    public ColorWheelsControl()
    {
        ColumnSpacing = 6;
        Padding = new Thickness(12, 4, 6, 6);
        for (int i = 0; i < 3; i++)
        {
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var stack = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(new TextBlock { Text = Labels[i], HorizontalAlignment = HorizontalAlignment.Center, FontSize = 12 });
            Grid wheel = Wheel();
            int index = i;
            _pucks[i] = new Ellipse
            {
                Width = 12,
                Height = 12,
                Stroke = new SolidColorBrush(Colors.Black),
                StrokeThickness = 1.5,
                Fill = new SolidColorBrush(Colors.White),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
            };
            wheel.Children.Add(_pucks[i]);
            wheel.PointerPressed += (_, e) => OnPressed(index, e);
            wheel.PointerMoved += (_, e) => OnMoved(index, e);
            wheel.PointerReleased += (_, e) => OnReleased(index, e);
            wheel.PointerCaptureLost += (_, e) => OnReleased(index, e);
            wheel.DoubleTapped += (_, _) => WheelChanged?.Invoke(this, new WheelChange(index, 0, 0, Final: true));
            AutomationProperties.SetName(wheel, "Rueda de " + Labels[i].ToLowerInvariant());
            ToolTipService.SetToolTip(wheel, "Arrastra el punto hacia un color para teñir " + Labels[i].ToLowerInvariant() + ". Doble clic: al centro.");
            _wheels[i] = wheel;
            stack.Children.Add(wheel);
            SetColumn(stack, i);
            Children.Add(stack);
        }
    }

    /// <summary>Un punto movido (x, y de −1 a 1, y hacia arriba); <see cref="WheelChange.Final"/> al soltar o al reiniciar.</summary>
    public event EventHandler<WheelChange>? WheelChanged;

    /// <summary>Pone los tres puntos donde dicen los parámetros (x, y de −1 a 1 por rueda).</summary>
    public void Show(IReadOnlyList<(double X, double Y)> positions)
    {
        for (int i = 0; i < 3 && i < positions.Count; i++)
        {
            if (_dragging == i)
            {
                continue;
            }

            (double x, double y) = positions[i];
            _pucks[i].Margin = new Thickness((Size / 2) + (x * Size / 2) - 6, (Size / 2) - (y * Size / 2) - 6, 0, 0);
        }
    }

    private void OnPressed(int index, PointerRoutedEventArgs e)
    {
        _dragging = index;
        _wheels[index].CapturePointer(e.Pointer);
        Move(index, e, final: false);
        e.Handled = true;
    }

    private void OnMoved(int index, PointerRoutedEventArgs e)
    {
        if (_dragging == index)
        {
            Move(index, e, final: false);
            e.Handled = true;
        }
    }

    private void OnReleased(int index, PointerRoutedEventArgs e)
    {
        if (_dragging != index)
        {
            return;
        }

        Move(index, e, final: true);
        _dragging = null;
        _wheels[index].ReleasePointerCaptures();
    }

    private void Move(int index, PointerRoutedEventArgs e, bool final)
    {
        Point p = e.GetCurrentPoint(_wheels[index]).Position;
        (double x, double y) = WheelMath.Clamp(((p.X / Size) * 2) - 1, 1 - ((p.Y / Size) * 2));
        _pucks[index].Margin = new Thickness((Size / 2) + (x * Size / 2) - 6, (Size / 2) - (y * Size / 2) - 6, 0, 0);
        WheelChanged?.Invoke(this, new WheelChange(index, x, y, final));
    }

    /// <summary>El disco: cuñas con el tinte de cada dirección y un degradado blanco hacia el centro.</summary>
    private static Grid Wheel()
    {
        var grid = new Grid { Width = Size, Height = Size, Background = new SolidColorBrush(Colors.Transparent) };
        double radius = Size / 2;
        for (int i = 0; i < Wedges; i++)
        {
            double a0 = i * 2 * Math.PI / Wedges, a1 = (i + 1.05) * 2 * Math.PI / Wedges, mid = (i + 0.5) * 2 * Math.PI / Wedges;
            (double r, double g, double b) = WheelMath.Tint(Math.Cos(mid), Math.Sin(mid), 1);
            var figure = new PathFigure { StartPoint = new Point(radius, radius), IsClosed = true };
            figure.Segments.Add(new LineSegment { Point = new Point(radius + (radius * Math.Cos(a0)), radius - (radius * Math.Sin(a0))) });
            figure.Segments.Add(new LineSegment { Point = new Point(radius + (radius * Math.Cos(a1)), radius - (radius * Math.Sin(a1))) });
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            grid.Children.Add(new Path
            {
                Data = geometry,
                Fill = new SolidColorBrush(ColorHelper.FromArgb(255, (byte)(255 * (0.5 + (0.5 * r))), (byte)(255 * (0.5 + (0.5 * g))),
                    (byte)(255 * (0.5 + (0.5 * b))))),
                IsHitTestVisible = false,
            });
        }

        var fade = new RadialGradientBrush();
        fade.GradientStops.Add(new GradientStop { Color = ColorHelper.FromArgb(255, 128, 128, 128), Offset = 0 });
        fade.GradientStops.Add(new GradientStop { Color = ColorHelper.FromArgb(0, 128, 128, 128), Offset = 1 });
        grid.Children.Add(new Ellipse { Fill = fade, IsHitTestVisible = false });
        grid.Children.Add(new Ellipse { Stroke = new SolidColorBrush(ColorHelper.FromArgb(160, 0, 0, 0)), StrokeThickness = 1, IsHitTestVisible = false });
        return grid;
    }
}

/// <summary>Un punto de rueda movido.</summary>
internal readonly record struct WheelChange(int Wheel, double X, double Y, bool Final);
