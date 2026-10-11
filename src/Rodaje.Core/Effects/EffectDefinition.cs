using System.Collections.Immutable;

namespace Rodaje.Core.Effects;

/// <summary>A qué se puede aplicar un efecto.</summary>
[Flags]
public enum EffectTarget
{
    None = 0,

    /// <summary>Clips de video (y pistas de video).</summary>
    Video = 1,

    /// <summary>Clips y pistas de audio.</summary>
    Audio = 2,

    /// <summary>El corte entre dos clips (<see cref="Model.TransitionInstance"/>).</summary>
    Transition = 4,
}

/// <summary>Dónde lo aplica el motor; decide el orden y si va en el mismo shader que la conversión de color.</summary>
public enum EffectStage
{
    /// <summary>Colocación de la capa en el cuadro.</summary>
    Transform,

    /// <summary>Por píxel, fusionado en el shader que convierte YUV a RGB (color, LUT, viñeta).</summary>
    PerPixel,

    /// <summary>Necesita vecinos: va en un intermedio del tamaño en pantalla (desenfoque, nitidez).</summary>
    Spatial,

    /// <summary>Cómo se mezcla la capa con lo de abajo.</summary>
    Blend,

    /// <summary>Mezcla de dos clips en un corte.</summary>
    Transition,

    /// <summary>En el mezclador de audio.</summary>
    Audio,

    /// <summary>Registrado para reconocerlo, pero el motor todavía no lo aplica (velocidad, estabilizar).</summary>
    Deferred,
}

public enum OptionKind
{
    /// <summary>Uno de <see cref="OptionDefinition.Choices"/>.</summary>
    Choice,

    /// <summary>Referencia <c>paquete@versión/elemento</c>, o vacía.</summary>
    PackageReference,
}

/// <summary>Parámetro numérico de un efecto. <see cref="Slot"/> es su posición en los valores que recibe el motor.</summary>
/// <param name="Name">Clave en <c>parameters</c>.</param>
/// <param name="DefaultValue">Valor cuando falta.</param>
/// <param name="Animatable">Admite keyframes.</param>
/// <param name="Minimum">Límite del modelo; <see cref="double.NegativeInfinity"/> si no tiene.</param>
/// <param name="Maximum">Límite del modelo; <see cref="double.PositiveInfinity"/> si no tiene.</param>
public sealed record ParameterDefinition(
    string Name,
    double DefaultValue,
    double Minimum,
    double Maximum,
    bool Animatable = true)
{
    public int Slot { get; internal init; }

    public double Clamp(double value) => double.IsNaN(value) ? DefaultValue : Math.Clamp(value, Minimum, Maximum);
}

/// <summary>Opción no numérica de un efecto, guardada como texto en <c>options</c>.</summary>
public sealed record OptionDefinition(string Name, OptionKind Kind, string DefaultValue, ImmutableArray<string> Choices)
{
    public static OptionDefinition Choice(string name, string defaultValue, params string[] choices) =>
        new(name, OptionKind.Choice, defaultValue, [.. choices]);

    public static OptionDefinition Reference(string name) => new(name, OptionKind.PackageReference, "", []);

    /// <summary>Si <paramref name="value"/> es aceptable; una referencia solo se comprueba al resolverla.</summary>
    public bool Accepts(string value) => Kind == OptionKind.PackageReference || Choices.Contains(value);
}

/// <summary>Definición estática de un tipo de efecto o de transición.</summary>
public sealed class EffectDefinition
{
    public const int MaxParameters = 16;

    public EffectDefinition(string type, int code, EffectTarget target, EffectStage stage,
        ImmutableArray<ParameterDefinition> parameters = default, ImmutableArray<OptionDefinition> options = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(code);
        parameters = parameters.IsDefault ? [] : parameters;
        if (parameters.Length > MaxParameters)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters), $"Un efecto admite hasta {MaxParameters} parámetros.");
        }

        Type = type;
        Code = code;
        Target = target;
        Stage = stage;
        Parameters = [.. parameters.Select((p, i) => p with { Slot = i })];
        Options = options.IsDefault ? [] : options;
    }

    public string Type { get; }

    /// <summary>Número estable que viaja al motor y a la GPU; nunca se reutiliza.</summary>
    public int Code { get; }

    public EffectTarget Target { get; }

    public EffectStage Stage { get; }

    public ImmutableArray<ParameterDefinition> Parameters { get; }

    public ImmutableArray<OptionDefinition> Options { get; }

    public bool IsTransition => Target == EffectTarget.Transition;

    public ParameterDefinition? FindParameter(string name)
    {
        foreach (ParameterDefinition parameter in Parameters)
        {
            if (parameter.Name == name)
            {
                return parameter;
            }
        }

        return null;
    }

    public OptionDefinition? FindOption(string name)
    {
        foreach (OptionDefinition option in Options)
        {
            if (option.Name == name)
            {
                return option;
            }
        }

        return null;
    }

    public override string ToString() => $"{Type} ({Code})";
}
