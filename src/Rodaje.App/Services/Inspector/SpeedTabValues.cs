using Rodaje.Core.Model;
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

    /// <summary>Opciones de cuadros intermedios para el desplegable del inspector.</summary>
    public static IReadOnlyList<SamplingOption> SamplingOptions { get; } =
    [
        new(FrameSampling.Nearest, "Cuadro más cercano"),
        new(FrameSampling.Blend, "Mezcla de cuadros"),
        new(FrameSampling.OpticalFlow, "Flujo óptico"),
    ];

    public static ChoiceList<FrameSampling> SamplingChoices()
    {
        var list = new ChoiceList<FrameSampling>();
        foreach (SamplingOption option in SamplingOptions)
        {
            list.Add(option.Name, option.Sampling);
        }

        return list;
    }

    /// <summary>
    /// Los clips que deciden el modo de cuadros intermedios a mostrar: solo aquellos que admiten cambiarlo (<see cref="SpeedState.CanChangeSampling"/>),
    /// o todos si ninguno lo admite (p. ej. solo audio o congelados).
    /// </summary>
    public static IReadOnlyList<SpeedState> SamplingDeciders(IReadOnlyList<SpeedState> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        var applicable = new List<SpeedState>();
        for (int i = 0; i < states.Count; i++)
        {
            if (states[i].CanChangeSampling)
            {
                applicable.Add(states[i]);
            }
        }

        return applicable.Count > 0 ? applicable : states;
    }

    /// <summary>
    /// Índice en <see cref="SamplingChoices"/> a seleccionar en el desplegable de cuadros intermedios;
    /// −1 si la selección es mixta o no hay elementos.
    /// </summary>
    public static int SelectedSamplingIndex(IReadOnlyList<SpeedState> states)
    {
        ArgumentNullException.ThrowIfNull(states);
        if (states.Count == 0)
        {
            return -1;
        }

        if (states.Count == 1)
        {
            return SamplingChoices().IndexOf(states[0].Sampling);
        }

        IReadOnlyList<SpeedState> deciders = SamplingDeciders(states);
        if (deciders.Count == 0)
        {
            return -1;
        }

        FrameSampling first = deciders[0].Sampling;
        for (int i = 1; i < deciders.Count; i++)
        {
            if (deciders[i].Sampling != first)
            {
                return -1;
            }
        }

        return SamplingChoices().IndexOf(first);
    }

    /// <summary>Por encima el sonido, con o sin «Mantener tono», se oye granuloso; sigue sonando (decisión de diseño).</summary>
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

/// <summary>Opción de muestreo de cuadros intermedios para la interfaz de velocidad.</summary>
public sealed record SamplingOption(FrameSampling Sampling, string Name);
