using System.Numerics;

namespace Rodaje.Analysis.Video;

/// <summary>
/// Esquinas de Shi y Tomasi («Good features to track», 1994) repartidas en una rejilla y seguimiento de Lucas y Kanade
/// piramidal (Bouguet, 2000). Implementación propia.
/// </summary>
internal sealed class FeatureTracker
{
    /// <summary>Radio de la ventana del seguimiento (11×11).</summary>
    public const int WindowRadius = 5;

    private const int TensorRadius = 3;
    private const int MaxIterations = 12;
    private const float Epsilon = 0.02f;

    private readonly int _width;
    private readonly int _height;
    private readonly float[] _xx;
    private readonly float[] _xy;
    private readonly float[] _yy;
    private readonly float[] _tmp;
    private readonly float[] _response;

    public FeatureTracker(int width, int height)
    {
        _width = width;
        _height = height;
        int n = width * height;
        _xx = new float[n];
        _xy = new float[n];
        _yy = new float[n];
        _tmp = new float[n];
        _response = new float[n];
    }

    /// <summary>
    /// La esquina más fuerte de cada celda de <paramref name="cell"/> píxeles (el menor autovalor del tensor de
    /// estructura), si pasa de un umbral relativo al máximo de la imagen. Deja fuera el borde que el seguimiento no puede usar.
    /// </summary>
    public void Detect(Pyramid pyramid, int cell, List<Vector2> output)
    {
        output.Clear();
        float[] dx = pyramid.Dx[0];
        float[] dy = pyramid.Dy[0];
        int w = _width;
        int h = _height;
        for (int i = 0; i < dx.Length; i++)
        {
            float gx = dx[i];
            float gy = dy[i];
            _xx[i] = gx * gx;
            _xy[i] = gx * gy;
            _yy[i] = gy * gy;
        }

        BoxSum(_xx);
        BoxSum(_xy);
        BoxSum(_yy);
        float max = 0;
        for (int i = 0; i < _response.Length; i++)
        {
            float a = _xx[i];
            float b = _xy[i];
            float c = _yy[i];
            float half = (a - c) * 0.5f;
            float r = ((a + c) * 0.5f) - MathF.Sqrt((half * half) + (b * b));
            _response[i] = r;
            max = MathF.Max(max, r);
        }

        // Un umbral fijo mínimo evita «esquinas» en el ruido de una imagen lisa.
        float threshold = MathF.Max(max * 0.01f, 50f);
        int border = WindowRadius + 2;
        for (int cy = border; cy < h - border; cy += cell)
        {
            for (int cx = border; cx < w - border; cx += cell)
            {
                float best = threshold;
                int bx = -1, by = -1;
                int yEnd = Math.Min(cy + cell, h - border);
                int xEnd = Math.Min(cx + cell, w - border);
                for (int y = cy; y < yEnd; y++)
                {
                    int row = y * w;
                    for (int x = cx; x < xEnd; x++)
                    {
                        float r = _response[row + x];
                        if (r > best)
                        {
                            best = r;
                            bx = x;
                            by = y;
                        }
                    }
                }

                if (bx >= 0)
                {
                    output.Add(new Vector2(bx, by));
                }
            }
        }
    }

    /// <summary>
    /// Sigue el punto <paramref name="point"/> de <paramref name="previous"/> en <paramref name="next"/>, de arriba hacia
    /// abajo de la pirámide. <c>false</c> si se sale de la imagen, si la ventana no tiene textura o si no converge.
    /// </summary>
    public static bool Track(Pyramid previous, Pyramid next, Vector2 point, out Vector2 found, out float error)
    {
        found = point;
        error = float.MaxValue;
        int levels = previous.Levels.Length;
        Vector2 guess = Vector2.Zero;
        bool started = false;
        for (int level = levels - 1; level >= 0; level--)
        {
            float scale = 1f / (1 << level);
            Vector2 p = point * scale;
            GrayImage image = previous.Levels[level];
            if (!started && !Fits(p, image.Width, image.Height))
            {
                // Cerca del borde los niveles de arriba no tienen la ventana entera: se empieza más abajo.
                guess *= 2;
                continue;
            }

            started = true;
            if (!TrackLevel(previous, next, level, p, guess, out Vector2 flow, out error))
            {
                return false;
            }

            guess = level > 0 ? (guess + flow) * 2 : guess + flow;
        }

        if (!started)
        {
            return false;
        }

        found = point + guess;
        GrayImage base0 = next.Levels[0];
        return found.X >= 0 && found.Y >= 0 && found.X <= base0.Width - 1 && found.Y <= base0.Height - 1;
    }

