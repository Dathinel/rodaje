using Rodaje.Engine.Timeline.Retime;

namespace Rodaje.Engine.Offline;

/// <summary>
/// Cambio de frecuencia de la mezcla (48 kHz) a la de la salida con un sinc enventanado (Kaiser), el mismo diseño de
/// <see cref="VarispeedResampler"/> pero con las fases exactas de la razón: la muestra <c>n</c> de la salida es el
/// instante <c>n · 48 000 / salida</c> de la mezcla, así que no hay deriva y cualquier muestra sale igual se empiece
/// donde se empiece.
/// </summary>
/// <remarks>
/// Con una razón de más de <see cref="MaxPhases"/> fases (frecuencias raras) la fase se redondea a 1/<see cref="MaxPhases"/>.
/// Al bajar de frecuencia el corte baja con ella (0,91 de la nueva mitad) y el filtro se alarga en proporción.
/// </remarks>
internal sealed class RateConverter
{
    public const int MaxPhases = 4096;

    private const int BaseTaps = 32;
    private const double Cutoff = 0.5 * 0.91;
    private const double Beta = 8.6;

    private readonly long _p;
    private readonly long _q;
    private readonly int _phases;
    private readonly float[] _table;

    /// <param name="inputRate">Frecuencia de lo que entra (la mezcla).</param>
    /// <param name="outputRate">Frecuencia de lo que sale.</param>
    public RateConverter(int inputRate, int outputRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputRate);
        long g = Gcd(inputRate, outputRate);
        _p = inputRate / g;
        _q = outputRate / g;
        double ratio = (double)inputRate / outputRate;
        Taps = (int)Math.Ceiling(BaseTaps * Math.Max(1, ratio) / 8) * 8;
        _phases = (int)Math.Min(_q, MaxPhases);
        _table = Build(Taps, _phases, Cutoff / Math.Max(1, ratio));
    }

    public int Taps { get; }

    /// <summary>Primera muestra de la entrada que necesita la salida <paramref name="output"/>.</summary>
    public long FirstInput(long output) => Position(output).Whole - (Taps / 2) + 1;

    /// <summary>Última muestra de la entrada (incluida) que necesita la salida <paramref name="output"/>.</summary>
    public long LastInput(long output) => Position(output).Whole + (Taps / 2) + 1;

    /// <summary>
    /// Llena <paramref name="left"/> y <paramref name="right"/> con las salidas desde <paramref name="output"/>, leyendo la
    /// entrada de <paramref name="inLeft"/>/<paramref name="inRight"/>, cuya posición 0 es la muestra <paramref name="inputBase"/>.
    /// </summary>
    public void Convert(long output, Span<float> left, Span<float> right, ReadOnlySpan<float> inLeft, ReadOnlySpan<float> inRight, long inputBase)
    {
        int half = Taps / 2;
        for (int i = 0; i < left.Length; i++)
        {
            (long whole, int phase) = Position(output + i);
            int at = (int)(whole - half + 1 - inputBase);
            (left[i], right[i]) = VectorMath.Dot2(_table.AsSpan(phase * Taps, Taps), inLeft.Slice(at, Taps), inRight.Slice(at, Taps));
        }
    }

    /// <summary>La muestra entera de la entrada y la fase de la fracción para la salida <paramref name="output"/>.</summary>
    private (long Whole, int Phase) Position(long output)
    {
        Int128 scaled = (Int128)output * _p;
        long whole = (long)Int128.DivRem(scaled, _q).Quotient;
        long rest = (long)(scaled - ((Int128)whole * _q));
        if (rest < 0)
        {
            whole--;
            rest += _q;
        }

        if (_phases == _q)
        {
            return (whole, (int)rest);
        }

        int phase = (int)Math.Round((double)rest / _q * _phases);
        return phase == _phases ? (whole + 1, 0) : (whole, phase);
    }

    private static float[] Build(int taps, int phases, double fc)
    {
        int half = taps / 2;
        var table = new float[phases * taps];
        double norm = BesselI0(Beta);
        var row = new double[taps];
        for (int p = 0; p < phases; p++)
        {
            double fraction = (double)p / phases;
            double sum = 0;
            for (int j = 0; j < taps; j++)
            {
                double x = j - half + 1 - fraction;
                double t = x / half;
                double window = Math.Abs(t) >= 1 ? 0 : BesselI0(Beta * Math.Sqrt(1 - (t * t))) / norm;
                double arg = 2 * fc * x;
                double sinc = Math.Abs(arg) < 1e-12 ? 1 : Math.Sin(Math.PI * arg) / (Math.PI * arg);
                row[j] = sinc * window;
                sum += row[j];
            }

            for (int j = 0; j < taps; j++)
            {
                table[(p * taps) + j] = (float)(row[j] / sum);
            }
        }

        return table;
    }

    private static double BesselI0(double x)
    {
        double sum = 1;
        double term = 1;
        double half = x / 2;
        for (int k = 1; k < 64; k++)
        {
            term *= half / k;
            double add = term * term;
            sum += add;
            if (add < sum * 1e-17)
            {
                break;
            }
        }

        return sum;
    }

    private static long Gcd(long a, long b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }
}
