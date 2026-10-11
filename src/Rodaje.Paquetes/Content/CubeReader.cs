using System.Globalization;
using System.Text;

namespace Rodaje.Paquetes.Content;

/// <summary>Lector de <c>.cube</c> (Adobe Cube LUT 1.0, solo 3D) sobre los bytes, sin pasar por <c>string</c>.</summary>
internal static class CubeReader
{
    private static readonly double[] Pow10 = BuildPow10();

    public static LutTable Read(Stream stream)
    {
        byte[] data = ReadAll(stream, out int length);
        ReadOnlySpan<byte> text = data.AsSpan(0, length);
        if (text.StartsWith("﻿"u8))
        {
            text = text[3..];
        }

        string? title = null;
        int size = 0;
        float[] min = [0, 0, 0];
        float[] max = [1, 1, 1];
        bool hasMin = false, hasMax = false;
        float[]? rgb = null;
        int count = 0, total = 0, lineNumber = 0;
        Span<float> triple = stackalloc float[3];

        int pos = 0;
        while (pos < text.Length)
        {
            lineNumber++;
            while (pos < text.Length && IsBlank(text[pos]))
            {
                pos++;
            }

            if (pos == text.Length)
            {
                break;
            }

            byte first = text[pos];
            if (first == (byte)'\n')
            {
                pos++;
                continue;
            }

            if (first is not ((byte)'#' or (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z')))
            {
                if (rgb is null)
                {
                    if (size == 0)
                    {
                        throw Fail(lineNumber, "falta LUT_3D_SIZE antes de los datos.");
                    }

                    total = size * size * size;
                    rgb = new float[total * 3];
                }

                if (count == total)
                {
                    throw new PackageContentException($"El .cube trae más de las {total} entradas que pide LUT_3D_SIZE {size}.");
                }

                // Camino caliente: hasta 65³ líneas, sin cortar la línea antes de leerla.
                int at = count * 3;
                for (int k = 0; k < 3; k++)
                {
                    while (pos < text.Length && IsBlank(text[pos]))
                    {
                        pos++;
                    }

                    int end = pos;
                    while (end < text.Length && text[end] != (byte)'\n' && !IsBlank(text[end]))
                    {
                        end++;
                    }

                    if (end == pos || !TryParseFloat(text[pos..end], out rgb[at + k]))
                    {
                        throw Fail(lineNumber, "se esperaban tres números.");
                    }

                    pos = end;
                }

                while (pos < text.Length && IsBlank(text[pos]))
                {
                    pos++;
                }

                if (pos < text.Length && text[pos] != (byte)'\n')
                {
                    throw Fail(lineNumber, "se esperaban tres números.");
                }

                pos++;
                count++;
                continue;
            }

            int newline = text[pos..].IndexOf((byte)'\n');
            ReadOnlySpan<byte> line = Trim(newline < 0 ? text[pos..] : text.Slice(pos, newline));
            pos = newline < 0 ? text.Length : pos + newline + 1;
            if (line[0] == (byte)'#')
            {
                continue;
            }

            int split = line.IndexOfAny((byte)' ', (byte)'\t');
            ReadOnlySpan<byte> keyword = split < 0 ? line : line[..split];
            ReadOnlySpan<byte> rest = split < 0 ? default : Trim(line[split..]);
            if (rgb is not null)
            {
                throw Fail(lineNumber, $"«{Name(keyword)}» tiene que ir antes de los datos.");
            }

            if (keyword.SequenceEqual("TITLE"u8))
            {
                if (title is not null)
                {
                    throw Fail(lineNumber, "TITLE está repetido.");
                }

                title = ParseTitle(rest);
            }
            else if (keyword.SequenceEqual("LUT_3D_SIZE"u8))
            {
                if (size != 0)
                {
                    throw Fail(lineNumber, "LUT_3D_SIZE está repetido.");
                }

                if (!int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out size) || size is < LutTable.MinSize or > LutTable.MaxSize)
                {
                    throw Fail(lineNumber, $"LUT_3D_SIZE tiene que ser un entero de {LutTable.MinSize} a {LutTable.MaxSize}.");
                }
            }
            else if (keyword.SequenceEqual("LUT_1D_SIZE"u8) || keyword.SequenceEqual("LUT_1D_INPUT_RANGE"u8))
            {
                throw new PackageContentException("Es una LUT 1D: Rodaje solo usa LUT 3D (LUT_3D_SIZE).");
            }
            else if (keyword.SequenceEqual("DOMAIN_MIN"u8) || keyword.SequenceEqual("DOMAIN_MAX"u8))
            {
                bool isMin = keyword[7] == (byte)'M' && keyword[8] == (byte)'I';
                if (isMin ? hasMin : hasMax)
                {
                    throw Fail(lineNumber, $"{Name(keyword)} está repetido.");
                }

                if (!TryParseTriple(rest, triple))
                {
                    throw Fail(lineNumber, $"{Name(keyword)} necesita tres números.");
                }

                triple.CopyTo(isMin ? min : max);
                hasMin |= isMin;
                hasMax |= !isMin;
            }
            else if (keyword.SequenceEqual("LUT_3D_INPUT_RANGE"u8))
            {
                // Variante de algunos programas: el mismo dominio para los tres canales.
                if (hasMin || hasMax)
                {
                    throw Fail(lineNumber, "El dominio está repetido (DOMAIN_MIN/MAX y LUT_3D_INPUT_RANGE).");
                }

                int space = rest.IndexOfAny((byte)' ', (byte)'\t');
                if (space < 0 || !TryParseFloat(rest[..space], out float low) || !TryParseFloat(Trim(rest[space..]), out float high))
                {
                    throw Fail(lineNumber, "LUT_3D_INPUT_RANGE necesita dos números.");
                }

                min = [low, low, low];
                max = [high, high, high];
                hasMin = hasMax = true;
            }
            else
            {
                throw Fail(lineNumber, $"palabra clave desconocida «{Name(keyword)}».");
            }
        }

        if (size == 0)
        {
            throw new PackageContentException("Falta LUT_3D_SIZE: no parece un .cube 3D.");
        }

        if (count != total || rgb is null)
        {
            throw new PackageContentException($"El .cube trae {count} entradas y LUT_3D_SIZE {size} pide {size * size * size}.");
        }

        for (int c = 0; c < 3; c++)
        {
            if (!(min[c] < max[c]))
            {
                throw new PackageContentException("DOMAIN_MIN tiene que ser menor que DOMAIN_MAX en cada canal.");
            }
        }

        if (min is not [0, 0, 0] || max is not [1, 1, 1])
        {
            for (int i = 0; i < rgb.Length; i++)
            {
                int c = i % 3;
                rgb[i] = (rgb[i] - min[c]) / (max[c] - min[c]);
            }
        }

        return new LutTable(title ?? "", size, rgb);
    }

    private static byte[] ReadAll(Stream stream, out int length)
    {
        ArgumentNullException.ThrowIfNull(stream);
        const string TooBig = "El .cube pesa más de 16 MB.";
        if (stream.CanSeek && stream.Length - stream.Position > LutTable.MaxFileBytes)
        {
            throw new PackageContentException(TooBig);
        }

        byte[] buffer = new byte[stream.CanSeek ? (int)Math.Max(stream.Length - stream.Position, 0) + 1 : 1 << 16];
        length = 0;
        while (true)
        {
            if (length == buffer.Length)
            {
                if (buffer.Length > LutTable.MaxFileBytes)
                {
                    throw new PackageContentException(TooBig);
                }

                Array.Resize(ref buffer, (int)Math.Min((long)buffer.Length * 2, LutTable.MaxFileBytes + 1));
            }

            int read = stream.Read(buffer, length, buffer.Length - length);
            if (read == 0)
            {
                break;
            }

            length += read;
            if (length > LutTable.MaxFileBytes)
            {
                throw new PackageContentException(TooBig);
            }
        }

        return buffer;
    }

    private static string ParseTitle(ReadOnlySpan<byte> rest)
    {
        if (rest.Length >= 2 && rest[0] == (byte)'"' && rest[^1] == (byte)'"')
        {
            rest = rest[1..^1];
        }

        string title = Encoding.UTF8.GetString(rest);
        return SafeText.Truncate(SafeText.Clean(title), SafeText.MaxName);
    }

    private static bool TryParseTriple(ReadOnlySpan<byte> line, Span<float> values)
    {
        for (int i = 0; i < 3; i++)
        {
            line = TrimStart(line);
            int end = line.IndexOfAny((byte)' ', (byte)'\t');
            ReadOnlySpan<byte> token = end < 0 ? line : line[..end];
            if (token.IsEmpty || !TryParseFloat(token, out values[i]))
            {
                return false;
            }

            line = end < 0 ? default : line[end..];
        }

        return TrimStart(line).IsEmpty;
    }

    /// <summary>Camino rápido para los decimales de siempre (<c>-0.123456</c>, <c>1e-3</c>); lo raro va a <c>float.TryParse</c>.</summary>
    private static bool TryParseFloat(ReadOnlySpan<byte> token, out float value)
    {
        int i = 0;
        bool negative = false;
        if (token.Length > 0 && token[0] is (byte)'-' or (byte)'+')
        {
            negative = token[0] == (byte)'-';
            i++;
        }

        long mantissa = 0;
        int digits = 0, scale = 0;
        bool dot = false, any = false;
        for (; i < token.Length; i++)
        {
            byte b = token[i];
            if (b is >= (byte)'0' and <= (byte)'9')
            {
                any = true;
                if (digits < 18)
                {
                    mantissa = (mantissa * 10) + (b - '0');
                    if (mantissa != 0)
                    {
                        digits++;
                    }

                    if (dot)
                    {
                        scale--;
                    }
                }
                else if (!dot)
                {
                    scale++;
                }
            }
            else if (b == (byte)'.' && !dot)
            {
                dot = true;
            }
            else
            {
                break;
            }
        }

        if (any && i < token.Length && token[i] is (byte)'e' or (byte)'E')
        {
            int j = i + 1;
            bool expNegative = false;
            if (j < token.Length && token[j] is (byte)'-' or (byte)'+')
            {
                expNegative = token[j] == (byte)'-';
                j++;
            }

            int exponent = 0;
            int start = j;
            for (; j < token.Length && token[j] is >= (byte)'0' and <= (byte)'9' && exponent < 1000; j++)
            {
                exponent = (exponent * 10) + (token[j] - '0');
            }

            if (j == start)
            {
                return Fallback(token, out value);
            }

            scale += expNegative ? -exponent : exponent;
            i = j;
        }

        if (!any || i != token.Length || scale < -Pow10.Length + 1 || scale > Pow10.Length - 1)
        {
            return Fallback(token, out value);
        }

        double result = scale >= 0 ? mantissa * Pow10[scale] : mantissa / Pow10[-scale];
        value = (float)(negative ? -result : result);
        return float.IsFinite(value);
    }

    private static bool Fallback(ReadOnlySpan<byte> token, out float value) =>
        float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);

    private static double[] BuildPow10()
    {
        double[] table = new double[39];
        double p = 1;
        for (int i = 0; i < table.Length; i++, p *= 10)
        {
            table[i] = p;
        }

        return table;
    }

    private static bool IsBlank(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\f' or (byte)'\v';

    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> s) => TrimStart(s).TrimEnd(" \t\r\f\v"u8);

    private static ReadOnlySpan<byte> TrimStart(ReadOnlySpan<byte> s) => s.TrimStart(" \t\r\f\v"u8);

    private static string Name(ReadOnlySpan<byte> keyword)
    {
        string name = Encoding.UTF8.GetString(keyword[..Math.Min(keyword.Length, 40)]);
        return SafeText.Clean(name);
    }

    private static PackageContentException Fail(int line, string message) =>
        new($"Línea {line.ToString(CultureInfo.InvariantCulture)} del .cube: {message}");
}
