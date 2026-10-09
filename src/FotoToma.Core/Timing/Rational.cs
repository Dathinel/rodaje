namespace FotoToma.Core.Timing;

/// <summary>
/// Fracción positiva y normalizada (numerador/denominador), usada para las tasas de cuadros.
/// 29,97 fps se representa exacto como 30000/1001 en lugar de un double que arrastra error.
/// </summary>
public readonly record struct Rational
{
    public long Num { get; }
    public long Den { get; }

    public Rational(long num, long den)
    {
        if (num <= 0 || den <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(num), $"Una tasa debe ser positiva y se recibió {num}/{den}.");
        }

        long g = Gcd(num, den);
        Num = num / g;
        Den = den / g;
    }

    /// <summary>23,976 fps (24000/1001).</summary>
    public static readonly Rational Ntsc24 = new(24000, 1001);
    public static readonly Rational Fps24 = new(24, 1);
    public static readonly Rational Fps25 = new(25, 1);
    /// <summary>29,97 fps (30000/1001).</summary>
    public static readonly Rational Ntsc30 = new(30000, 1001);
    public static readonly Rational Fps30 = new(30, 1);
    public static readonly Rational Fps50 = new(50, 1);
    /// <summary>59,94 fps (60000/1001).</summary>
    public static readonly Rational Ntsc60 = new(60000, 1001);
    public static readonly Rational Fps60 = new(60, 1);

    public double ToDouble() => (double)Num / Den;

    public override string ToString() => Den == 1 ? $"{Num}" : $"{Num}/{Den}";

    private static long Gcd(long a, long b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }
}
