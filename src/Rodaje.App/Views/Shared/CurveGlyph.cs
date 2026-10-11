using Microsoft.UI.Xaml.Media;
using Rodaje.App.Services.Inspector;
using Rodaje.Core.Model;
using Windows.Foundation;

namespace Rodaje.App.Views.Shared;

/// <summary>Una curva de velocidad en miniatura (selector de preajustes, indicador del clip), con la altura logarítmica del editor.</summary>
internal static class CurveGlyph
{
    /// <summary>La polilínea de la curva entre <paramref name="from"/> y <paramref name="to"/>, en un área de <paramref name="width"/> × <paramref name="height"/>.</summary>
    public static PointCollection Line(IReadOnlyList<SpeedPoint> points, double from, double to, double width, double height, double inset = 1)
    {
        var line = new PointCollection();
        double w = Math.Max(1, width - (2 * inset));
        double h = Math.Max(1, height - (2 * inset));
        foreach ((double x, double level) in SpeedCurveMath.Shape(points, from, to, 4))
        {
            line.Add(new Point(inset + (x * w), inset + ((1 - level) * h)));
        }

        return line;
    }

    public static PointCollection Line(IReadOnlyList<SpeedPoint> points, double width, double height, double inset = 1) =>
        Line(points, 0, SpeedCurve.AtScale, width, height, inset);
}
