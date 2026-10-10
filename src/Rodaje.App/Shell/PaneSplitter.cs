using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Rodaje.App.Shell;

/// <summary>
/// Separador que se arrastra para cambiar el ancho de una columna (o el alto de una fila) de su <see cref="Grid"/>.
/// Va en su propia columna o fila; <see cref="TargetIndex"/> es la que cambia y <see cref="GrowsTowardEnd"/> dice si
/// crece al arrastrar hacia la derecha o abajo (la columna está antes del separador) o al revés.
/// </summary>
internal sealed partial class PaneSplitter : Grid
{
    private readonly Rectangle _line;
    private double _startSize;
    private double _startPointer;
    private bool _dragging;

    public PaneSplitter()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _line = new Rectangle { Fill = (Brush)Application.Current.Resources["StrokeBrush"] };
        Children.Add(_line);
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += (_, _) => EndDrag();
        PointerEntered += (_, _) => _line.Fill = (Brush)Application.Current.Resources["AccentBrush"];
        PointerExited += (_, _) =>
        {
            if (!_dragging)
            {
                _line.Fill = (Brush)Application.Current.Resources["StrokeBrush"];
            }
        };
        Loaded += (_, _) => ApplyOrientation();
    }

    /// <summary>Se soltó el separador; da el tamaño nuevo en píxeles independientes del DPI.</summary>
    public event EventHandler<double>? Resized;

    /// <summary><c>true</c>: cambia una columna (se arrastra a los lados); <c>false</c>: una fila.</summary>
    public bool ResizesColumn { get; set; } = true;

    public int TargetIndex { get; set; }

    public bool GrowsTowardEnd { get; set; } = true;

    public double MinSize { get; set; } = 120;

    public double MaxSize { get; set; } = 2000;

    /// <summary>Lo que se le deja como mínimo a la columna o fila elástica (*) al agrandar la que se arrastra.</summary>
    public double ReserveForStar { get; set; } = 320;

    private void ApplyOrientation()
    {
        if (ResizesColumn)
        {
            _line.Width = 1;
            _line.HorizontalAlignment = HorizontalAlignment.Center;
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        }
        else
        {
            _line.Height = 1;
            _line.VerticalAlignment = VerticalAlignment.Center;
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
        }
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (Parent is not Grid grid || !TryGetSize(grid, out double size))
        {
            return;
        }

        _startSize = size;
        _startPointer = Coordinate(e, grid);
        _dragging = CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || Parent is not Grid grid)
        {
            return;
        }

        double delta = Coordinate(e, grid) - _startPointer;
        double wanted = _startSize + (GrowsTowardEnd ? delta : -delta);
        SetSize(grid, Math.Clamp(wanted, MinSize, Math.Max(MinSize, Math.Min(MaxSize, Available(grid)))));
        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        ReleasePointerCapture(e.Pointer);
        EndDrag();
        e.Handled = true;
    }

    private void EndDrag()
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        _line.Fill = (Brush)Application.Current.Resources["StrokeBrush"];
        if (Parent is Grid grid && TryGetSize(grid, out double size))
        {
            Resized?.Invoke(this, size);
        }
    }

    private double Coordinate(PointerRoutedEventArgs e, Grid grid)
    {
        Windows.Foundation.Point p = e.GetCurrentPoint(grid).Position;
        return ResizesColumn ? p.X : p.Y;
    }

    /// <summary>El máximo que puede medir la columna sin dejar a la elástica por debajo de su reserva.</summary>
    private double Available(Grid grid)
    {
        double total = ResizesColumn ? grid.ActualWidth : grid.ActualHeight;
        double others = 0;
        if (ResizesColumn)
        {
            for (int i = 0; i < grid.ColumnDefinitions.Count; i++)
            {
                if (i != TargetIndex && !grid.ColumnDefinitions[i].Width.IsStar)
                {
                    others += grid.ColumnDefinitions[i].ActualWidth;
                }
            }
        }
        else
        {
            for (int i = 0; i < grid.RowDefinitions.Count; i++)
            {
                if (i != TargetIndex && !grid.RowDefinitions[i].Height.IsStar)
                {
                    others += grid.RowDefinitions[i].ActualHeight;
                }
            }
        }

        return total - others - ReserveForStar;
    }

    private bool TryGetSize(Grid grid, out double size)
    {
        if (ResizesColumn && TargetIndex < grid.ColumnDefinitions.Count)
        {
            size = grid.ColumnDefinitions[TargetIndex].ActualWidth;
            return true;
        }

        if (!ResizesColumn && TargetIndex < grid.RowDefinitions.Count)
        {
            size = grid.RowDefinitions[TargetIndex].ActualHeight;
            return true;
        }

        size = 0;
        return false;
    }

    private void SetSize(Grid grid, double size)
    {
        if (ResizesColumn)
        {
            grid.ColumnDefinitions[TargetIndex].Width = new GridLength(size);
        }
        else
        {
            grid.RowDefinitions[TargetIndex].Height = new GridLength(size);
        }
    }
}
