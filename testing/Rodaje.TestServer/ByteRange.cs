using System.Globalization;

namespace Rodaje.TestServer;

/// <summary>Un rango de bytes inclusivo ya resuelto contra el largo del archivo.</summary>
internal readonly record struct ByteRange(long First, long Last)
{
    public long Length => Last - First + 1;

    /// <summary>Resultado de leer una cabecera <c>Range</c>.</summary>
    internal enum ParseResult
    {
        /// <summary>No hay rango o no se entiende: se manda el archivo entero (RFC 9110 permite ignorarlo).</summary>
        Ignore,

        /// <summary>Un rango válido.</summary>
        Satisfiable,

        /// <summary>Rango bien escrito pero fuera del archivo: 416.</summary>
        Unsatisfiable,
    }

    /// <summary>
    /// Lee <c>bytes=a-b</c>, <c>bytes=a-</c> y <c>bytes=-n</c>. Varios rangos separados por comas no se
    /// atienden (se ignoran y va el archivo entero): un descargador no los necesita.
    /// </summary>
    public static ParseResult TryParse(string? header, long length, out ByteRange range)
    {
        range = default;
        if (string.IsNullOrWhiteSpace(header))
        {
            return ParseResult.Ignore;
        }

        var text = header.Trim();
        const string prefix = "bytes=";
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || text.Contains(',', StringComparison.Ordinal))
        {
            return ParseResult.Ignore;
        }

        var spec = text[prefix.Length..].Trim();
        var dash = spec.IndexOf('-', StringComparison.Ordinal);
        if (dash < 0)
        {
            return ParseResult.Ignore;
        }

        var left = spec[..dash].Trim();
        var right = spec[(dash + 1)..].Trim();

        if (left.Length == 0)
        {
            // Sufijo: los últimos n bytes.
            if (!TryParseNumber(right, out var suffix))
            {
                return ParseResult.Ignore;
            }

            if (suffix == 0 || length == 0)
            {
                return ParseResult.Unsatisfiable;
            }

            var count = Math.Min(suffix, length);
            range = new ByteRange(length - count, length - 1);
            return ParseResult.Satisfiable;
        }

        if (!TryParseNumber(left, out var first))
        {
            return ParseResult.Ignore;
        }

        long last;
        if (right.Length == 0)
        {
            last = length - 1;
        }
        else if (!TryParseNumber(right, out last) || last < first)
        {
            return ParseResult.Ignore;
        }

        if (first >= length)
        {
            return ParseResult.Unsatisfiable;
        }

        range = new ByteRange(first, Math.Min(last, length - 1));
        return ParseResult.Satisfiable;
    }

    private static bool TryParseNumber(string text, out long value) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
