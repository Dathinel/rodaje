using System.Globalization;
using Rodaje.Core.Model;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;
using A = Rodaje.Core.Model.BuiltInEffects.AudioParameters;

namespace Rodaje.App.Services.Inspector;

/// <summary>
/// Un parámetro tal como lo muestra el inspector: nombre, unidad y rango en la interfaz, y la conversión al valor del
/// modelo (la escala y la opacidad se ven en porcentaje).
/// </summary>
/// <param name="EffectType">Efecto al que pertenece.</param>
/// <param name="Name">Clave del parámetro en el modelo.</param>
/// <param name="Label">Etiqueta en la interfaz.</param>
/// <param name="Description">Texto del historial al cambiarlo.</param>
/// <param name="Minimum">Mínimo en unidades de la interfaz.</param>
/// <param name="Maximum">Máximo en unidades de la interfaz.</param>
/// <param name="Step">Paso del deslizador.</param>
/// <param name="DisplayFactor">Unidades de la interfaz por unidad del modelo.</param>
/// <param name="DefaultValue">Valor del modelo cuando falta el parámetro.</param>
/// <param name="Decimals">Decimales del número.</param>
/// <param name="Unit">Unidad que se muestra («px», «%», «°», «dB», «s»).</param>
/// <param name="Animatable">Tiene rombo de keyframe.</param>
public sealed record ParameterSpec(
    string EffectType,
    string Name,
    string Label,
    string Description,
    double Minimum,
    double Maximum,
    double Step,
    double DisplayFactor,
    double DefaultValue,
    int Decimals,
    string Unit,
    bool Animatable)
{
    public double ToDisplay(double modelValue) => modelValue * DisplayFactor;

    /// <summary>Valor del modelo para lo que se ve; lo que se escribe a mano puede salir del rango del deslizador.</summary>
    public double ToModel(double displayValue) => Math.Round(displayValue, Decimals) / DisplayFactor;

    public string Format(double modelValue) =>
        ToDisplay(modelValue).ToString(Decimals == 0 ? "0" : "0." + new string('0', Decimals), CultureInfo.InvariantCulture);

    /// <summary>Posición, escala, rotación y opacidad; la posición usa el tamaño de la secuencia como rango.</summary>
    public static IReadOnlyList<ParameterSpec> Transform(int width, int height) =>
    [
        new(BuiltInEffects.Transform, T.X, "Posición X", "Mover en X", -width, width, 1, 1, 0, 0, "px", true),
        new(BuiltInEffects.Transform, T.Y, "Posición Y", "Mover en Y", -height, height, 1, 1, 0, 0, "px", true),
        new(BuiltInEffects.Transform, T.Scale, "Escala", "Cambiar escala", 1, 400, 1, 100, 1, 1, "%", true),
        new(BuiltInEffects.Transform, T.Rotation, "Rotación", "Girar", -360, 360, 1, 1, 0, 1, "°", true),
        new(BuiltInEffects.Transform, T.Opacity, "Opacidad", "Cambiar opacidad", 0, 100, 1, 100, 1, 0, "%", true),
    ];

    /// <summary>Volumen y fundidos; los fundidos llegan como mucho a la mitad del clip.</summary>
    public static IReadOnlyList<ParameterSpec> Audio(double clipSeconds)
    {
        double fade = Math.Max(0.1, Math.Floor(clipSeconds * 5) / 10);
        return
        [
            new(BuiltInEffects.Audio, A.GainDb, "Volumen", "Cambiar volumen", -60, 12, 0.5, 1, 0, 1, "dB", false),
            new(BuiltInEffects.Audio, A.FadeIn, "Fundido de entrada", "Fundido de entrada", 0, fade, 0.1, 1, 0, 1, "s", false),
            new(BuiltInEffects.Audio, A.FadeOut, "Fundido de salida", "Fundido de salida", 0, fade, 0.1, 1, 0, 1, "s", false),
        ];
    }
}

/// <summary>Las curvas entre keyframes con sus nombres, en el orden del desplegable.</summary>
public static class KeyframeEase
{
    public static IReadOnlyList<(string Name, Interpolation Value)> Options { get; } =
    [
        ("Lineal", Interpolation.Linear),
        ("Suave", Interpolation.EaseInOut),
        ("Acelerar", Interpolation.EaseIn),
        ("Frenar", Interpolation.EaseOut),
        ("Mantener", Interpolation.Hold),
    ];

    public static int IndexOf(Interpolation interpolation)
    {
        for (int i = 0; i < Options.Count; i++)
        {
            if (Options[i].Value == interpolation)
            {
                return i;
            }
        }

        return 0;
    }
}
