using System.Security.Cryptography;

namespace Rodaje.Core.Collab;

/// <summary>
/// SHA-256 del archivo entero, en 64 dígitos hexadecimales en mayúsculas. Un medio solo cuenta como «el mismo» en otro equipo
/// cuando coincide este valor; <see cref="ContentKey"/> sirve para buscar candidatos sin leer todo el archivo.
/// </summary>
public static class ContentHash
{
    public const int HexLength = 64;

    public static string Compute(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = Open(path, asynchronous: false);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>Igual que <see cref="Compute(string)"/>, sin bloquear el hilo y con cancelación (un medio puede pesar gigas).</summary>
    public static async Task<string> ComputeAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = Open(path, asynchronous: false);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    public static bool IsValid(string? hash) => hash is { Length: HexLength } && IsUpperHex(hash);

    internal static bool IsUpperHex(ReadOnlySpan<char> text)
    {
        foreach (char c in text)
        {
            if (!(char.IsAsciiDigit(c) || c is >= 'A' and <= 'F'))
            {
                return false;
            }
        }

        return true;
    }

    private static FileStream Open(string path, bool asynchronous) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan | (asynchronous ? FileOptions.Asynchronous : FileOptions.None));
}