    private static bool Fits(Vector2 p, int width, int height) =>
        p.X >= WindowRadius + 1 && p.Y >= WindowRadius + 1 && p.X <= width - WindowRadius - 2 && p.Y <= height - WindowRadius - 2;

    private static bool TrackLevel(Pyramid previous, Pyramid next, int level, Vector2 p, Vector2 guess, out Vector2 flow, out float error)
    {
        const int r = WindowRadius;
        const int side = (2 * r) + 1;
        flow = Vector2.Zero;
        error = float.MaxValue;
        GrayImage prev = previous.Levels[level];
        GrayImage cur = next.Levels[level];
        int w = prev.Width;
        int h = prev.Height;
        if (!Fits(p, w, h))
        {
            return false;
        }

        Span<float> patch = stackalloc float[side * side];
        Span<float> gx = stackalloc float[side * side];
        Span<float> gy = stackalloc float[side * side];
        Sample(prev.Pixels, w, p, patch);
        Sample(previous.Dx[level], w, p, gx);
        Sample(previous.Dy[level], w, p, gy);
        float gxx = 0, gxy = 0, gyy = 0;
        for (int i = 0; i < patch.Length; i++)
        {
            gxx += gx[i] * gx[i];
            gxy += gx[i] * gy[i];
            gyy += gy[i] * gy[i];
        }

        float det = (gxx * gyy) - (gxy * gxy);
        float trace = gxx + gyy;
        if (det < 1e-3f * trace * trace || trace < 1e-3f)
        {
            return false;
        }

        Span<float> moved = stackalloc float[side * side];
        Vector2 v = Vector2.Zero;
        for (int iteration = 0; iteration < MaxIterations; iteration++)
        {
            Vector2 q = p + guess + v;
            if (!Fits(q, cur.Width, cur.Height))
            {
                return false;
            }

            Sample(cur.Pixels, cur.Width, q, moved);
            float bx = 0, by = 0, sum = 0;
            for (int i = 0; i < patch.Length; i++)
            {
                float e = patch[i] - moved[i];
                bx += e * gx[i];
                by += e * gy[i];
                sum += MathF.Abs(e);
            }

            error = sum / patch.Length;
            var step = new Vector2(((gyy * bx) - (gxy * by)) / det, ((gxx * by) - (gxy * bx)) / det);
            v += step;
            if (step.LengthSquared() < Epsilon * Epsilon)
            {
                break;
            }
        }

        flow = v;
        return float.IsFinite(v.X) && float.IsFinite(v.Y);
    }

    /// <summary>La ventana alrededor de <paramref name="center"/> con interpolación bilineal (los pesos son los mismos en toda la ventana).</summary>
    private static void Sample(float[] image, int width, Vector2 center, Span<float> output)
    {
        const int r = WindowRadius;
        int x0 = (int)MathF.Floor(center.X);
        int y0 = (int)MathF.Floor(center.Y);
        float fx = center.X - x0;
        float fy = center.Y - y0;
        float w00 = (1 - fx) * (1 - fy);
        float w10 = fx * (1 - fy);
        float w01 = (1 - fx) * fy;
        float w11 = fx * fy;
        int k = 0;
        for (int dy = -r; dy <= r; dy++)
        {
            int row = ((y0 + dy) * width) + x0;
            for (int dx = -r; dx <= r; dx++)
            {
                int i = row + dx;
                output[k++] = (w00 * image[i]) + (w10 * image[i + 1]) + (w01 * image[i + width]) + (w11 * image[i + width + 1]);
            }
        }
    }

    /// <summary>Suma en una ventana cuadrada de lado 2·<see cref="TensorRadius"/>+1, separable, con sumas corridas.</summary>
    private void BoxSum(float[] data)
    {
        int w = _width;
        int h = _height;
        const int r = TensorRadius;
        float[] tmp = _tmp;
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            float sum = 0;
            for (int x = 0; x <= Math.Min(r, w - 1); x++)
            {
                sum += data[row + x];
            }

            for (int x = 0; x < w; x++)
            {
                tmp[row + x] = sum;
                if (x + r + 1 < w)
                {
                    sum += data[row + x + r + 1];
                }

                if (x - r >= 0)
                {
                    sum -= data[row + x - r];
                }
            }
        }

        for (int x = 0; x < w; x++)
        {
            float sum = 0;
            for (int y = 0; y <= Math.Min(r, h - 1); y++)
            {
                sum += tmp[(y * w) + x];
            }

            for (int y = 0; y < h; y++)
            {
                data[(y * w) + x] = sum;
                if (y + r + 1 < h)
                {
                    sum += tmp[((y + r + 1) * w) + x];
                }

                if (y - r >= 0)
                {
                    sum -= tmp[((y - r) * w) + x];
                }
            }
        }
    }
}
