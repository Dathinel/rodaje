using System.Globalization;
using System.Runtime.InteropServices;

namespace Fase0.Shaders;

/// <summary>
/// Contenido de las texturas de entrada por fórmula, sin aleatorios ni funciones trascendentes (solo +, ×, / y floor en
/// float32), así Windows y Linux dan los mismos bits; el hash de <see cref="TextureSpec.Hash"/> lo comprueba.
/// </summary>
public static class Patterns
{
    public static int Components(string format) => format switch
    {
        "rgba16f" or "rgba32f" => 4,
        "rg16f" => 2,
        "r16f" or "r32f" => 1,
        _ => throw new ArgumentException($"Formato desconocido: {format}"),
    };

    public static bool IsHalf(string format) => format.EndsWith("16f", StringComparison.Ordinal);

    public static int BytesPerTexel(string format) => Components(format) * (IsHalf(format) ? 2 : 4);

    /// <summary>Los bytes tal cual se suben: texeles por filas, capas de 3D seguidas.</summary>
    public static byte[] Bytes(TextureSpec t)
    {
        float[] values = Values(t);
        if (!IsHalf(t.Format))
        {
            return MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
        }

        var halves = new Half[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            halves[i] = (Half)values[i];
        }

        return MemoryMarshal.AsBytes(halves.AsSpan()).ToArray();
    }

    public static string Hash(ReadOnlySpan<byte> bytes)
    {
        ulong h = 14695981039346656037UL;
        foreach (byte b in bytes)
        {
            h = (h ^ b) * 1099511628211UL;
        }

        return h.ToString("x16", CultureInfo.InvariantCulture);
    }

    public static float[] Values(TextureSpec t)
    {
        int n = Components(t.Format);
        var v = new float[t.Width * t.Height * t.Depth * n];
        if (t.Pattern == "literal")
        {
            float[] data = t.Data ?? throw new InvalidDataException($"{t.Name}: falta Data.");
            if (data.Length != v.Length)
            {
                throw new InvalidDataException($"{t.Name}: Data tiene {data.Length} valores y hacen falta {v.Length}.");
            }

            return (float[])data.Clone();
        }

        int i = 0;
        for (int z = 0; z < t.Depth; z++)
        {
            for (int y = 0; y < t.Height; y++)
            {
                for (int x = 0; x < t.Width; x++)
                {
                    for (int c = 0; c < n; c++)
                    {
                        v[i++] = Texel(t, x, y, z, c, n);
                    }
                }
            }
        }

        return v;
    }

    private static float Texel(TextureSpec t, int x, int y, int z, int c, int n) => t.Pattern switch
    {
        "suave" => Smooth(t.Seed, x, y, z, c),
        "premultiplicada" => c == 3 ? Alpha(t.Seed, x, y) : Smooth(t.Seed, x, y, z, c) * Alpha(t.Seed, x, y),
        "lut" => c == 3 ? 1f : Lut(t, x, y, z, c),
        "curvas" => c == 3 ? 1f : Curve(t, x, y, c),
        "distancia" => (MathF.Abs(x + 0.5f - (0.5f * t.Width)) + MathF.Abs(y + 0.5f - (0.5f * t.Height))) * 6f - 14f,
        _ => throw new InvalidDataException($"{t.Name}: patrón desconocido {t.Pattern}."),
    };

    // Onda triangular suavizada: de 0,06 a 0,94, con pendiente máxima de ~0,33 por texel.
    private static float Smooth(int seed, int x, int y, int z, int c)
    {
        float fx = (2 + (((seed * 7) + (c * 5)) % 9)) / 160f;
        float fy = (2 + (((seed * 3) + (c * 7)) % 9)) / 160f;
        float fz = (1 + c) / 31f;
        float phase = (((seed * 13) + (c * 17)) % 29) / 29f;
        return 0.06f + (0.88f * SmoothStep(Triangle((fx * x) + (fy * y) + (fz * z) + phase)));
    }

    private static float Alpha(int seed, int x, int y) => 0.55f + (0.45f * SmoothStep(Triangle((x / 97f) - (y / 131f) + (seed / 7f))));

    private static float Lut(TextureSpec t, int x, int y, int z, int c)
    {
        float r = x / (float)(t.Width - 1), g = y / (float)(t.Height - 1), b = z / (float)(t.Depth - 1);
        float own = c == 0 ? r : c == 1 ? g : b;
        float cross = c == 0 ? g - b : c == 1 ? b - r : r - g;
        return Math.Clamp((0.7f * own) + (0.3f * own * own * (3f - (2f * own))) + (0.05f * cross), 0f, 1f);
    }

    private static float Curve(TextureSpec t, int x, int y, int c)
    {
        float u = x / (float)(t.Width - 1);
        if (y == 0)
        {
            float k = c == 0 ? 0.3f : c == 1 ? -0.2f : 0.15f;
            return u + (k * u * (1f - u));
        }

        return c switch
        {
            0 => 0.5f + (0.05f * (Triangle(u * 2f) - 0.5f)),
            1 => 0.5f + (0.1f * (u - 0.5f)),
            _ => 0.5f + (0.08f * u),
        };
    }

    private static float Triangle(float t) => MathF.Abs((2f * (t - MathF.Floor(t))) - 1f);

    private static float SmoothStep(float t) => t * t * (3f - (2f * t));
}
