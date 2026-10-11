using Rodaje.Core.Timing;

namespace Rodaje.App.Services.Inspector;

/// <summary>Valores fijos de la pestaña Velocidad: atajos, imanes del deslizador y congelado.</summary>
public static class SpeedPresets
{
    /// <summary>Botones rápidos bajo el deslizador.</summary>
    public static IReadOnlyList<Rational> Quick { get; } = [new(1, 2), new(1, 1), new(3, 2), new(2, 1), new(4, 1)];

    /// <summary>El deslizador se queda en estas velocidades al pasar cerca.</summary>
    public static IReadOnlyList<Rational> Detents { get; } = [new(1, 4), new(1, 2), new(1, 1), new(2, 1), new(4, 1), new(10, 1)];

    /// <summary>Lo que dura el cuadro congelado que mete «Congelar».</summary>
    public static Time FreezeDuration { get; } = Time.FromSeconds(3);
}
