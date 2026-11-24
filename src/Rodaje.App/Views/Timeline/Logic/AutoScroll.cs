namespace Rodaje.App.Views.Timeline;

/// <summary>
/// El desplazamiento solo al arrastrar cerca de un borde de las pistas: nada fuera de la franja del borde y, dentro, más
/// rápido cuanto más cerca; en el borde o más allá, el paso máximo.
/// </summary>
public static class AutoScroll
{
    public const double Zone = 32;

    /// <summary>Píxeles por paso (unos 60 por segundo) con el puntero en el borde.</summary>
    public const double MaxStep = 24;

    /// <summary>El paso con signo para una posición del puntero en un eje de <paramref name="size"/> píxeles: negativo hacia el inicio.</summary>
    public static double Step(double position, double size)
    {
        if (size <= 0 || !double.IsFinite(position))
        {
            return 0;
        }

        // En una vista muy chica la franja no puede tapar más de un cuarto por lado.
        double zone = Math.Min(Zone, size / 4);
        double depth = position < zone ? position - zone : position > size - zone ? position - (size - zone) : 0;
        return depth == 0 ? 0 : Math.Sign(depth) * MaxStep * Math.Min(1, Math.Abs(depth) / zone);
    }
}
