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

    /// <summary>Registrado para reconocerlo, pero el motor no lo aplica como efecto (la velocidad).</summary>
    Deferred,
}

public enum OptionKind
{
    /// <summary>Uno de <see cref="OptionDefinition.Choices"/>.</summary>
    Choice,

    /// <summary>Referencia <c>paquete@versión/elemento</c>, o vacía.</summary>
    PackageReference,

    /// <summary>Texto libre de hasta <see cref="OptionDefinition.MaxTextLength"/> caracteres, sin caracteres de control.</summary>
    Text,
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

    public const int MaxTextLength = 16_384;

    public static OptionDefinition Text(string name) => new(name, OptionKind.Text, "", []);

    /// <summary>Prefijo de un archivo suelto en una referencia que lo admite (<see cref="AllowsFile"/>).</summary>
    public const string FilePrefix = "file:";

    /// <param name="name">Clave en <c>options</c>.</param>
    /// <param name="allowsFile">Admite también un archivo suelto, <c>file:</c> más su ruta (la LUT de «Cargar .cube…»).</param>
    public static OptionDefinition Reference(string name, bool allowsFile = false) =>
        new(name, OptionKind.PackageReference, "", []) { AllowsFile = allowsFile };

    /// <summary>La referencia admite <c>file:</c> más una ruta, relativa a la carpeta del proyecto o absoluta, con <c>/</c>.</summary>
    public bool AllowsFile { get; init; }

    /// <summary>
    /// <c>file:</c> seguido de una ruta no vacía, sin caracteres de control ni los que Windows no admite en una ruta
    /// (<c>" &lt; &gt; |</c>): la misma regla que la ruta de un medio.
    /// </summary>
    public static bool IsFileReference(string? value) =>
        value is not null && value.Length > FilePrefix.Length && value.StartsWith(FilePrefix, StringComparison.Ordinal)
        && !value.AsSpan(FilePrefix.Length).ContainsAnyInRange('\0', '\u001f')
        && value.AsSpan(FilePrefix.Length).IndexOfAny("\"<>|") < 0;

    /// <summary>
    /// Si <paramref name="value"/> es aceptable: uno de <see cref="Choices"/>, o una referencia vacía o bien formada
    /// (<see cref="IsWellFormedReference"/>). Si el paquete existe lo decide quien la resuelve.
    /// </summary>
    public bool Accepts(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Kind switch
        {
            OptionKind.PackageReference => value.Length == 0 || IsWellFormedReference(value) || (AllowsFile && IsFileReference(value)),
            OptionKind.Text => value.Length <= MaxTextLength && !value.AsSpan().ContainsAnyInRange((char)0, (char)0x1f),
            _ => Choices.Contains(value),
        };
    }

    /// <summary>
    /// Forma <c>paquete@versión/elemento</c>: paquete y elemento en minúsculas ASCII, dígitos, guiones y puntos; la versión
    /// empieza con un dígito. La comprobación completa (versión semántica, nombres reservados) es de <c>Rodaje.Packages</c>.
    /// </summary>
    public static bool IsWellFormedReference(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        int at = value.IndexOf('@', StringComparison.Ordinal);
        int slash = value.IndexOf('/', StringComparison.Ordinal);
        if (at < 0 || slash < at)
        {
            return false;
        }

        ReadOnlySpan<char> text = value;
        ReadOnlySpan<char> version = text[(at + 1)..slash];
        return IsIdentifier(text[..at])
            && IsIdentifier(text[(slash + 1)..])
            && version.Length is > 0 and <= MaxReferencePart
            && char.IsAsciiDigit(version[0])
            && !version.ContainsAnyExcept(VersionChars);
    }

    private const int MaxReferencePart = 64;

    private static readonly System.Buffers.SearchValues<char> IdentifierChars =
        System.Buffers.SearchValues.Create("abcdefghijklmnopqrstuvwxyz0123456789-.");

    private static readonly System.Buffers.SearchValues<char> VersionChars =
        System.Buffers.SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-.+");

    private static bool IsIdentifier(ReadOnlySpan<char> part) =>
        part.Length is > 0 and <= MaxReferencePart && !part.ContainsAnyExcept(IdentifierChars);
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

    /// <summary>Reservado: se reconoce, pero no se ofrece ni se acepta en un proyecto (<c>rodaje.speed</c>).</summary>
    public bool Hidden { get; init; }

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
