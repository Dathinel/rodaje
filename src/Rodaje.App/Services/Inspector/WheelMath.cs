using W = Rodaje.Core.Model.BuiltInEffects.ColorWheelsParameters;

namespace Rodaje.App.Services.Inspector;

/// <summary>
/// De un punto de la rueda (x, y de −1 a 1, y hacia arriba) al tinte R'G'B' de esa zona y de vuelta. El rojo está a 0°,
/// el verde a 120° y el azul a 240°; el tinte suma cero entre los tres canales, así no cambia el nivel de la zona (eso es
/// el deslizador general). La cuarta rueda, el desplazamiento, suma a toda la imagen por igual antes que las otras.
/// </summary>
public static class WheelMath
{
    private static readonly double[] Angles = [0, 2 * Math.PI / 3, 4 * Math.PI / 3];

    /// <summary>Los parámetros de cada rueda (rojo, verde, azul) y cuánto tinte da el borde.</summary>
    public static IReadOnlyList<(string Red, string Green, string Blue, double Scale)> Wheels { get; } =
    [
        (W.LiftRed, W.LiftGreen, W.LiftBlue, 0.2),
        (W.GammaRed, W.GammaGreen, W.GammaBlue, 0.5),
        (W.GainRed, W.GainGreen, W.GainBlue, 0.5),
        (W.OffsetRed, W.OffsetGreen, W.OffsetBlue, 0.2),
    ];

    /// <summary>El nombre de cada rueda, en el orden de <see cref="Wheels"/>.</summary>
    public static IReadOnlyList<string> Labels { get; } = ["Sombras", "Medios", "Luces", "Desplazamiento"];

    /// <summary>El punto dentro del círculo unidad.</summary>
    public static (double X, double Y) Clamp(double x, double y)
    {
        double r = Math.Sqrt((x * x) + (y * y));
        return r > 1 ? (x / r, y / r) : (x, y);
    }

    public static (double R, double G, double B) Tint(double x, double y, double scale)
    {
        (x, y) = Clamp(x, y);
        double C(int c) => scale * ((x * Math.Cos(Angles[c])) + (y * Math.Sin(Angles[c])));
        return (C(0), C(1), C(2));
    }

    /// <summary>El punto de un tinte (se ignora lo que tenga en común entre los tres canales).</summary>
    public static (double X, double Y) Position(double r, double g, double b, double scale)
    {
        if (!(scale > 0))
        {
            return (0, 0);
        }

        double x = 0, y = 0;
        double[] o = [r, g, b];
        for (int c = 0; c < 3; c++)
        {
            x += o[c] * Math.Cos(Angles[c]);
            y += o[c] * Math.Sin(Angles[c]);
        }

        return Clamp(2 * x / (3 * scale), 2 * y / (3 * scale));
    }
}
