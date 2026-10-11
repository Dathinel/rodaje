using Rodaje.Core.Model;

namespace Rodaje.Core.Timing;

/// <summary>
/// Una velocidad compilada: <c>I(u)</c>, el origen recorrido tras <c>u</c> ticks de la línea de tiempo, exacto en
/// <see cref="Int128"/>. Constante es <c>u·num/den</c>; una curva es lineal a tramos en centésimas, así que <c>I</c> es
/// cuadrática en cada tramo y su inversa sale con una raíz entera. Inmutable.
/// </summary>
/// <remarks>
/// Cotas: |u| ≤ <see cref="MaxArgument"/> e |I| ≤ <see cref="MaxTarget"/>; con ellas el numerador más grande
/// (L·P + 2·L·h·x + Δh·x², sobre 200·L) no pasa de 2^104 y el discriminante de 2^112.
/// </remarks>
public sealed class RetimeMath : IEquatable<RetimeMath>
{
    /// <summary>Tope de |<see cref="ClipSpeed.Offset"/>| + duración y de <see cref="SpeedCurve.Span"/>: 2^44 ticks, unas 6,9 h.</summary>
    public const long MaxExtent = 1L << 44;

    /// <summary>Tope de |u| al evaluar: deja margen para extender un clip en una transición.</summary>
    public const long MaxArgument = 1L << 46;

    /// <summary>Tope de |objetivo| en <see cref="Inverse"/>.</summary>
    public const long MaxTarget = 1L << 50;

    /// <summary>Tope del numerador y del denominador de una velocidad constante.</summary>
    public const long MaxRateTerm = 1 << 20;

    public const int MinHundredths = 10;

    public const int MaxHundredths = 1000;

    public static Rational MinRate { get; } = new(1, 10);

    public static Rational MaxRate { get; } = new(100, 1);

    public static RetimeMath Identity { get; } = new(1, 1);

    private readonly long _num;
    private readonly long _den;

    // Curva: nudos en ticks, velocidad en centésimas y P[k] = 200·I(a[k]).
    private readonly long[]? _at;
    private readonly int[]? _h;
    private readonly Int128[]? _prefix;

    private RetimeMath(long num, long den)
    {
        _num = num;
        _den = den;
    }

    private RetimeMath(long[] at, int[] h, Int128[] prefix)
    {
        _at = at;
        _h = h;
        _prefix = prefix;
        _num = 1;
        _den = 1;
    }

    public bool IsCurve => _at is not null;

    public bool IsIdentity => _at is null && _num == 1 && _den == 1;

    /// <summary>Velocidad constante; de <see cref="MinRate"/> a <see cref="MaxRate"/> con términos hasta <see cref="MaxRateTerm"/>.</summary>
    public static RetimeMath Constant(Rational rate)
    {
        if (!IsValidRate(rate))
        {
            throw new ArgumentOutOfRangeException(nameof(rate), $"La velocidad {rate} está fuera de 1/10 a 100 o tiene términos demasiado grandes.");
        }

        return rate.Num == 1 && rate.Den == 1 ? Identity : new RetimeMath(rate.Num, rate.Den);
    }

    /// <summary>Curva con nudos en <c>⌊At·Span/<see cref="SpeedCurve.AtScale"/>⌋</c>.</summary>
    public static RetimeMath FromCurve(SpeedCurve curve)
    {
        ArgumentNullException.ThrowIfNull(curve);
        if (CurveProblem(curve) is { } problem)
        {
            throw new ArgumentException(problem, nameof(curve));
        }

        int n = curve.Points.Count;
        long[] at = new long[n];
        int[] h = new int[n];
        Int128[] prefix = new Int128[n];
        for (int k = 0; k < n; k++)
        {
            at[k] = (long)((Int128)curve.Points[k].At * curve.Span.Ticks / SpeedCurve.AtScale);
            h[k] = curve.Points[k].Hundredths;
            if (k > 0)
            {
                prefix[k] = prefix[k - 1] + ((Int128)(at[k] - at[k - 1]) * (h[k - 1] + h[k]));
            }
        }

        return new RetimeMath(at, h, prefix);
    }

