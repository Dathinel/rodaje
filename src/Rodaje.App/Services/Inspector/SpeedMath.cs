using System.Globalization;
using Rodaje.App.Services.Format;
using Rodaje.Core.Timing;

namespace Rodaje.App.Services.Inspector;

/// <summary>Cuentas y textos de la pestaña Velocidad: deslizador logarítmico, velocidad para una duración, «1,5×» y «3,3 s».</summary>
public static class SpeedMath
{
    /// <summary>El deslizador va de 0 (0,1×) a este valor (100×): tres décadas, 1× en un tercio y 10× en dos.</summary>
    public const double SliderMax = 1000;

    // Cerca de un imán (unos ±4 %) el deslizador se queda en él.
    private const double DetentReach = 6;

    // Con InvariantGlobalization no hay culturas: pedir «es-ES» lanza al abrir la app; el formato común lo resuelve.
    private static readonly NumberFormatInfo Spanish = SpanishNumbers.Format;

    public static double ToSlider(double rate) =>
        Math.Clamp((Math.Log10(Math.Clamp(rate, 0.1, 100)) + 1) / 3 * SliderMax, 0, SliderMax);

    /// <summary>La velocidad de una posición del deslizador: redondeada a centésimas (décimas desde 10×) o en un imán cercano.</summary>
    public static Rational FromSlider(double position)
    {
        position = Math.Clamp(position, 0, SliderMax);
        foreach (Rational detent in SpeedTabValues.Detents)
        {
            if (Math.Abs(ToSlider(detent.ToDouble()) - position) <= DetentReach)
            {
                return detent;
            }
        }

        return Round(Math.Pow(10, position / SliderMax * 3 - 1));
    }

    /// <summary>A centésimas por debajo de 10× y a décimas desde ahí, dentro de 0,1×–100×.</summary>
    public static Rational Round(double rate)
    {
        rate = Math.Clamp(rate, 0.1, 100);
        return rate < 10 ? new Rational((long)Math.Round(rate * 100), 100) : new Rational((long)Math.Round(rate * 10), 10);
    }

    /// <summary>
    /// La velocidad con la que un tramo de origen de <paramref name="source"/> dura <paramref name="duration"/> en la pista,
    /// dentro de 0,1×–100×: la fracción más cercana con términos que admite el núcleo.
    /// </summary>
    public static Rational RateForDuration(Time source, Time duration)
    {
        if (source <= Time.Zero || duration <= Time.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "La duración y el tramo tienen que ser positivos.");
        }

        Int128 a = source.Ticks;
        Int128 b = duration.Ticks;
        if (a * 10 < b)
        {
            return RetimeMath.MinRate;
        }

        return a > b * 100 ? RetimeMath.MaxRate : Approximate(a, b, RetimeMath.MaxRateTerm);
    }

    /// <summary>La fracción más cercana a <paramref name="a"/>/<paramref name="b"/> con numerador y denominador hasta <paramref name="max"/>.</summary>
    public static Rational Approximate(Int128 a, Int128 b, long max)
    {
        // Fracciones continuas: el último convergente que cabe, o el semiconvergente que cabe si queda más cerca.
        Int128 p0 = 0, q0 = 1, p1 = 1, q1 = 0;
        Int128 n = a, d = b;
        while (d != 0)
        {
            Int128 t = n / d;
            Int128 p2 = p0 + t * p1, q2 = q0 + t * q1;
            if (p2 > max || q2 > max)
            {
                Int128 k = Int128.Min(p1 == 0 ? max : (max - p0) / p1, q1 == 0 ? max : (max - q0) / q1);
                if (k > 0)
                {
                    Int128 pk = p0 + k * p1, qk = q0 + k * q1;
                    if (q1 == 0 || Int128.Abs(pk * b - a * qk) * q1 < Int128.Abs(p1 * b - a * q1) * qk)
                    {
                        return new Rational((long)pk, (long)qk);
                    }
                }

                break;
            }

            (p0, q0, p1, q1) = (p1, q1, p2, q2);
            (n, d) = (d, n - t * d);
        }

        return new Rational((long)p1, (long)q1);
    }

    /// <summary>«1,5×», «0,25×», «13,7×» o «100×».</summary>
    public static string FormatRate(double rate) =>
        rate.ToString(rate < 10 ? "0.0#" : "0.#", Spanish) + "×";

    public static string FormatRate(Rational rate) => FormatRate(rate.ToDouble());

    /// <summary>«3,3 s» o, desde un minuto, «1:05,3».</summary>
    public static string FormatDuration(Time duration)
    {
        double seconds = Math.Round(duration.ToSeconds(), 1);
        if (seconds < 60)
        {
            return seconds.ToString("0.0", Spanish) + " s";
        }

        int minutes = (int)(seconds / 60);
        return minutes.ToString(CultureInfo.InvariantCulture) + ":" + (seconds - minutes * 60).ToString("00.0", Spanish);
    }

    /// <summary>Lee «1,5×», «1.5», «x2» o «150 %»; fuera de 0,1×–100× se lleva al borde. Nulo si no es un número positivo.</summary>
    public static Rational? ParseRate(string? text)
    {
        if (text is null)
        {
            return null;
        }

        string s = text.Trim().Replace(" ", "", StringComparison.Ordinal);
        bool percent = s.EndsWith('%');
        s = s.Trim('×', 'x', 'X', '%');
        if (!TryNumber(s, out double value) || value <= 0)
        {
            return null;
        }

        value = Math.Clamp(percent ? value / 100 : value, 0.1, 100);
        return new Rational((long)Math.Round(value * 10_000), 10_000);
    }

    /// <summary>Lee «3,3», «3,3 s», «1:05,3» o «0:01:05»; nulo si no es una duración positiva.</summary>
    public static Time? ParseDuration(string? text)
    {
        if (text is null)
        {
            return null;
        }

        string s = text.Trim();
        if (s.EndsWith('s') || s.EndsWith('S'))
        {
            s = s[..^1].TrimEnd();
        }

        double seconds = 0;
        string[] parts = s.Split(':');
        if (parts.Length > 3)
        {
            return null;
        }

        for (int i = 0; i < parts.Length; i++)
        {
            bool last = i == parts.Length - 1;
            if (!TryNumber(parts[i], out double part) || part < 0 || (!last && part != Math.Floor(part)))
            {
                return null;
            }

            seconds = seconds * 60 + part;
        }

        return seconds > 0 && seconds < 86_400 ? Time.FromSeconds(seconds) : null;
    }

    private static bool TryNumber(string text, out double value) =>
        double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)
        && double.IsFinite(value);
}
