namespace Rodaje.Paquetes.Content;

/// <summary>
/// Una LUT 3D leída de un <c>.cube</c>: <see cref="Size"/>³ entradas RGB, con R variando más rápido, luego G, luego B
/// (el orden del formato). Los valores se llevan del dominio del archivo a 0–1 al leerla.
/// </summary>
/// <param name="Title">El <c>TITLE</c> del archivo, o vacío.</param>
/// <param name="Size">De 2 a 65.</param>
/// <param name="Rgb">Size³ × 3 valores.</param>
public sealed record LutTable(string Title, int Size, float[] Rgb)
{
    public const int MinSize = 2;
    public const int MaxSize = 65;

    /// <summary>Tamaño máximo del archivo <c>.cube</c>.</summary>
    public const long MaxFileBytes = 16L * 1024 * 1024;
}

/// <summary>Cómo pasa un valor de una clave a la siguiente; los nombres son los de <c>.rodaje</c> (<c>easeInOut</c>…).</summary>
public enum PresetInterpolation
{
    Hold,
    Linear,
    EaseIn,
    EaseOut,
    EaseInOut,
}

/// <summary>Una clave de una curva: <paramref name="T"/> en segundos desde el inicio del preajuste.</summary>
/// <param name="T">Segundos desde el inicio.</param>
/// <param name="Value">Valor del parámetro.</param>
/// <param name="Interpolation">Cómo se llega a la clave siguiente.</param>
public readonly record struct PresetKey(double T, double Value, PresetInterpolation Interpolation);

/// <summary>Curva de un parámetro de un efecto primitivo (por ejemplo <c>rodaje.transform</c> / <c>scale</c>).</summary>
public sealed record PresetCurve(string Effect, string Parameter, IReadOnlyList<PresetKey> Keys);

/// <summary>Un efecto primitivo con valores fijos, tal como se copia al proyecto.</summary>
/// <param name="Type">Tipo del registro de efectos, por ejemplo <c>rodaje.color</c>.</param>
/// <param name="Parameters">Valores numéricos por nombre.</param>
/// <param name="Options">Opciones de texto por nombre (una LUT, un modo de mezcla).</param>
public sealed record PresetEffect(string Type, IReadOnlyDictionary<string, double> Parameters, IReadOnlyDictionary<string, string> Options);

/// <summary><c>rodaje.filter</c> v1: una lista de efectos primitivos que se copia al clip.</summary>
public sealed record FilterPreset(string Id, string Name, IReadOnlyList<PresetEffect> Effects)
{
    public const string Format = "rodaje.filter";
    public const int Version = 1;
}

/// <summary>Dónde va la animación de un <see cref="EffectPreset"/> dentro del clip.</summary>
public enum EffectPlacement
{
    /// <summary>Al principio del clip.</summary>
    In,

    /// <summary>Al final del clip.</summary>
    Out,

    /// <summary>Estirada a todo el clip.</summary>
    Whole,
}

/// <summary>
/// <c>rodaje.effect</c> v1: efectos primitivos más curvas sobre sus parámetros, que se copian al clip como keyframes.
/// Las posiciones van en píxeles de la secuencia <see cref="ReferenceWidth"/> × <see cref="ReferenceHeight"/>.
/// </summary>
public sealed record EffectPreset(
    string Id,
    string Name,
    EffectPlacement Placement,
    double DurationSeconds,
    int ReferenceWidth,
    int ReferenceHeight,
    IReadOnlyList<PresetEffect> Effects,
    IReadOnlyList<PresetCurve> Curves)
{
    public const string Format = "rodaje.effect";
    public const int Version = 1;
}

/// <summary>
/// <c>rodaje.preset</c> v2: curvas de la transformación del clip que sale y del que entra, más una mezcla propia
/// (<see cref="Mix"/>, por ejemplo <c>rodaje.crossfade</c>). El v1 de «Guardar como transición» se lee como curvas del
/// clip que entra con fundido cruzado.
/// </summary>
public sealed record TransitionPreset(
    string Id,
    string Name,
    double DurationSeconds,
    string Mix,
    int ReferenceWidth,
    int ReferenceHeight,
    IReadOnlyList<PresetCurve> Outgoing,
    IReadOnlyList<PresetCurve> Incoming)
{
    public const string Format = "rodaje.preset";
    public const int Version = 2;

    /// <summary>Parámetros de <see cref="Mix"/> (por ejemplo <c>angle</c> de un barrido); los que faltan van por defecto.</summary>
    public IReadOnlyDictionary<string, double> MixParameters { get; init; } = EmptyNumbers;

    /// <summary>Opciones de <see cref="Mix"/> (por ejemplo <c>direction</c> de un empuje).</summary>
    public IReadOnlyDictionary<string, string> MixOptions { get; init; } = EmptyOptions;

    private static readonly IReadOnlyDictionary<string, double> EmptyNumbers = new Dictionary<string, double>();
    private static readonly IReadOnlyDictionary<string, string> EmptyOptions = new Dictionary<string, string>();
}

/// <summary>Un archivo de contenido no se pudo leer; el mensaje lo explica en español.</summary>
public sealed class PackageContentException : Exception
{
    public PackageContentException()
    {
    }

    public PackageContentException(string message)
        : base(message)
    {
    }

    public PackageContentException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
