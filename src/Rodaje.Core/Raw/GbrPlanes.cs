using System.Runtime.InteropServices;

namespace Rodaje.Core.Raw;

/// <summary>
/// De rgba16f intercalado a los tres planos de <c>gbrpf16le</c> de ffmpeg (G, B, R; sin alfa). ffmpeg recorta a 1,0 el
/// rgbaf16le antes de <c>zscale</c>, pero los planos le llegan enteros (docs/propuestas/hdr-12-bits.md).
/// </summary>
public static class GbrPlanes
{
    /// <exception cref="ArgumentException">La entrada no son píxeles de 8 bytes o la salida no mide 6 bytes por píxel.</exception>
    public static void FromRgbaHalf(ReadOnlySpan<byte> rgba16f, Span<byte> gbr)
    {
        if (rgba16f.Length % 8 != 0 || gbr.Length != rgba16f.Length / 8 * 6)
        {
            throw new ArgumentException($"rgba16f de {rgba16f.Length} bytes no cabe en planos de {gbr.Length} bytes.", nameof(gbr));
        }

        int pixels = rgba16f.Length / 8;
        ReadOnlySpan<ulong> source = MemoryMarshal.Cast<byte, ulong>(rgba16f);
        Span<ushort> planes = MemoryMarshal.Cast<byte, ushort>(gbr);
        Span<ushort> g = planes[..pixels], b = planes.Slice(pixels, pixels), r = planes.Slice(2 * pixels, pixels);
        for (int i = 0; i < source.Length; i++)
        {
            ulong p = source[i];
            r[i] = (ushort)p;
            g[i] = (ushort)(p >> 16);
            b[i] = (ushort)(p >> 32);
        }
    }
}
