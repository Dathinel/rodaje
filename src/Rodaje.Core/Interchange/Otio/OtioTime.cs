using Rodaje.Core.Timing;

namespace Rodaje.Core.Interchange.Otio;

/// <summary>Normalización de tasas y conversión de RationalTime a Time para OpenTimelineIO.</summary>
internal static class OtioTime
{
    public static Rational NormalizeRate(double rate, InterchangeReport? report)
    {
        if (double.IsNaN(rate) || double.IsInfinity(rate) || rate <= 0 || rate < 0.2 || rate > 1000.0)
        {
            throw new InterchangeException($"Tasa de cuadros inválida: {rate}. Debe estar entre 1/5 y 1000.");
        }

        if (Math.Abs(rate - (24000.0 / 1001.0)) < 1e-3 || Math.Abs(rate - 23.976) < 1e-3)
        {
            report?.Add(IssueSeverity.Info, IssueKind.Rounding, "Tasa de cuadros normalizada a 24000/1001.");
            return new Rational(24000, 1001);
        }

        if (Math.Abs(rate - (30000.0 / 1001.0)) < 1e-3 || Math.Abs(rate - 29.97) < 1e-3)
        {
            report?.Add(IssueSeverity.Info, IssueKind.Rounding, "Tasa de cuadros normalizada a 30000/1001.");
            return new Rational(30000, 1001);
        }

        if (Math.Abs(rate - (60000.0 / 1001.0)) < 1e-3 || Math.Abs(rate - 59.94) < 1e-3)
        {
            report?.Add(IssueSeverity.Info, IssueKind.Rounding, "Tasa de cuadros normalizada a 60000/1001.");
            return new Rational(60000, 1001);
        }

        double round = Math.Round(rate);
        if (Math.Abs(rate - round) < 1e-6)
        {
            return new Rational((long)round, 1);
        }

        double err1 = Math.Abs(rate - round);
        double num1001 = Math.Round(rate * 1001.0);
        double err1001 = Math.Abs(rate - (num1001 / 1001.0));

        if (err1 <= err1001)
        {
            return new Rational((long)round, 1);
        }

        long n = (long)num1001;
        if (n % 1001 == 0)
        {
            return new Rational(n / 1001, 1);
        }

        return new Rational(n, 1001);
    }

    public static Time RationalTimeToTime(double value, double rate, InterchangeReport? report)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > 1e14)
        {
            throw new InterchangeException($"Valor de tiempo no finito o fuera de rango: {value}.");
        }

        Rational r = NormalizeRate(rate, report);
        long ticks;
        try
        {
            checked
            {
                double exactTicks = value * Time.TicksPerSecond * r.Den / r.Num;
                ticks = (long)Math.Round(exactTicks);
            }
        }
        catch (OverflowException ex)
        {
            throw new InterchangeException($"Desbordamiento al calcular tiempo con valor {value}.", ex);
        }

        double backValue = (double)ticks * r.Num / (Time.TicksPerSecond * r.Den);
        if (Math.Abs(value - backValue) >= 1.0)
        {
            report?.Add(IssueSeverity.Approximate, IssueKind.Rounding, "El redondeo a tick movió un cuadro o más.");
        }

        return Time.FromTicks(ticks);
    }

    public static (double Value, double Rate) TimeToRationalTime(Time time, Rational rate)
    {
        double frames = (double)time.Ticks * rate.Num / (Time.TicksPerSecond * rate.Den);
        double rounded = Math.Round(frames);
        double val = Math.Abs(frames - rounded) < 1e-7 ? rounded : frames;
        return (val, rate.ToDouble());
    }

    public static Rational RationalFromDouble(double scalar)
    {
        if (double.IsNaN(scalar) || double.IsInfinity(scalar) || scalar <= 0)
        {
            return new Rational(1, 1);
        }

        double round = Math.Round(scalar);
        if (Math.Abs(scalar - round) < 1e-6)
        {
            return new Rational((long)round, 1);
        }

        int[] denominators = [2, 3, 4, 5, 8, 10, 100, 1000, 1001];
        int bestDenom = 1;
        long bestNum = (long)round;
        double bestErr = Math.Abs(scalar - round);

        foreach (int d in denominators)
        {
            long n = (long)Math.Round(scalar * d);
            double err = Math.Abs(scalar - ((double)n / d));
            if (err < bestErr)
            {
                bestErr = err;
                bestDenom = d;
                bestNum = n;
            }
        }

        long gcd = Gcd(bestNum, bestDenom);
        return new Rational(bestNum / gcd, bestDenom / gcd);
    }

    private static long Gcd(long a, long b)
    {
        while (b != 0)
        {
            long t = b;
            b = a % b;
            a = t;
        }

        return Math.Abs(a);
    }
}
