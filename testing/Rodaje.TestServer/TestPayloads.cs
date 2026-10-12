using System.Security.Cryptography;

namespace Rodaje.TestServer;

/// <summary>Contenido de prueba determinista y sus hashes, para armar archivos buenos y malos.</summary>
public static class TestPayloads
{
    /// <summary>
    /// Bytes pseudoaleatorios deterministas: la misma semilla da siempre lo mismo, y dos posiciones distintas
    /// casi nunca coinciden, así que un rango mal unido se nota en el hash.
    /// </summary>
    public static byte[] Deterministic(int length, int seed = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var bytes = new byte[length];

        // xorshift32: sencillo, sin depender de la implementación de System.Random.
        var state = (uint)seed | 1u;
        for (var i = 0; i < bytes.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            bytes[i] = (byte)state;
        }

        return bytes;
    }

    /// <summary>SHA-256 en hexadecimal minúsculo, como lo lista el índice (<c>docs/fase-7.md</c>).</summary>
    public static string Sha256Hex(ReadOnlySpan<byte> content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    /// <summary>Copia con un byte invertido en <paramref name="index"/>: mismo tamaño, hash distinto.</summary>
    public static byte[] Corrupt(ReadOnlySpan<byte> content, int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, content.Length);
        var copy = content.ToArray();
        copy[index] ^= 0xFF;
        return copy;
    }
}
