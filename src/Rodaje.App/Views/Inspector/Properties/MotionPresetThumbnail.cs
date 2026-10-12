using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Rodaje.App.Services.Inspector;
using Windows.Foundation;

namespace Rodaje.App.Views.Inspector.Properties;

/// <summary>
/// La miniatura de una animación de fábrica: el cuadro, la capa en cuatro instantes (de tenue a llena) y el recorrido de
/// su centro, con las mismas curvas que se aplican (<see cref="MotionPresetCommands.Preview"/>).
/// </summary>
internal static class MotionPresetThumbnail
{
    private const double Width = 64;
    private const double Height = 36;
    private const int ReferenceWidth = 160;
    private const int ReferenceHeight = 90;

    public static FrameworkElement Create(string presetId)
    {
        var canvas = new Canvas { Width = Width, Height = Height, HorizontalAlignment = HorizontalAlignment.Center };
        canvas.Clip = new RectangleGeometry { Rect = new Rect(0, 0, Width, Height) };
        Brush frame = (Brush)Application.Current.Resources["ControlBorderBrush"];
        Brush layer = (Brush)Application.Current.Resources["VideoClipEdgeBrush"];
        Brush path = (Brush)Application.Current.Resources["KeyframeBrush"];
        canvas.Children.Add(new Rectangle { Width = Width, Height = Height, Stroke = frame, StrokeThickness = 1 });

        IReadOnlyList<(double X, double Y, double Scale, double Rotation)> frames = MotionPresetCommands.Preview(presetId, ReferenceWidth, ReferenceHeight, 41);
        double k = Width / ReferenceWidth;
        int[] ghosts = [0, 13, 27, 40];
        for (int g = 0; g < ghosts.Length; g++)
        {
            (double x, double y, double scale, double rotation) = frames[ghosts[g]];
            double w = Width * 0.55 * scale;
            double h = Height * 0.55 * scale;
            var rect = new Rectangle
            {
                Width = w,
                Height = h,
                Fill = layer,
                Opacity = 0.18 + (0.24 * g),
                RadiusX = 1.5,
                RadiusY = 1.5,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform { Angle = rotation },
            };
            Canvas.SetLeft(rect, (Width / 2) + (x * k) - (w / 2));
            Canvas.SetTop(rect, (Height / 2) + (y * k) - (h / 2));
            canvas.Children.Add(rect);
        }

        var line = new Polyline { Stroke = path, StrokeThickness = 1.2, StrokeLineJoin = PenLineJoin.Round };
        foreach ((double x, double y, _, _) in frames)
        {
            line.Points.Add(new Point((Width / 2) + (x * k), (Height / 2) + (y * k)));
        }

        canvas.Children.Add(line);
        return canvas;
    }
}
