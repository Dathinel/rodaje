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
        ReadOnlySpan<ushort> source = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(rgba16f);
        Span<ushort> planes = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(gbr);
        Span<ushort> g = planes[..pixels], b = planes.Slice(pixels, pixels), r = planes.Slice(2 * pixels, pixels);
        for (int i = 0; i < pixels; i++)
        {
            r[i] = source[i * 4];
            g[i] = source[(i * 4) + 1];
            b[i] = source[(i * 4) + 2];
        }
    }
}
