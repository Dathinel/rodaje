using System.Globalization;
using Rodaje.Core.Interchange.Xml;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.Core.Interchange.Fcpxml;

/// <summary>Conversiones y formato de tiempo racional para FCPXML 1.10 (D-062).</summary>
internal static class FcpxmlTime
{
    public static Time AssetStart(MediaRef? media, Rational fallbackRate) =>
        media?.Timecode is { } text && SmpteTimecode.TryParse(text, media.FrameRate ?? fallbackRate, out var tc)
            ? tc.ToTime(media.FrameRate ?? fallbackRate) : Time.Zero;

    public static Time Scale(Time value, long numerator, long denominator)
    {
        if (denominator == 0) throw new InterchangeException("Tramo de tiempo de longitud cero en FCPXML.");
        Int128 product = (Int128)value.Ticks * numerator;
        Int128 result = product / denominator;
        Int128 remainder = product % denominator;
        if (Int128.Abs(remainder) * 2 >= Int128.Abs(denominator))
            result += Int128.Sign(product) * Math.Sign(denominator);
        try { return Time.FromTicks(checked((long)result)); }
        catch (OverflowException) { throw new InterchangeException("Mapeo de tiempo fuera de rango en FCPXML."); }
    }

    public static Time LocalStart(Clip clip, Project project, Rational rate) =>
        clip.SourceIn + (clip.IsCompound ? Time.Zero : AssetStart(project.FindMedia(clip.MediaId), rate));

    // El mapa exportado es lineal: las curvas se declaran como velocidad media en Fidelity.
    public static Time SourceAt(Clip clip, Time elapsed, Time assetStart)
    {
        if (clip.Speed?.Freeze == true) return assetStart + clip.SourceIn;
        if (clip.Speed is null) return assetStart + clip.SourceIn + elapsed;
        Time delta = Scale(elapsed, clip.SourceDuration.Ticks, clip.Duration.Ticks);
        return assetStart + (clip.Speed.Reverse ? clip.SourceOut - delta : clip.SourceIn + delta);
    }

    public static Time MapSource(XmlNode node, Time adjusted) => Map(node, adjusted, inverse: false);

    public static Time ParentElapsed(XmlNode parent, Time local, bool contained = false)
    {
        Time adjusted = contained ? local : Map(parent, local, inverse: true);
        return adjusted - ParseTime(parent["start"]);
    }

    private static Time Map(XmlNode node, Time value, bool inverse)
    {
        var points = node.Element("timeMap")?.Elements("timept").ToList();
        if (points is null || points.Count == 0) return value;
        Time X(XmlNode point) => ParseTime(point[inverse ? "value" : "time"] ?? (inverse ? null : point["start"]));
        Time Y(XmlNode point) => ParseTime(point[inverse ? "time" : "value"] ?? (inverse ? point["start"] : null));
        if (points.Count == 1) return inverse ? ParseTime(node["start"]) : Y(points[0]);

        for (int i = 1; i < points.Count; i++)
        {
            Time a = X(points[i - 1]);
            Time b = X(points[i]);
            if (a == b)
            {
                if (inverse && value == a) return ParseTime(node["start"]);
                continue;
            }
            if ((value >= Time.Min(a, b) && value <= Time.Max(a, b)) || i == points.Count - 1 || (i == 1 && value < a && a < b))
                return Y(points[i - 1]) + Scale(value - a, (Y(points[i]) - Y(points[i - 1])).Ticks, (b - a).Ticks);
        }
        return Y(points[0]);
    }

    public static long Gcd(long a, long b)
    {
        while (b != 0)
        {
            long temp = b;
            b = a % b;
            a = temp;
        }

        return a;
    }

    public static string FormatTime(Time time)
    {
        long ticks = time.Ticks;
        if (ticks == 0)
        {
            return "0s";
        }

        long denBase = Time.TicksPerSecond;
        long g = Gcd(Math.Abs(ticks), denBase);
        long num = ticks / g;
        long den = denBase / g;
        return den == 1 ? $"{num}s" : $"{num}/{den}s";
    }

    public static string FormatFrameDuration(Rational rate)
    {
        if (rate.Num <= 0 || rate.Den <= 0)
        {
            return "1/30s";
        }

        long num = rate.Den;
        long den = rate.Num;
        long g = Gcd(num, den);
        num /= g;
        den /= g;
        return den == 1 ? $"{num}s" : $"{num}/{den}s";
    }

