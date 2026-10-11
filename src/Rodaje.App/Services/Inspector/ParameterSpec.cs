using System.Globalization;
using Rodaje.Core.Effects;
using Rodaje.Core.Model;

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
    /// <summary>Límite del modelo (registro); lo escrito a mano se lleva a él.</summary>
    public double ModelMinimum { get; init; } = double.NegativeInfinity;

    public double ModelMaximum { get; init; } = double.PositiveInfinity;

    /// <summary>Texto del historial y de la ayuda del botón de restablecer: «Restablecer posición X».</summary>
    public string ResetDescription => "Restablecer " + char.ToLowerInvariant(Label[0]) + Label[1..];

    public double ToDisplay(double modelValue) => modelValue * DisplayFactor;

    /// <summary>Valor del modelo para lo que se ve; lo que se escribe a mano puede salir del rango del deslizador.</summary>
    public double ToModel(double displayValue) => Math.Clamp(Math.Round(displayValue, Decimals) / DisplayFactor, ModelMinimum, ModelMaximum);

    public string Format(double modelValue) =>
        ToDisplay(modelValue).ToString(Decimals == 0 ? "0" : "0." + new string('0', Decimals), CultureInfo.InvariantCulture);

    /// <summary>Lo que hace falta para fijar los rangos que dependen del contexto.</summary>
    /// <param name="Width">Ancho de la secuencia.</param>
    /// <param name="Height">Alto de la secuencia.</param>
    /// <param name="ClipSeconds">Duración del clip (para los fundidos).</param>
    public readonly record struct Context(int Width, int Height, double ClipSeconds);

    /// <summary>Un parámetro del registro con su presentación de <see cref="EffectUi"/>.</summary>
    /// <exception cref="ArgumentException">El tipo o el parámetro no están registrados, o no tienen presentación.</exception>
    public static ParameterSpec For(string effectType, string name, Context context)
    {
        EffectDefinition definition = EffectRegistry.Find(effectType)
            ?? throw new ArgumentException($"«{effectType}» no está en el registro de efectos.", nameof(effectType));
        ParameterDefinition parameter = definition.FindParameter(name)
            ?? throw new ArgumentException($"«{effectType}» no tiene el parámetro «{name}».", nameof(name));
        ParameterUi ui = EffectUi.Find(effectType, name)
            ?? throw new ArgumentException($"Falta la presentación de «{effectType}/{name}».", nameof(name));
        (double minimum, double maximum) = ui.Range switch
        {
            UiRange.SequenceWidth => (-context.Width, (double)context.Width),
            UiRange.SequenceHeight => (-context.Height, (double)context.Height),
            UiRange.HalfClip => (0, Math.Max(0.1, Math.Floor(context.ClipSeconds * 5) / 10)),
            UiRange.Custom => (ui.Minimum, ui.Maximum),
            _ => (parameter.Minimum * ui.DisplayFactor, parameter.Maximum * ui.DisplayFactor),
        };
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum))
        {
            throw new ArgumentException($"«{effectType}/{name}» no tiene límites en el registro: necesita un rango propio.", nameof(name));
        }

        return new ParameterSpec(effectType, name, ui.Label, ui.Description, minimum, maximum, ui.Step, ui.DisplayFactor,
            parameter.DefaultValue, ui.Decimals, ui.Unit, parameter.Animatable)
        {
            ModelMinimum = parameter.Minimum,
            ModelMaximum = parameter.Maximum,
        };
    }

    /// <summary>Todos los parámetros del efecto, en el orden del registro.</summary>
    public static IReadOnlyList<ParameterSpec> ForEffect(string effectType, Context context) =>
        EffectRegistry.Find(effectType) is { } definition
            ? [.. definition.Parameters.Select(p => For(effectType, p.Name, context))]
            : [];

    /// <summary>Posición, escala, rotación y opacidad; la posición usa el tamaño de la secuencia como rango.</summary>
    public static IReadOnlyList<ParameterSpec> Transform(int width, int height) =>
        ForEffect(BuiltInEffects.Transform, new Context(width, height, 0));

    /// <summary>Volumen y fundidos; los fundidos llegan como mucho a la mitad del clip.</summary>
    public static IReadOnlyList<ParameterSpec> Audio(double clipSeconds) =>
        ForEffect(BuiltInEffects.Audio, new Context(0, 0, clipSeconds));
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
