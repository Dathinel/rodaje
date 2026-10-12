namespace Rodaje.Audio.Metering;

/// <summary>
/// Ponderación K de ITU-R BS.1770-4 a 48 kHz para un canal: el estante de alta frecuencia (+4 dB) y el pasa altos
/// RLB, con los coeficientes de la norma.
/// </summary>
public struct KWeightingFilter
{
    private double _s1, _s2, _h1, _h2;

    public double Next(double x)
    {
        double y = (1.53512485958697 * x) + _s1;
        _s1 = (-2.69169618940638 * x) + (1.69065929318241 * y) + _s2;
        _s2 = (1.19839281085285 * x) - (0.73248077421585 * y);
        double z = y + _h1;
        _h1 = (-2.0 * y) + (1.99004745483398 * z) + _h2;
        _h2 = y - (0.99007225036621 * z);
        return z;
    }

    public void Reset() => this = default;
}

/// <summary>Cuentas de sonoridad de EBU R128 / ITU-R BS.1770-4 sobre potencias ponderadas K (suma de los canales).</summary>
public static class Loudness
{
    /// <summary>Puerta absoluta: −70 LUFS.</summary>
    public const double AbsoluteGateLufs = -70;

    public static double ToLufs(double power) => power > 0 ? -0.691 + (10 * Math.Log10(power)) : double.NegativeInfinity;

    public static double ToPower(double lufs) => Math.Pow(10, (lufs + 0.691) / 10);

    /// <summary>
    /// Sonoridad de los bloques que pasan la puerta absoluta y la relativa (<paramref name="relativeLu"/> bajo la media de
    /// los que pasaron la absoluta): −10 LU para la integrada, −20 para el rango.
    /// </summary>
    /// <remarks>Si <paramref name="kept"/> no es nulo, recibe la sonoridad de cada bloque que pasó las dos puertas.</remarks>
    public static double Gated(IReadOnlyList<double> powers, double relativeLu, List<double>? kept = null)
    {
        ArgumentNullException.ThrowIfNull(powers);
        double absolute = ToPower(AbsoluteGateLufs);
        double sum = 0;
        int count = 0;
        for (int i = 0; i < powers.Count; i++)
        {
            if (powers[i] > absolute)
            {
                sum += powers[i];
                count++;
            }
        }

        if (count == 0)
        {
            return double.NegativeInfinity;
        }

        double gate = Math.Max(absolute, sum / count * Math.Pow(10, relativeLu / 10));
        sum = 0;
        count = 0;
        for (int i = 0; i < powers.Count; i++)
        {
            if (powers[i] > gate)
            {
                sum += powers[i];
                count++;
                kept?.Add(ToLufs(powers[i]));
            }
        }

        return count == 0 ? double.NegativeInfinity : ToLufs(sum / count);
    }
}

/// <summary>
/// Sonoridad integrada en vivo: recibe la potencia de un bloque de 400 ms cada 100 ms (lo que suena) y aplica las puertas
/// de BS.1770-4. Se usa desde un solo hilo.
/// </summary>
public sealed class LoudnessIntegrator
{
    private readonly List<double> _blocks = [];

    public int Blocks => _blocks.Count;

    public double IntegratedLufs => Loudness.Gated(_blocks, -10);

    public void Add(double power)
    {
        if (double.IsFinite(power) && power >= 0)
        {
            _blocks.Add(power);
        }
    }

    public void Reset() => _blocks.Clear();
}
