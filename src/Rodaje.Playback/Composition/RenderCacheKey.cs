using System.Buffers.Binary;
using System.Globalization;

namespace Rodaje.Playback.Composition;

/// <summary>
/// La huella de 128 bits de la receta de un cuadro (<c>FrameRecipe</c>): la clave del render en caché (D-060). Dos
/// cuadros con la misma huella tienen la misma receta.
/// </summary>
public readonly record struct RenderCacheKey(ulong High, ulong Low)
{
    public const int Size = 16;

    /// <summary>Los primeros 16 bytes de <paramref name="hash"/> (por ejemplo un SHA-256), en orden big-endian.</summary>
    public static RenderCacheKey FromHash(ReadOnlySpan<byte> hash)
    {
        if (hash.Length < Size)
        {
            throw new ArgumentException($"La huella necesita al menos {Size} bytes.", nameof(hash));
        }

        return new RenderCacheKey(BinaryPrimitives.ReadUInt64BigEndian(hash), BinaryPrimitives.ReadUInt64BigEndian(hash[8..]));
    }

    public void WriteTo(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt64BigEndian(destination, High);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], Low);
    }

    /// <summary>32 cifras hexadecimales en minúscula.</summary>
    public override string ToString() => High.ToString("x16", CultureInfo.InvariantCulture) + Low.ToString("x16", CultureInfo.InvariantCulture);

    public static bool TryParse(string? text, out RenderCacheKey key)
    {
        key = default;
        if (text is not { Length: 32 }
            || !ulong.TryParse(text.AsSpan(0, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong high)
            || !ulong.TryParse(text.AsSpan(16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong low))
        {
            return false;
        }

        key = new RenderCacheKey(high, low);
        return true;
    }
}
