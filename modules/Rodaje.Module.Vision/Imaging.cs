namespace Rodaje.Module.Vision;

/// <summary>Un cuadro BGRA copiado de la memoria compartida (filas de <see cref="Stride"/> bytes).</summary>
internal sealed record BgraFrame(byte[] Pixels, int Width, int Height, int Stride);

/// <summary>Cómo pasar un cuadro BGRA a un plano CHW: orden de canales y <c>valor = v/255 · Scale[c] + Offset[c]</c>.</summary>
internal sealed record PlanarFormat(bool Rgb, float[] Scale, float[] Offset, float Pad = 0)
{
    public static PlanarFormat Normalized(bool rgb, float[] mean, float[] std) =>
        new(rgb, [1 / std[0], 1 / std[1], 1 / std[2]], [-mean[0] / std[0], -mean[1] / std[1], -mean[2] / std[2]]);
}

internal static class Imaging
{
    /// <summary>
    /// Remuestreo bilineal de <paramref name="frame"/> al rectángulo <paramref name="contentWidth"/>×<paramref name="contentHeight"/>
    /// (arriba a la izquierda) de un plano CHW de <paramref name="width"/>×<paramref name="height"/> que empieza en
    /// <paramref name="offset"/>; el resto queda en <see cref="PlanarFormat.Pad"/>. Si <paramref name="gray"/> no es nulo,
    /// recibe la luminancia (0..1) del contenido.
    /// </summary>
    public static void ToPlanar(BgraFrame frame, float[] tensor, int offset, int width, int height, int contentWidth, int contentHeight,
        PlanarFormat format, float[]? gray = null)
    {
        int plane = width * height;
        (int[] x0, int[] x1, float[] fx) = Map(frame.Width, contentWidth);
        (int[] y0, int[] y1, float[] fy) = Map(frame.Height, contentHeight);
        int[] channel = format.Rgb ? [2, 1, 0] : [0, 1, 2];
        byte[] src = frame.Pixels;
        int stride = frame.Stride;
        Parallel.For(0, height, y =>
        {
            int row = offset + (y * width);
            if (y >= contentHeight)
            {
                for (int c = 0; c < 3; c++)
                {
                    tensor.AsSpan(row + (c * plane), width).Fill(format.Pad);
                }

                return;
            }

            int r0 = y0[y] * stride, r1 = y1[y] * stride;
            float wy = fy[y];
            for (int x = 0; x < width; x++)
            {
                if (x >= contentWidth)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        tensor[row + (c * plane) + x] = format.Pad;
                    }

                    continue;
                }

                int a = x0[x] * 4, b = x1[x] * 4;
                float wx = fx[x];
                float lum = 0;
                for (int c = 0; c < 3; c++)
                {
                    int k = channel[c];
                    float top = src[r0 + a + k] + ((src[r0 + b + k] - src[r0 + a + k]) * wx);
                    float bottom = src[r1 + a + k] + ((src[r1 + b + k] - src[r1 + a + k]) * wx);
                    float v = (top + ((bottom - top) * wy)) / 255f;
                    tensor[row + (c * plane) + x] = (v * format.Scale[c]) + format.Offset[c];
                    lum += v * (k == 2 ? 0.299f : k == 1 ? 0.587f : 0.114f);
                }

                if (gray is not null)
                {
                    gray[(y * contentWidth) + x] = lum;
                }
            }
        });
    }

    /// <summary>Alfa (0..1) de <paramref name="width"/>×<paramref name="height"/> a <c>gray8</c> del tamaño de salida (stride = ancho).</summary>
    public static void AlphaToGray8(float[] alpha, int width, int height, Span<byte> output, int outWidth, int outHeight)
    {
        byte[] result = new byte[outWidth * outHeight];
        (int[] x0, int[] x1, float[] fx) = Map(width, outWidth);
        (int[] y0, int[] y1, float[] fy) = Map(height, outHeight);
        Parallel.For(0, outHeight, y =>
        {
            int r0 = y0[y] * width, r1 = y1[y] * width;
            float wy = fy[y];
            for (int x = 0; x < outWidth; x++)
            {
                float top = alpha[r0 + x0[x]] + ((alpha[r0 + x1[x]] - alpha[r0 + x0[x]]) * fx[x]);
                float bottom = alpha[r1 + x0[x]] + ((alpha[r1 + x1[x]] - alpha[r1 + x0[x]]) * fx[x]);
                float v = top + ((bottom - top) * wy);
                result[(y * outWidth) + x] = (byte)Math.Clamp((int)((v * 255f) + 0.5f), 0, 255);
            }
        });
        result.CopyTo(output);
    }

    /// <summary>Para cada posición de destino, los dos vecinos de origen y el peso (centros de píxel alineados).</summary>
    private static (int[] Low, int[] High, float[] Weight) Map(int source, int target)
    {
        var low = new int[target];
        var high = new int[target];
        var weight = new float[target];
        double scale = (double)source / target;
        for (int i = 0; i < target; i++)
        {
            double s = Math.Clamp(((i + 0.5) * scale) - 0.5, 0, source - 1);
            low[i] = (int)s;
            high[i] = Math.Min(low[i] + 1, source - 1);
            weight[i] = (float)(s - low[i]);
        }

        return (low, high, weight);
    }
}
