using System.Globalization;
using System.Security.Cryptography;

namespace Rodaje.Media.RenderCache;

/// <summary>
/// La huella de contenido de un archivo, sin su ruta ni su fecha: el tamaño y el SHA-256 de tres trozos de 64 KB
/// (principio, medio y final). Es probabilística: un cambio solo en otra parte con el mismo tamaño no se ve. La usan la
/// biblioteca para los medios repetidos y el render en caché (D-060), para que sirva entre equipos.
/// </summary>
public static class ContentFingerprint
{
    private const int SampleBytes = 64 * 1024;

    /// <summary><c>tamaño:sha256</c> en hexadecimal, o <c>null</c> si el archivo no se puede leer.</summary>
    public static string? Of(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, SampleBytes);
            long length = stream.Length;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[SampleBytes];
            foreach (long offset in new[] { 0L, Math.Max(0, (length / 2) - (SampleBytes / 2)), Math.Max(0, length - SampleBytes) })
            {
                stream.Position = offset;
                int read = stream.ReadAtLeast(buffer, (int)Math.Min(SampleBytes, length - offset), throwOnEndOfStream: false);
                hash.AppendData(buffer, 0, read);
            }

            return length.ToString(CultureInfo.InvariantCulture) + ":" + Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
