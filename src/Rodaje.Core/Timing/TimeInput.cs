using System.Globalization;

namespace Rodaje.Core.Timing;

/// <summary>
/// Lee un tiempo escrito a mano para «Ir a tiempo»: <c>1:23</c>, <c>1:23.5</c>, <c>0:01:23,5</c>, <c>83</c> (segundos),
/// <c>2m</c>, <c>120f</c> (fotogramas) y, con <c>+</c> o <c>-</c> delante, un salto desde el cursor (<c>+10</c>,
/// <c>-2s</c>, <c>+24f</c>). El resultado se lleva al inicio de su fotograma.
/// </summary>
public static class TimeInput
{
    /// <summary>
    /// Entiende <paramref name="text"/> y deja en <paramref name="result"/> el tiempo absoluto, a partir de
    /// <paramref name="current"/> si es relativo y nunca antes de cero. <c>false</c> si el texto no es un tiempo.
    /// </summary>
    public static bool TryParse(string? text, Rational frameRate, Time current, out Time result)
    {
        result = Time.Zero;
        if (frameRate.Num <= 0 || frameRate.Den <= 0 || string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        ReadOnlySpan<char> span = text.AsSpan().Trim();
        int sign = 0;
        if (span[0] is '+' or '-')
        {
            sign = span[0] == '+' ? 1 : -1;
            span = span[1..].TrimStart();
            if (span.IsEmpty)
            {
                return false;
            }
        }

        if (!TryParseSpan(span, frameRate, out Time offset))
        {
            return false;
        }

        Time absolute = sign == 0 ? offset : current + (sign < 0 ? -offset : offset);
        if (absolute < Time.Zero)
        {
            absolute = Time.Zero;
        }

        result = absolute.SnapToFrame(frameRate);
        return true;
    }

    private static bool TryParseSpan(ReadOnlySpan<char> span, Rational frameRate, out Time time)
    {
        time = Time.Zero;
        if (span.Length == 0)
        {
            return false;
        }

        char last = char.ToLowerInvariant(span[^1]);
        if (last is 'f' or 's' or 'm' or 'h')
        {
            ReadOnlySpan<char> number = span[..^1].TrimEnd();
            if (!TryNumber(number, out double value))
            {
                return false;
            }

            if (last == 'f')
            {
                time = Time.FromFrames((long)Math.Round(value), frameRate);
                return true;
            }

            return TrySeconds(last switch { 's' => value, 'm' => value * 60, _ => value * 3600 }, out time);
        }

        // h:mm:ss.fff, m:ss.f o segundos sueltos; el último campo puede llevar decimales con punto o coma.
        double total = 0;
        int fields = 0;
        while (true)
        {
            int colon = span.IndexOf(':');
            ReadOnlySpan<char> field = colon < 0 ? span : span[..colon];
            bool lastField = colon < 0;
            if (field.IsEmpty || fields >= 3)
            {
                return false;
            }

            if (lastField)
            {
                if (!TryNumber(field, out double seconds))
                {
                    return false;
                }

                total = total * 60 + seconds;
                break;
            }

            if (!int.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out int whole))
            {
                return false;
            }

            total = total * 60 + whole;
            fields++;
            span = span[(colon + 1)..];
        }

        return TrySeconds(total, out time);
    }

    private static bool TrySeconds(double seconds, out Time time)
    {
        time = seconds <= MaxValue ? Time.FromSeconds(seconds) : Time.Zero;
        return seconds <= MaxValue;
    }

    private static bool TryNumber(ReadOnlySpan<char> text, out double value)
    {
        value = 0;
        if (text.IsEmpty || text[0] is '+' or '-')
        {
            return false;
        }

        Span<char> normalized = stackalloc char[text.Length];
        text.CopyTo(normalized);
        normalized.Replace(',', '.');
        // Tope de mil millones (más de treinta años en segundos): por encima, los ticks no caben en un long.
        return double.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value)
            && double.IsFinite(value)
            && value <= MaxValue;
    }

    private const double MaxValue = 1_000_000_000;
}
