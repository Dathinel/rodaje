using System.Globalization;
using System.Security.Cryptography;

namespace Rodaje.Core.Collab;

/// <summary>
/// Huella corta de un archivo de medio, sin ruta ni fecha, para encontrar candidatos entre equipos: el tamaño y el SHA-256 del
/// primer y del último MiB (<c>52428800:9F…</c>; un archivo de hasta 2 MiB se resume entero). Es una muestra: no garantiza que
/// el centro sea igual; para confirmar que es el mismo archivo está <see cref="ContentHash"/>.
/// </summary>
public static class ContentKey
{
    public const int SampleBytes = 1 << 20;

    private const int HexLength = 64;

    public static string Compute(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        return Compute(stream);
    }

    /// <summary>La huella de un flujo que se pueda leer y mover (el flujo no se cierra).</summary>
    public static string Compute(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        long length = stream.Length;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[SampleBytes];
        if (length <= 2L * SampleBytes)
        {
            stream.Position = 0;
            Feed(stream, hash, buffer, (int)length);
        }
        else
        {
            stream.Position = 0;
            Feed(stream, hash, buffer, SampleBytes);
            stream.Position = length - SampleBytes;
            Feed(stream, hash, buffer, SampleBytes);
        }

        return Format(length, hash.GetHashAndReset());
    }

    public static string Format(long length, ReadOnlySpan<byte> sha256) =>
        length.ToString(CultureInfo.InvariantCulture) + ":" + Convert.ToHexString(sha256);

    /// <summary>Forma exacta: tamaño decimal sin ceros a la izquierda, dos puntos y 64 dígitos hexadecimales en mayúsculas.</summary>
    public static bool IsValid(string? key)
    {
        if (key is null)
        {
            return false;
        }

        int colon = key.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && colon <= 19
            && key[..colon].All(char.IsAsciiDigit) && (colon == 1 || key[0] != '0')
            && long.TryParse(key.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out _)
            && key.Length - colon - 1 == HexLength && ContentHash.IsUpperHex(key.AsSpan(colon + 1));
    }

    private static void Feed(Stream stream, IncrementalHash hash, byte[] buffer, int count)
    {
        int left = count;
        while (left > 0)
        {
            int read = stream.Read(buffer, 0, Math.Min(left, buffer.Length));
            if (read == 0)
            {
                throw new EndOfStreamException("El archivo cambió de tamaño mientras se leía.");
            }

            hash.AppendData(buffer, 0, read);
            left -= read;
        }
    }
}
