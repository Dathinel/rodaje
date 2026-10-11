using System.Text.Json.Serialization;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Model;

/// <summary>
/// Velocidad de un clip: cuánto origen avanza por cada instante de la línea de tiempo. Con ella el clip guarda su
/// <see cref="Clip.Duration"/>; todo cálculo de tiempos pasa por <see cref="ClipTimeMap"/>.
/// </summary>
public sealed class ClipSpeed
{
    /// <summary>Velocidad constante, de 1/10 a 100. Con <see cref="Curve"/> o <see cref="Freeze"/> vale 1.</summary>
    public Rational Rate { get; set; } = new(1, 1);

    /// <summary>Velocidad que cambia a lo largo del clip; nula si es constante.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SpeedCurve? Curve { get; set; }

    /// <summary>
    /// Instante del mapeo, en ticks de la línea de tiempo, donde empieza el clip. Lo mueven recortar y dividir para que el
    /// resto del clip siga mostrando los mismos cuadros; con una curva, 0 es su inicio.
    /// </summary>
    public Time Offset { get; set; }

    /// <summary>El origen corre hacia atrás: el clip empieza en <see cref="Clip.SourceOut"/>.</summary>
    public bool Reverse { get; set; }

    /// <summary>Cuadro congelado: muestra <see cref="Clip.SourceIn"/> todo el clip y <see cref="Clip.SourceOut"/> es el tick siguiente.</summary>
    public bool Freeze { get; set; }

    /// <summary>El audio mantiene el tono al cambiar de velocidad.</summary>
    public bool PreservePitch { get; set; } = true;

    public FrameSampling FrameSampling { get; set; }

    /// <summary>Sin curva, sin reversa, sin congelar y a 1×: los tiempos son los de un clip sin velocidad.</summary>
    [JsonIgnore]
    public bool IsIdentity => Curve is null && !Reverse && !Freeze && Rate == new Rational(1, 1);

    /// <summary>Misma forma de mover el origen (sin <see cref="Offset"/>, <see cref="PreservePitch"/> ni <see cref="FrameSampling"/>).</summary>
    public bool SameTiming(ClipSpeed other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Rate == other.Rate && Reverse == other.Reverse && Freeze == other.Freeze
            && (Curve is null ? other.Curve is null : other.Curve is not null && Curve.ContentEquals(other.Curve));
    }

    public bool ContentEquals(ClipSpeed other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return SameTiming(other) && Offset == other.Offset && PreservePitch == other.PreservePitch && FrameSampling == other.FrameSampling;
    }

    public ClipSpeed Clone()
    {
        ClipSpeed copy = (ClipSpeed)MemberwiseClone();
        copy.Curve = Curve?.Clone();
        return copy;
    }
}

/// <summary>Cómo se elige el cuadro del origen cuando la velocidad no cae justo en uno.</summary>
public enum FrameSampling
{
    /// <summary>El cuadro que contiene el instante del origen.</summary>
    Nearest,

    /// <summary>Reservado: mezcla de los dos cuadros vecinos. Hoy se muestra como <see cref="Nearest"/>.</summary>
    Blend,

    /// <summary>Reservado para el módulo de flujo óptico (fase 7). Hoy se muestra como <see cref="Nearest"/>.</summary>
    OpticalFlow,
}

/// <summary>
/// Velocidad lineal a tramos sobre <see cref="Span"/> ticks de la línea de tiempo. Los puntos van en diezmilésimas de
/// <see cref="Span"/> (<see cref="AtScale"/>): estirar la curva solo cambia <see cref="Span"/>. Antes del primero y
/// después del último la velocidad sigue constante.
/// </summary>
public sealed class SpeedCurve
{
    /// <summary><see cref="SpeedPoint.At"/> del último punto.</summary>
    public const int AtScale = 10_000;

    public const int MaxPoints = 64;

    public Time Span { get; set; }

    /// <summary>De 2 a <see cref="MaxPoints"/>, el primero en 0 y el último en <see cref="AtScale"/>, en orden estricto.</summary>
    public List<SpeedPoint> Points { get; set; } = [];

    public bool ContentEquals(SpeedCurve other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Span == other.Span && Points.SequenceEqual(other.Points);
    }

    public SpeedCurve Clone() => new() { Span = Span, Points = [.. Points] };
}

/// <summary>Un punto de la curva: en <see cref="At"/> diezmilésimas de su duración, la velocidad vale <see cref="Hundredths"/>/100 (de 10 a 1000).</summary>
public sealed record SpeedPoint(int At, int Hundredths);