    /// <summary>La de <paramref name="speed"/>; un congelado o una velocidad nula avanzan a 1×.</summary>
    public static RetimeMath Of(ClipSpeed? speed) =>
        speed is null || speed.Freeze ? Identity : speed.Curve is { } curve ? FromCurve(curve) : Constant(speed.Rate);

    public static bool IsValidRate(Rational rate) =>
        rate.Num > 0 && rate.Den > 0 && rate.Num <= MaxRateTerm && rate.Den <= MaxRateTerm
        && rate.Num * 10 >= rate.Den && rate.Num <= rate.Den * 100;

    /// <summary>El primer problema de la curva, en español; nulo si es válida.</summary>
    public static string? CurveProblem(SpeedCurve curve)
    {
        ArgumentNullException.ThrowIfNull(curve);
        List<SpeedPoint> points = curve.Points;
        if (curve.Span.Ticks <= 0 || curve.Span.Ticks > MaxExtent)
        {
            return "La curva de velocidad tiene una duración fuera de rango.";
        }

        if (points is null || points.Count < 2 || points.Count > SpeedCurve.MaxPoints)
        {
            return $"La curva de velocidad necesita de 2 a {SpeedCurve.MaxPoints} puntos.";
        }

        if (points.Exists(p => p is null))
        {
            return "La curva de velocidad tiene un punto vacío.";
        }

        if (points[0].At != 0 || points[^1].At != SpeedCurve.AtScale)
        {
            return $"La curva de velocidad debe empezar en 0 y terminar en {SpeedCurve.AtScale}.";
        }

        for (int i = 0; i < points.Count; i++)
        {
            if (points[i].Hundredths is < MinHundredths or > MaxHundredths)
            {
                return "La curva de velocidad tiene un punto fuera de 0,1× a 10×.";
            }

            if (i > 0 && points[i].At <= points[i - 1].At)
            {
                return "Los puntos de la curva de velocidad no están en orden estricto.";
            }
        }

        return null;
    }

    /// <summary><c>⌊I(u)⌋</c>.</summary>
    public long Floor(long u)
    {
        Eval e = Evaluate(u);
        return (long)FloorDiv(e.I, e.Den);
    }

    /// <summary><c>⌈I(u)⌉</c>.</summary>
    public long Ceiling(long u)
    {
        Eval e = Evaluate(u);
        return (long)CeilDiv(e.I, e.Den);
    }

    /// <summary><c>⌊I(u) + d·v(u)⌋</c>: sigue desde <paramref name="u"/> a la velocidad que tiene ahí.</summary>
    public long FloorExtended(long u, long d)
    {
        CheckArgument(d);
        Eval e = Evaluate(u);
        return (long)FloorDiv(e.I + (d * e.V), e.Den);
    }

    /// <summary>El menor <c>u</c> con <c>I(u) ≥ target</c>.</summary>
    public long Inverse(long target)
    {
        if (target is > MaxTarget or < -MaxTarget)
        {
            throw new ArgumentOutOfRangeException(nameof(target), target, "Objetivo fuera de rango.");
        }

        if (_at is null)
        {
            return (long)CeilDiv((Int128)target * _den, _num);
        }

        Int128 t200 = (Int128)target * 200;
        int last = _at.Length - 1;
        if (target <= 0)
        {
            return (long)CeilDiv((Int128)target * 100, _h![0]);
        }

        if (t200 > _prefix![last])
        {
            return _at[last] + (long)CeilDiv(t200 - _prefix[last], 2 * _h![last]);
        }

        // El primer tramo cuyo final alcanza el objetivo; su inicio queda debajo, así que tiene largo positivo.
        int k = 0;
        while (_prefix[k + 1] < t200)
        {
            k++;
        }

        Int128 length = _at[k + 1] - _at[k];
        Int128 b = length * _h![k];
        Int128 dh = _h[k + 1] - _h[k];
        Int128 r = (length * t200) - (length * _prefix[k]);
        Int128 x = r / (b + Sqrt((b * b) + (dh * r)));
        while ((2 * b * x) + (dh * x * x) < r)
        {
            x++;
        }

        while (x > 0 && (2 * b * (x - 1)) + (dh * (x - 1) * (x - 1)) >= r)
        {
            x--;
        }

        return _at[k] + (long)x;
    }

