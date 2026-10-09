using System.Globalization;

namespace FotoToma.Core.Timing;

/// <summary>
/// Instante o duración exacta, contada en ticks enteros de 1/705 600 000 de segundo.
/// </summary>
/// <remarks>
/// Ese número es divisible entre la duración de un cuadro a 24, 25, 30, 48, 50, 60, 90, 100 y 120 fps,
/// también en sus variantes NTSC (×1000/1001), y entre la duración de una muestra a 8, 16, 22,05, 24,
/// 32, 44,1, 48, 88,2, 96 y 192 kHz. Así las posiciones de video y de audio caen en ticks exactos y
/// sumar o restar tiempos nunca acumula error de redondeo, que es lo que pasa con segundos en double.
/// Un <c>long</c> alcanza para más de 400 años.
/// </remarks>
public readonly record struct Time : IComparable<Time>
{
    public const long TicksPerSecond = 705_600_000;

    public long Ticks { get; }

    public Time(long ticks) => Ticks = ticks;

    public static readonly Time Zero = new(0);

    public static Time FromTicks(long ticks) => new(ticks);

    /// <summary>Convierte segundos a ticks redondeando al tick más cercano. Solo para entradas humanas.</summary>
    public static Time FromSeconds(double seconds) => new((long)Math.Round(seconds * TicksPerSecond));

    public double ToSeconds() => (double)Ticks / TicksPerSecond;

    /// <summary>Instante en que empieza el cuadro <paramref name="frame"/> a la tasa dada.</summary>
    /// <remarks>
    /// Se redondea hacia arriba para que <c>FromFrames(n).ToFrames() == n</c> también con tasas
    /// cuyo cuadro no dura un número entero de ticks.
    /// </remarks>
    public static Time FromFrames(long frame, Rational rate)
    {
        Int128 numerator = (Int128)frame * TicksPerSecond * rate.Den;
        return new((long)CeilDiv(numerator, rate.Num));
    }

    /// <summary>Cuadro que se está mostrando en este instante (redondeo hacia abajo).</summary>
    public long ToFrames(Rational rate)
    {
        Int128 numerator = (Int128)Ticks * rate.Num;
        return (long)FloorDiv(numerator, (Int128)TicksPerSecond * rate.Den);
    }

    /// <summary>Lleva el instante al inicio del cuadro más cercano.</summary>
    public Time SnapToFrame(Rational rate)
    {
        Time floor = FromFrames(ToFrames(rate), rate);
        Time next = FromFrames(ToFrames(rate) + 1, rate);
        return (Ticks - floor.Ticks) * 2 < next.Ticks - floor.Ticks ? floor : next;
    }

    public static Time FromSamples(long sample, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        Int128 numerator = (Int128)sample * TicksPerSecond;
        return new((long)CeilDiv(numerator, sampleRate));
    }

    public long ToSamples(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        return (long)FloorDiv((Int128)Ticks * sampleRate, TicksPerSecond);
    }

    public static Time Min(Time a, Time b) => a.Ticks <= b.Ticks ? a : b;

    public static Time Max(Time a, Time b) => a.Ticks >= b.Ticks ? a : b;

    public int CompareTo(Time other) => Ticks.CompareTo(other.Ticks);

    public static Time operator +(Time a, Time b) => new(checked(a.Ticks + b.Ticks));

    public static Time operator -(Time a, Time b) => new(checked(a.Ticks - b.Ticks));

    public static Time operator -(Time a) => new(checked(-a.Ticks));

    public static bool operator <(Time a, Time b) => a.Ticks < b.Ticks;

    public static bool operator >(Time a, Time b) => a.Ticks > b.Ticks;

    public static bool operator <=(Time a, Time b) => a.Ticks <= b.Ticks;

    public static bool operator >=(Time a, Time b) => a.Ticks >= b.Ticks;

    public Time Add(Time other) => this + other;

    public Time Subtract(Time other) => this - other;

    public Time Negate() => -this;

    /// <summary>Formato legible hh:mm:ss.mmm, pensado para mensajes de error y depuración.</summary>
    public override string ToString()
    {
        long ticks = Math.Abs(Ticks);
        long totalMs = ticks / (TicksPerSecond / 1000);
        string sign = Ticks < 0 ? "-" : "";
        return string.Create(CultureInfo.InvariantCulture,
            $"{sign}{totalMs / 3_600_000:00}:{totalMs / 60_000 % 60:00}:{totalMs / 1000 % 60:00}.{totalMs % 1000:000}");
    }

    private static Int128 FloorDiv(Int128 a, Int128 b)
    {
        Int128 q = a / b;
        return (a % b != 0 && (a < 0) != (b < 0)) ? q - 1 : q;
    }

    private static Int128 CeilDiv(Int128 a, Int128 b) => -FloorDiv(-a, b);
}
