using Rodaje.Core.Timing;

namespace Rodaje.App.Services.Inspector;

/// <summary>Valores fijos de la pestaña Velocidad: atajos, imanes del deslizador, congelado y límites del sonido.</summary>
public static class SpeedTabValues
{
    /// <summary>Botones rápidos bajo el deslizador.</summary>
    public static IReadOnlyList<Rational> Quick { get; } = [new(1, 2), new(1, 1), new(3, 2), new(2, 1), new(4, 1)];

    /// <summary>El deslizador se queda en estas velocidades al pasar cerca.</summary>
    public static IReadOnlyList<Rational> Detents { get; } = [new(1, 4), new(1, 2), new(1, 1), new(2, 1), new(4, 1), new(10, 1)];

    /// <summary>Lo que dura el cuadro congelado que mete «Congelar».</summary>
    public static Time FreezeDuration { get; } = Time.FromSeconds(3);

    /// <summary>Por encima el sonido, con o sin «Mantener tono», se oye granuloso; sigue sonando (decisión de Daniel).</summary>
    public const double GrainyAbove = 8;

    /// <summary>Por debajo el sonido se oye metálico y con eco; también sigue sonando.</summary>
    public const double HollowBelow = 0.25;

    /// <summary>El aviso de los límites del sonido a esa velocidad; nulo si suena bien o el clip no tiene sonido.</summary>
    public static string? AudioWarning(double rate, bool hasAudio)
    {
        if (!hasAudio)
        {
            return null;
        }

        if (rate > GrainyAbove)
        {
            return "Por encima de 8× el sonido se oye granuloso. Sigue sonando; si molesta, baja su volumen o silencia el clip.";
        }

        return rate < HollowBelow
            ? "Por debajo de 0,25× el sonido se oye metálico y con eco. Sigue sonando; si molesta, baja su volumen o silencia el clip."
            : null;
    }
}
