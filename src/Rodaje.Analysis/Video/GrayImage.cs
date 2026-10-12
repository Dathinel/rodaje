using System.Runtime.CompilerServices;

namespace Rodaje.Analysis.Video;

/// <summary>Cómo vienen las muestras del plano de luma.</summary>
public enum LumaFormat
{
    /// <summary>Un byte por muestra (NV12, yuv420p).</summary>
    Gray8,

    /// <summary>Palabras de 16 bits con el dato en los bits altos (P010).</summary>
    Gray16High,

    /// <summary>Palabras de 16 bits con el dato de 10 bits en los bits bajos (yuv420p10).</summary>
    Gray16Low10,
}

/// <summary>Plano de luma de un cuadro tal como sale del decodificador: <c>height</c> filas de <c>stride</c> bytes.</summary>
public readonly ref struct LumaFrame(ReadOnlySpan<byte> data, int width, int height, int stride, LumaFormat format)
{
    public ReadOnlySpan<byte> Data { get; } = data;

    public int Width { get; } = width;

    public int Height { get; } = height;

    public int Stride { get; } = stride;

    public LumaFormat Format { get; } = format;
}

/// <summary>Imagen gris en <c>float</c> (0–255) para el análisis de movimiento.</summary>
internal sealed class GrayImage
{
    public GrayImage(int width, int height)
    {
        Width = width;
        Height = height;
        Pixels = new float[width * height];
    }

    public int Width { get; }

    public int Height { get; }

    public float[] Pixels { get; }

    /// <summary>
    /// Reduce el plano por un factor entero con el promedio de cada bloque (filtro de caja, que también quita el ruido
    /// de compresión) y lo deja en <paramref name="target"/>, que mide <c>ancho/factor × alto/factor</c>.
    /// </summary>
    public static void Downsample(LumaFrame frame, int factor, GrayImage target)
    {
        int width = target.Width;
        int height = target.Height;
        float[] output = target.Pixels;
        float scale = 1f / (factor * factor);
        LumaFormat format = frame.Format;
        int stride = frame.Stride;
        unsafe
        {
            fixed (byte* basePtr = frame.Data)
            {
                nint source = (nint)basePtr;
                Parallel.For(0, height, y =>
                {
                    int row = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        float sum = 0;
                        for (int dy = 0; dy < factor; dy++)
                        {
                            byte* line = (byte*)source + ((long)((y * factor) + dy) * stride);
                            int sx = x * factor;
                            switch (format)
                            {
                                case LumaFormat.Gray8:
                                    for (int dx = 0; dx < factor; dx++)
                                    {
                                        sum += line[sx + dx];
                                    }

                                    break;
                                case LumaFormat.Gray16High:
                                    for (int dx = 0; dx < factor; dx++)
                                    {
                                        sum += Unsafe.ReadUnaligned<ushort>(line + ((sx + dx) * 2)) * (1f / 256);
                                    }

                                    break;
                                default:
                                    for (int dx = 0; dx < factor; dx++)
                                    {
                                        sum += Unsafe.ReadUnaligned<ushort>(line + ((sx + dx) * 2)) * (1f / 4);
                                    }

                                    break;
                            }
                        }

                        output[row + x] = sum * scale;
                    }
                });
            }
        }
    }

    /// <summary>La mitad en cada lado con el promedio de 2×2.</summary>
    public static void Half(GrayImage source, GrayImage target)
    {
        float[] s = source.Pixels;
        float[] t = target.Pixels;
        int sw = source.Width;
        int w = target.Width;
        for (int y = 0; y < target.Height; y++)
        {
            int a = 2 * y * sw;
            int b = a + sw;
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int i = 2 * x;
                t[row + x] = (s[a + i] + s[a + i + 1] + s[b + i] + s[b + i + 1]) * 0.25f;
            }
        }
    }

    /// <summary>Derivadas centradas; el borde queda en 0.</summary>
    public static void Gradients(GrayImage image, float[] dx, float[] dy)
    {
        float[] p = image.Pixels;
        int w = image.Width;
        int h = image.Height;
        Array.Clear(dx);
        Array.Clear(dy);
        for (int y = 1; y < h - 1; y++)
        {
            int row = y * w;
            for (int x = 1; x < w - 1; x++)
            {
                int i = row + x;
                dx[i] = (p[i + 1] - p[i - 1]) * 0.5f;
                dy[i] = (p[i + w] - p[i - w]) * 0.5f;
            }
        }
    }
}
