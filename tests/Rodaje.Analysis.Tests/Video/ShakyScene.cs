using Rodaje.Analysis.Video;

namespace Rodaje.Analysis.Tests.Video;

/// <summary>
/// Escena sintética con una cámara de movimiento conocido: una textura de ruido de varias escalas vista a través de
/// <see cref="Camera"/>, que lleva un punto del mundo (en altos de la imagen, centrado) a la posición en el cuadro.
/// </summary>
internal sealed class ShakyScene
{
    private const int TextureSize = 2048;
    private readonly float[] _texture = new float[TextureSize * TextureSize];

    public ShakyScene(int seed)
    {
        var random = new Random(seed);
        foreach ((int cell, float amplitude) in new[] { (96, 60f), (40, 50f), (16, 40f), (8, 25f) })
        {
            int n = (TextureSize / cell) + 2;
            float[] lattice = new float[n * n];
            for (int i = 0; i < lattice.Length; i++)
            {
                lattice[i] = (float)random.NextDouble();
            }

            for (int y = 0; y < TextureSize; y++)
            {
                float gy = (float)y / cell;
                int y0 = (int)gy;
                float fy = Smooth(gy - y0);
                for (int x = 0; x < TextureSize; x++)
                {
                    float gx = (float)x / cell;
                    int x0 = (int)gx;
                    float fx = Smooth(gx - x0);
                    float top = Lerp(lattice[(y0 * n) + x0], lattice[(y0 * n) + x0 + 1], fx);
                    float bottom = Lerp(lattice[((y0 + 1) * n) + x0], lattice[((y0 + 1) * n) + x0 + 1], fx);
                    _texture[(y * TextureSize) + x] += amplitude * Lerp(top, bottom, fy);
                }
            }
        }
    }

    /// <summary>Movimiento verdadero de cada cuadro: una panorámica lenta con giro, más temblor de mano filtrado.</summary>
    public static Similarity[] Camera(int frames, int seed, double jitter = 0.012, double jitterDegrees = 0.4, double pan = 0.0015)
    {
        var random = new Random(seed);
        var path = new Similarity[frames];
        double jx = 0, jy = 0, ja = 0;
        for (int k = 0; k < frames; k++)
        {
            // Ruido blanco gaussiano pasado por un polo (0,4): temblor de unos pocos hercios.
            jx = (0.4 * jx) + (0.6 * Gauss(random) * jitter);
            jy = (0.4 * jy) + (0.6 * Gauss(random) * jitter);
            ja = (0.4 * ja) + (0.6 * Gauss(random) * jitterDegrees * Math.PI / 180);
            double angle = (0.002 * Math.Sin(k / 40.0)) + ja;
            double scale = 1 + (0.01 * Math.Sin(k / 70.0));
            path[k] = Similarity.FromParts(angle, scale, (pan * k) + jx, (0.3 * pan * k) + jy);
        }

        return path;
    }

    /// <summary>El cuadro que ve <paramref name="camera"/>, en valores 0–255, de <paramref name="width"/>×<paramref name="height"/>.</summary>
    public void Render(Similarity camera, int width, int height, Span<float> output)
    {
        Similarity back = camera.Inverse();
        const double texelsPerUnit = 600;
        for (int j = 0; j < height; j++)
        {
            double py = (j + 0.5 - (height / 2.0)) / height;
            for (int i = 0; i < width; i++)
            {
                double px = (i + 0.5 - (width / 2.0)) / height;
                (double wx, double wy) = back.Apply(px, py);
                output[(j * width) + i] = Sample((wx * texelsPerUnit) + (TextureSize / 2.0), (wy * texelsPerUnit) + (TextureSize / 2.0));
            }
        }
    }

    public byte[] RenderLuma(Similarity camera, int width, int height)
    {
        float[] values = new float[width * height];
        Render(camera, width, height, values);
        return [.. values.Select(v => (byte)Math.Clamp(Math.Round(v), 0, 255))];
    }

    private float Sample(double x, double y)
    {
        x = Math.Clamp(x, 0, TextureSize - 2);
        y = Math.Clamp(y, 0, TextureSize - 2);
        int x0 = (int)x;
        int y0 = (int)y;
        float fx = (float)(x - x0);
        float fy = (float)(y - y0);
        int i = (y0 * TextureSize) + x0;
        return Lerp(Lerp(_texture[i], _texture[i + 1], fx), Lerp(_texture[i + TextureSize], _texture[i + TextureSize + 1], fx), fy);
    }

    private static float Smooth(float t) => t * t * (3 - (2 * t));

    private static float Lerp(float a, float b, float t) => a + ((b - a) * t);

    private static double Gauss(Random random) =>
        Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
}
