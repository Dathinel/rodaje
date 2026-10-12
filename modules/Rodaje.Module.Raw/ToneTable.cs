using System.Runtime.InteropServices;
using Rodaje.Modules.Protocol;

namespace Rodaje.Module.Raw;

/// <summary>
/// De 16 bits lineales de LibRaw (65 535 = blanco) al píxel de salida, con la exposición (2^EV, en luz lineal) y la
/// curva en una tabla de 65 536 entradas: un cuadro 4K se escribe con tres búsquedas por píxel. En <c>rgba16f</c> la
/// exposición puede pasar de 1 (hay margen arriba); en 8 bits se recorta.
/// </summary>
internal sealed class ToneTable
{
    private readonly Half[]? _half;
    private readonly byte[]? _bytes;

    public ToneTable(string pixelFormat, double exposure, OutputCurve curve)
    {
        PixelFormat = pixelFormat;
        double gain = Math.Pow(2, exposure);
        switch (pixelFormat)
        {
            case PixelFormats.Rgba16F:
                _half = new Half[65_536];
                for (int i = 0; i < _half.Length; i++)
                {
                    _half[i] = (Half)Encode(i / 65_535.0 * gain, curve);
                }

                break;
            case PixelFormats.Bgra8 or PixelFormats.Rgba8:
                _bytes = new byte[65_536];
                for (int i = 0; i < _bytes.Length; i++)
                {
                    _bytes[i] = (byte)Math.Round(Math.Clamp(Encode(i / 65_535.0 * gain, curve), 0, 1) * 255);
                }

                break;
            default:
                throw new ArgumentException($"Formato de salida {pixelFormat} no admitido: rgba16f, bgra8 o rgba8.");
        }
    }

    public string PixelFormat { get; }

    public static bool Supports(string pixelFormat) => pixelFormat is PixelFormats.Rgba16F or PixelFormats.Bgra8 or PixelFormats.Rgba8;

    /// <summary>La curva aplicada a un valor lineal (sin recortar arriba).</summary>
    public static double Encode(double linear, OutputCurve curve)
    {
        double v = Math.Max(linear, 0);
        return curve switch
        {
            OutputCurve.Rec709 => v < 0.018 ? 4.5 * v : (1.099 * Math.Pow(v, 0.45)) - 0.099,
            OutputCurve.Srgb => v <= 0.0031308 ? 12.92 * v : (1.055 * Math.Pow(v, 1 / 2.4)) - 0.055,
            _ => v,
        };
    }

    /// <summary>Escribe <paramref name="rgb"/> (16 bits por canal, sin relleno) en <paramref name="output"/> fila por fila.</summary>
    public void Write(ReadOnlySpan<ushort> rgb, int width, int height, Span<byte> output, int stride)
    {
        int bytesPerPixel = PixelFormats.BytesPerPixel(PixelFormat);
        if (stride < width * bytesPerPixel || output.Length < ((long)stride * (height - 1)) + (width * bytesPerPixel))
        {
            throw new ArgumentException("La salida es más chica que el cuadro.");
        }

        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<ushort> source = rgb.Slice(y * width * 3, width * 3);
            Span<byte> row = output.Slice(y * stride, width * bytesPerPixel);
            if (_half is { } half)
            {
                Span<Half> target = MemoryMarshal.Cast<byte, Half>(row);
                for (int x = 0, s = 0, t = 0; x < width; x++, s += 3, t += 4)
                {
                    target[t] = half[source[s]];
                    target[t + 1] = half[source[s + 1]];
                    target[t + 2] = half[source[s + 2]];
                    target[t + 3] = Half.One;
                }
            }
            else
            {
                byte[] table = _bytes!;
                (int r, int b) = PixelFormat == PixelFormats.Bgra8 ? (2, 0) : (0, 2);
                for (int x = 0, s = 0, t = 0; x < width; x++, s += 3, t += 4)
                {
                    row[t + r] = table[source[s]];
                    row[t + 1] = table[source[s + 1]];
                    row[t + b] = table[source[s + 2]];
                    row[t + 3] = 255;
                }
            }
        }
    }
}
