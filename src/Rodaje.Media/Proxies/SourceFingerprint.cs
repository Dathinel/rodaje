using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Rodaje.Media.Proxies;

/// <summary>Identidad de un archivo de origen: si cambia cualquier parte de ella, su proxy deja de servir.</summary>
/// <param name="Path">Ruta absoluta.</param>
/// <param name="Size">Tamaño en bytes.</param>
/// <param name="LastWriteUtc">Última escritura.</param>
/// <param name="Key">
/// SHA-256 (32 caracteres hexadecimales) de la ruta absoluta sin distinguir mayúsculas, el tamaño, la última
/// escritura y el primer y el último MiB del contenido.
/// </param>
public sealed record SourceFingerprint(string Path, long Size, DateTime LastWriteUtc, string Key)
{
    public const int SampleBytes = 1 << 20;

    /// <summary>Calcula la huella leyendo como mucho 2 MiB del archivo.</summary>
    /// <exception cref="FileNotFoundException">El archivo no existe.</exception>
    /// <exception cref="IOException">El archivo cambió mientras se leía, o no se pudo leer.</exception>
    public static SourceFingerprint Compute(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = System.IO.Path.GetFullPath(path);
        for (int attempt = 0; ; attempt++)
        {
            var info = new FileInfo(full);
            if (!info.Exists)
            {
                throw new FileNotFoundException($"No existe el archivo de origen {full}.", full);
            }

            long size = info.Length;
            DateTime lastWrite = info.LastWriteTimeUtc;
            string key = HashOf(full, size, lastWrite);
            info.Refresh();
            if (info.Exists && info.Length == size && info.LastWriteTimeUtc == lastWrite)
            {
                return new SourceFingerprint(full, size, lastWrite, key);
            }

            if (attempt >= 2)
            {
                throw new IOException($"El archivo {full} cambia mientras se lee; no se puede identificar su proxy.");
            }
        }
    }

    /// <summary>Si el tamaño y la última escritura siguen iguales (comprobación barata, sin leer contenido).</summary>
    public bool MatchesQuick(string path)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length == Size && info.LastWriteTimeUtc == LastWriteUtc;
    }

    private static string HashOf(string fullPath, long size, DateTime lastWrite)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(string.Create(
            CultureInfo.InvariantCulture,
            $"rodaje-proxy-origen-v1\n{fullPath.ToUpperInvariant()}\n{size}\n{lastWrite.Ticks}\n")));
        using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None))
        {
            byte[] buffer = new byte[SampleBytes];
            int head = (int)Math.Min(SampleBytes, size);
            stream.ReadExactly(buffer, 0, head);
            hash.AppendData(buffer, 0, head);
            long tailStart = Math.Max(head, size - SampleBytes);
            int tail = (int)(size - tailStart);
            if (tail > 0)
            {
                stream.Seek(tailStart, SeekOrigin.Begin);
                stream.ReadExactly(buffer, 0, tail);
                hash.AppendData(buffer, 0, tail);
            }
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset().AsSpan(0, 16));
    }
}