    /// <summary>Velocidad exacta en <paramref name="u"/> (continua por la derecha), como fracción.</summary>
    public (Int128 Num, Int128 Den) Rate(long u)
    {
        Eval e = Evaluate(u);
        return (e.V, e.Den);
    }

    public double RateAt(long u)
    {
        (Int128 num, Int128 den) = Rate(u);
        return (double)num / (double)den;
    }

    /// <summary>
    /// Menor y mayor velocidad en <c>[from, to]</c>. La curva es lineal a tramos, así que los extremos están en los bordes
    /// o en sus nudos; constante, las dos son la misma.
    /// </summary>
    public (double Min, double Max) RateRange(long from, long to)
    {
        if (to < from)
        {
            (from, to) = (to, from);
        }

        double first = RateAt(from);
        double last = RateAt(to);
        double min = Math.Min(first, last);
        double max = Math.Max(first, last);
        if (_at is null)
        {
            return (min, max);
        }

        for (int k = 0; k < _at.Length; k++)
        {
            if (_at[k] > from && _at[k] < to)
            {
                double rate = _h![k] / 100.0;
                min = Math.Min(min, rate);
                max = Math.Max(max, rate);
            }
        }

        return (min, max);
    }

    public bool Equals(RetimeMath? other) =>
        other is not null && (ReferenceEquals(this, other)
            || (_num == other._num && _den == other._den
                && (_at is null ? other._at is null
                    : other._at is not null && _at.AsSpan().SequenceEqual(other._at) && _h.AsSpan().SequenceEqual(other._h))));

    public override bool Equals(object? obj) => Equals(obj as RetimeMath);

    public override int GetHashCode() => _at is null ? HashCode.Combine(_num, _den) : HashCode.Combine(_at.Length, _at[^1], _h![0], _h[^1]);

    // I = I/Den y v = V/Den con el mismo denominador, así extender es sumar.
    private readonly record struct Eval(Int128 I, Int128 V, Int128 Den);

    private Eval Evaluate(long u)
    {
        CheckArgument(u);
        if (_at is null)
        {
            return new Eval((Int128)u * _num, _num, _den);
        }

        int last = _at.Length - 1;
        if (u <= 0)
        {
            return new Eval((Int128)u * 2 * _h![0], 2 * _h[0], 200);
        }

        if (u >= _at[last])
        {
            return new Eval(_prefix![last] + ((Int128)(u - _at[last]) * 2 * _h![last]), 2 * _h[last], 200);
        }

        int k = Array.BinarySearch(_at, u);
        k = k >= 0 ? k : ~k - 1;
        while (_at[k + 1] == _at[k])
        {
            k++;
        }

        Int128 length = _at[k + 1] - _at[k];
        Int128 x = u - _at[k];
        Int128 dh = _h![k + 1] - _h[k];
        Int128 i = (length * _prefix![k]) + (2 * length * _h[k] * x) + (dh * x * x);
        return new Eval(i, 2 * ((length * _h[k]) + (dh * x)), 200 * length);
    }

    private static void CheckArgument(long u)
    {
        if (u is > MaxArgument or < -MaxArgument)
        {
            throw new ArgumentOutOfRangeException(nameof(u), u, "Instante fuera del rango del mapeo de velocidad.");
        }
    }

    internal static Int128 FloorDiv(Int128 a, Int128 b)
    {
        Int128 q = a / b;
        return (a % b != 0 && (a < 0) != (b < 0)) ? q - 1 : q;
    }

    internal static Int128 CeilDiv(Int128 a, Int128 b) => -FloorDiv(-a, b);

    /// <summary>⌊√n⌋ para n ≥ 0.</summary>
    internal static Int128 Sqrt(Int128 n)
    {
        if (n < 2)
        {
            return n < 0 ? 0 : n;
        }

        // Newton desde una cota superior: decrece hasta ⌊√n⌋.
        int bits = 128 - (int)Int128.LeadingZeroCount(n);
        Int128 x = (Int128)1 << ((bits + 1) / 2);
        while (true)
        {
            Int128 y = (x + (n / x)) / 2;
            if (y >= x)
            {
                return x;
            }

            x = y;
        }
    }
}
