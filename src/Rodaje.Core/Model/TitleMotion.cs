using Rodaje.Core.Timing;

namespace Rodaje.Core.Model;

/// <summary>
/// Reglas comunes de la entrada y la salida de un título que comparten el motor y las ediciones: cuánto duran dentro del
/// clip y cómo se compone su delta con la transformación del clip.
/// </summary>
public static class TitleMotion
{
    /// <summary>Ticks de una animación que mueve algo, o 0 si no anima (sin duración o sin curvas con puntos).</summary>
    public static long Ticks(StyleAnimation? animation) =>
        animation is { DurationSeconds: > 0 } a && double.IsFinite(a.DurationSeconds) && a.Curves.Values.Any(k => k.Count > 0)
            ? Time.FromSeconds(a.DurationSeconds).Ticks
            : 0;

    /// <summary>
    /// Ajusta entrada y salida a <paramref name="durationTicks"/>: si no caben, las dos se encogen por igual y suman
    /// exactamente el clip. <c>Stretch</c> es el factor (1 si caben).
    /// </summary>
    public static (long In, long Out, double Stretch) Fit(long inTicks, long outTicks, long durationTicks)
    {
        long total = inTicks + outTicks;
        if (total <= durationTicks || total <= 0)
        {
            return (inTicks, outTicks, 1);
        }

        double stretch = (double)durationTicks / total;
        long fittedIn = Math.Min(durationTicks, (long)Math.Round(inTicks * stretch));
        long fittedOut = outTicks == 0 ? 0 : durationTicks - fittedIn;
        return (fittedIn, fittedOut, stretch);
    }

    /// <summary>
    /// Primero el delta de la animación, luego la transformación del clip: posición y giro se suman, escala y opacidad se
    /// multiplican, y el desplazamiento del delta gira y se escala con el clip.
    /// </summary>
    public static (double X, double Y, double Scale, double Rotation, double Opacity) Compose(
        double x, double y, double scale, double rotation, double opacity,
        double dx, double dy, double dScale, double dRotation, double dOpacity)
    {
        double radians = rotation * Math.PI / 180;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);
        return (
            x + (scale * ((dx * cos) - (dy * sin))),
            y + (scale * ((dx * sin) + (dy * cos))),
            scale * dScale,
            rotation + dRotation,
            Math.Clamp(opacity * dOpacity, 0, 1));
    }
}