    public static Time ParseTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Time.Zero;
        }

        ReadOnlySpan<char> span = text.AsSpan().Trim();
        if (span.EndsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            span = span[..^1];
        }

        int slash = span.IndexOf('/');
        if (slash >= 0)
        {
            ReadOnlySpan<char> numSpan = span[..slash];
            ReadOnlySpan<char> denSpan = span[(slash + 1)..];
            if (!long.TryParse(numSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out long num)
                || !long.TryParse(denSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out long den))
            {
                throw new InterchangeException($"Tiempo no válido en FCPXML: «{text}».");
            }

            if (den == 0)
            {
                throw new InterchangeException($"División por cero en el tiempo de FCPXML: «{text}».");
            }

            try
            {
                Int128 numerator = (Int128)num * Time.TicksPerSecond;
                long ticks = checked((long)(numerator / den));
                Int128 rem = numerator % den;
                if (rem != 0 && Math.Abs((long)(rem * 2)) >= Math.Abs(den))
                {
                    ticks += Math.Sign(num) * Math.Sign(den);
                }

                return Time.FromTicks(ticks);
            }
            catch (OverflowException)
            {
                throw new InterchangeException($"Tiempo fuera de rango en FCPXML: «{text}».");
            }
        }

        if (span.IndexOf('.') >= 0)
        {
            if (!double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
                || !double.IsFinite(seconds))
            {
                throw new InterchangeException($"Tiempo decimal no válido en FCPXML: «{text}».");
            }

            try
            {
                long ticks = checked((long)Math.Round(seconds * Time.TicksPerSecond));
                return Time.FromTicks(ticks);
            }
            catch (OverflowException)
            {
                throw new InterchangeException($"Tiempo fuera de rango en FCPXML: «{text}».");
            }
        }

        if (!long.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out long sec))
        {
            throw new InterchangeException($"Tiempo no válido en FCPXML: «{text}».");
        }

        try
        {
            long ticks = checked(sec * Time.TicksPerSecond);
            return Time.FromTicks(ticks);
        }
        catch (OverflowException)
        {
            throw new InterchangeException($"Tiempo fuera de rango en FCPXML: «{text}».");
        }
    }

    public static Time ParseDuration(string? text)
    {
        Time time = ParseTime(text);
        if (time < Time.Zero)
        {
            throw new InterchangeException($"La duración no puede ser negativa en FCPXML: «{text}».");
        }

        return time;
    }

    public static Rational ParseFrameDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new Rational(30, 1);
        }

        ReadOnlySpan<char> span = text.AsSpan().Trim();
        if (span.EndsWith("s", StringComparison.OrdinalIgnoreCase))
        {
            span = span[..^1];
        }

        int slash = span.IndexOf('/');
        if (slash >= 0)
        {
            ReadOnlySpan<char> numSpan = span[..slash];
            ReadOnlySpan<char> denSpan = span[(slash + 1)..];
            if (!long.TryParse(numSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out long num)
                || !long.TryParse(denSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out long den))
            {
                throw new InterchangeException($"frameDuration no válida en FCPXML: «{text}».");
            }

            if (num <= 0 || den <= 0)
            {
                throw new InterchangeException($"frameDuration debe ser positiva en FCPXML: «{text}».");
            }

            long g = Gcd(num, den);
            num /= g;
            den /= g;
            return new Rational((int)den, (int)num);
        }

        if (double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
            && double.IsFinite(seconds) && seconds > 0)
        {
            double fps = 1.0 / seconds;
            if (Math.Abs(fps - 29.97002997) < 0.05) return Rational.Ntsc30;
            if (Math.Abs(fps - 23.97602398) < 0.05) return Rational.Ntsc24;
            if (Math.Abs(fps - 59.94005994) < 0.05) return Rational.Ntsc60;
            int rounded = (int)Math.Round(fps);
            if (Math.Abs(fps - rounded) < 1e-4) return new Rational(rounded, 1);
            return new Rational((int)Math.Round(fps * 1000), 1000);
        }

        if (int.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) && s > 0)
        {
            return new Rational(1, s);
        }

        throw new InterchangeException($"frameDuration no válida en FCPXML: «{text}».");
    }

    public static Rational RationalFromTicks(long numTicks, long denTicks)
    {
        if (denTicks == 0)
        {
            throw new InterchangeException("Tasa con denominador 0 no válida en FCPXML.");
        }

        long g = Gcd(Math.Abs(numTicks), Math.Abs(denTicks));
        long num = numTicks / g;
        long den = denTicks / g;
        if (den < 0)
        {
            num = -num;
            den = -den;
        }

        return new Rational(num, den);
    }
}
