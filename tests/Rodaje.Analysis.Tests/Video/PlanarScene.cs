using System.Numerics;
using Rodaje.Core.Effects;

namespace Rodaje.Analysis.Tests.Video;

/// <summary>
/// Un plano con textura de ruido suave de varias escalas, visto en cada cuadro a través de una homografía conocida. La
/// textura se evalúa en coordenadas continuas (con 2×2 muestras por píxel, como un sensor), así la posición verdadera de
/// cualquier punto se conoce al subpíxel.
/// </summary>
internal sealed class PlanarScene(int seed)
{
    private static readonly (double Cell, double Amplitude)[] Octaves = [(40, 70), (17, 50), (8, 35), (4, 18)];

    /// <summary>Valor de la textura en el punto del plano (de 0 a 255 aproximadamente).</summary>
    public double At(double x, double y)
    {
        double sum = 30;
        for (int o = 0; o < Octaves.Length; o++)
        {
            (double cell, double amplitude) = Octaves[o];
            double gx = x / cell, gy = y / cell;
            double fx = Math.Floor(gx), fy = Math.Floor(gy);
            int ix = (int)fx, iy = (int)fy;
            double tx = Smooth(gx - fx), ty = Smooth(gy - fy);
            double top = Lerp(Hash(ix, iy, o), Hash(ix + 1, iy, o), tx);
            double bottom = Lerp(Hash(ix, iy + 1, o), Hash(ix + 1, iy + 1, o), tx);
            sum += amplitude * Lerp(top, bottom, ty);
        }

        return sum;
    }

    /// <summary>El cuadro visto por <paramref name="view"/> (plano → imagen), con un objeto que tapa y ruido opcionales.</summary>
    public float[] Render(int width, int height, Homography view, double noise = 0, Random? random = null, (Vector2 Min, Vector2 Max)? occluder = null)
    {
        Homography back = view.Inverse()!.Value;
        float[] image = new float[width * height];
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                double sum = 0;
                for (int s = 0; s < 4; s++)
                {
                    (double px, double py) = back.Apply(x + 0.25 + (0.5 * (s & 1)), y + 0.25 + (0.5 * (s >> 1)));
                    sum += At(px, py);
                }

                image[(y * width) + x] = (float)(sum / 4);
            }
        });

        if (occluder is { } box)
        {
            for (int y = Math.Max(0, (int)box.Min.Y); y < Math.Min(height, (int)box.Max.Y); y++)
            {
                for (int x = Math.Max(0, (int)box.Min.X); x < Math.Min(width, (int)box.Max.X); x++)
                {
                    image[(y * width) + x] = 90;
                }
            }
        }

        if (noise > 0 && random is not null)
        {
            for (int i = 0; i < image.Length; i++)
            {
                double u = 1 - random.NextDouble(), v = random.NextDouble();
                image[i] += (float)(noise * Math.Sqrt(-2 * Math.Log(u)) * Math.Cos(2 * Math.PI * v));
            }
        }

        return image;
    }

    /// <summary>Desenfoque de movimiento: promedio de <paramref name="samples"/> vistas entre <paramref name="from"/> y <paramref name="to"/>.</summary>
    public float[] RenderBlurred(int width, int height, Func<double, Homography> view, int samples)
    {
        float[] sum = new float[width * height];
        for (int s = 0; s < samples; s++)
        {
            float[] frame = Render(width, height, view(s / (double)(samples - 1)));
            for (int i = 0; i < sum.Length; i++)
            {
                sum[i] += frame[i] / samples;
            }
        }

        return sum;
    }

    /// <summary>Un píxel de la imagen (centro del píxel en x + 0,5) llevado por <paramref name="view"/>: dónde está en el cuadro.</summary>
    public static Vector2 Project(Homography view, Vector2 plane)
    {
        (double x, double y) = view.Apply(plane.X, plane.Y);
        return new Vector2((float)(x - 0.5), (float)(y - 0.5));
    }

    /// <summary>El punto del plano que se ve en el píxel <paramref name="pixel"/> (convención de <see cref="Project"/>).</summary>
    public static Vector2 Unproject(Homography view, Vector2 pixel)
    {
        (double x, double y) = view.Inverse()!.Value.Apply(pixel.X + 0.5, pixel.Y + 0.5);
        return new Vector2((float)x, (float)y);
    }

    private double Hash(int x, int y, int octave)
    {
        unchecked
        {
            uint h = (uint)((x * 374761393) + (y * 668265263) + (octave * 2147483647) + (seed * 1274126177));
            h = (h ^ (h >> 13)) * 1274126177;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / (double)0xFFFFFF;
        }
    }

    private static double Smooth(double t) => t * t * t * ((t * ((t * 6) - 15)) + 10);

    private static double Lerp(double a, double b, double t) => a + ((b - a) * t);
}
