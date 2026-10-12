using Rodaje.Playback.Composition;

namespace Rodaje.Render.Tests.Support;

/// <summary>Color de 0 a 1 en doble precisión.</summary>
internal readonly record struct C3(double R, double G, double B)
{
    public static C3 From(Rgb rgb) => new(rgb.R / 255, rgb.G / 255, rgb.B / 255);

    public Rgb To255() => new(R * 255, G * 255, B * 255);

    public C3 Map(Func<double, double> f) => new(f(R), f(G), f(B));

    public static C3 operator +(C3 a, C3 b) => new(a.R + b.R, a.G + b.G, a.B + b.B);

    public static C3 operator -(C3 a, C3 b) => new(a.R - b.R, a.G - b.G, a.B - b.B);

    public static C3 operator *(C3 a, double k) => new(a.R * k, a.G * k, a.B * k);

    public static C3 Mix(C3 a, C3 b, double t) => a + ((b - a) * t);
}

/// <summary>
/// Referencias en la CPU de los efectos, escritas desde la definición de cada fórmula (docs/arquitectura/render.md) sin
/// compartir código con los shaders.
/// </summary>
internal static class EffectReference
{
    private const double Kr = 0.2126;
    private const double Kg = 0.7152;
    private const double Kb = 0.0722;

    public static double Saturate(double v) => Math.Clamp(v, 0, 1);

    public static double Smoothstep(double edge0, double edge1, double x)
    {
        double t = Saturate((x - edge0) / (edge1 - edge0));
        return t * t * (3 - (2 * t));
    }

    /// <summary>Exposición, contraste, saturación, temperatura, tinte, altas y sombras, en ese orden.</summary>
    public static C3 Grade(C3 c, double exposure, double contrast, double saturation, double temperature, double tint,
        double highlights, double shadows)
    {
        c *= Math.Pow(2, exposure);
        c = c.Map(v => ((v - 0.5) * (1 + contrast)) + 0.5);
        double luma = (Kr * c.R) + (Kg * c.G) + (Kb * c.B);
        c = c.Map(v => luma + ((v - luma) * (1 + saturation)));
        c = new C3(c.R * (1 + (0.2 * temperature)), c.G * (1 - (0.2 * tint)), c.B * (1 - (0.2 * temperature)));
        luma = (Kr * c.R) + (Kg * c.G) + (Kb * c.B);
        double lift = 0.25 * ((highlights * Smoothstep(0.5, 1, luma)) + (shadows * (1 - Smoothstep(0, 0.5, luma))));
        return c.Map(v => Saturate(v + lift));
    }

    /// <summary>Ruedas: lift <c>c + l·(1 − c)</c>, gain <c>c·(1 + g)</c> y gamma <c>c^(2^−γ)</c>, por canal con el general sumado.</summary>
    public static C3 Wheels(C3 c, C3 lift, C3 gamma, C3 gain)
    {
        static double One(double v, double l, double y, double g) =>
            Saturate(Math.Pow(Math.Max((v + (l * (1 - v))) * (1 + g), 0), Math.Pow(2, -y)));
        return new C3(One(c.R, lift.R, gamma.R, gain.R), One(c.G, lift.G, gamma.G, gain.G), One(c.B, lift.B, gamma.B, gain.B));
    }

    /// <summary>
    /// Croma en Y'CbCr BT.709: alfa = smoothstep(tol, tol + suavidad, 2·|CbCr − clave|) recortado a 0,45·limpieza por cada
    /// lado; el derrame quita del croma su componente positiva en la dirección de la clave, con la luma fija.
    /// </summary>
    public static (C3 Color, double Alpha) ChromaKey(C3 c, C3 key, double tolerance, double softness, double spill, double cleanup)
    {
        static (double Y, double Cb, double Cr) ToYcc(C3 v)
        {
            double y = (Kr * v.R) + (Kg * v.G) + (Kb * v.B);
            return (y, (v.B - y) / 1.8556, (v.R - y) / 1.5748);
        }

        (double y, double cb, double cr) = ToYcc(c);
        (_, double kcb, double kcr) = ToYcc(key);
        double distance = 2 * Math.Sqrt(((cb - kcb) * (cb - kcb)) + ((cr - kcr) * (cr - kcr)));
        double alpha = Smoothstep(tolerance, tolerance + Math.Max(softness, 1e-3), distance);
        double clip = 0.45 * cleanup;
        alpha = Saturate((alpha - clip) / (1 - (2 * clip)));
        double length = Math.Sqrt((kcb * kcb) + (kcr * kcr));
        if (length > 1e-4)
        {
            double along = Math.Max(((cb * kcb) + (cr * kcr)) / length, 0);
            cb -= spill * along * kcb / length;
            cr -= spill * along * kcr / length;
        }

        double r = y + (1.5748 * cr);
        double b = y + (1.8556 * cb);
        double g = (y - (Kr * r) - (Kb * b)) / Kg;
        return (new C3(Saturate(r), Saturate(g), Saturate(b)), alpha);
    }

    /// <summary>LUT con interpolación trilineal entre los N³ puntos y mezcla por intensidad.</summary>
    public static C3 Lut(C3 c, LutData lut, double intensity)
    {
        int n = lut.Size;
        float[] data = lut.Rgb.ToArray();
        double x = Saturate(c.R) * (n - 1);
        double y = Saturate(c.G) * (n - 1);
        double z = Saturate(c.B) * (n - 1);
        int x0 = Math.Min((int)x, n - 2);
        int y0 = Math.Min((int)y, n - 2);
        int z0 = Math.Min((int)z, n - 2);
        double fx = x - x0;
        double fy = y - y0;
        double fz = z - z0;
        C3 At(int i, int j, int k)
        {
            int o = 3 * (i + (n * (j + (n * k))));
            return new C3(data[o], data[o + 1], data[o + 2]);
        }

        C3 c00 = C3.Mix(At(x0, y0, z0), At(x0 + 1, y0, z0), fx);
        C3 c10 = C3.Mix(At(x0, y0 + 1, z0), At(x0 + 1, y0 + 1, z0), fx);
        C3 c01 = C3.Mix(At(x0, y0, z0 + 1), At(x0 + 1, y0, z0 + 1), fx);
        C3 c11 = C3.Mix(At(x0, y0 + 1, z0 + 1), At(x0 + 1, y0 + 1, z0 + 1), fx);
        C3 graded = C3.Mix(C3.Mix(c00, c10, fy), C3.Mix(c01, c11, fy), fz);
        return C3.Mix(c, graded, intensity).Map(Saturate);
    }

    /// <summary>Ganancia de la viñeta en un punto de la capa (0–1): elipse que llega a 1 en las esquinas.</summary>
    public static double Vignette(double u, double v, double amount, double size, double softness)
    {
        double d = Math.Sqrt(Math.Pow((u - 0.5) * 2, 2) + Math.Pow((v - 0.5) * 2, 2)) * Math.Sqrt(0.5);
        double t = Saturate((d - size) / Math.Max(softness, 1e-3));
        return 1 - (amount * t * t * (3 - (2 * t)));
    }

    /// <summary>Mezcla W3C con alfa sobre un fondo opaco; la suma recorta a 1 como el destino de 8 bits.</summary>
    public static C3 Blend(BlendMode mode, C3 cb, C3 cs, double alpha)
    {
        if (mode == BlendMode.Add)
        {
            return new C3(Math.Min(1, cb.R + (alpha * cs.R)), Math.Min(1, cb.G + (alpha * cs.G)), Math.Min(1, cb.B + (alpha * cs.B)));
        }

        var mixed = new C3(Channel(mode, cb.R, cs.R), Channel(mode, cb.G, cs.G), Channel(mode, cb.B, cs.B));
        return C3.Mix(cb, mixed, alpha);
    }

    public static double Channel(BlendMode mode, double b, double s) => mode switch
    {
        BlendMode.Multiply => b * s,
        BlendMode.Screen => b + s - (b * s),
        BlendMode.Overlay => HardLight(s, b),
        BlendMode.SoftLight => s <= 0.5
            ? b - ((1 - (2 * s)) * b * (1 - b))
            : b + (((2 * s) - 1) * ((b <= 0.25 ? ((((16 * b) - 12) * b) + 4) * b : Math.Sqrt(b)) - b)),
        BlendMode.HardLight => HardLight(b, s),
        BlendMode.Darken => Math.Min(b, s),
        BlendMode.Lighten => Math.Max(b, s),
        BlendMode.Difference => Math.Abs(b - s),
        BlendMode.Exclusion => b + s - (2 * b * s),
        BlendMode.ColorDodge => b == 0 ? 0 : s >= 1 ? 1 : Math.Min(1, b / (1 - s)),
        BlendMode.ColorBurn => b >= 1 ? 1 : s <= 0 ? 0 : 1 - Math.Min(1, (1 - b) / s),
        _ => s,
    };

    // Luz fuerte de W3C: multiplicar con 2·Cs si Cs ≤ 0,5; si no, trama con 2·Cs − 1.
    private static double HardLight(double b, double s) => s <= 0.5 ? b * 2 * s : b + ((2 * s) - 1) - (b * ((2 * s) - 1));
}

/// <summary>Imagen RGBA en doble precisión para seguir los pasos de los intermedios en la CPU.</summary>
internal sealed class FloatImage(int width, int height)
{
    private readonly double[] _rgba = new double[width * height * 4];

    public int Width { get; } = width;

    public int Height { get; } = height;

    public static FloatImage FromColors(int width, int height, Func<int, int, C3> color)
    {
        var image = new FloatImage(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                image.Set(x, y, color(x, y), 1);
            }
        }

        return image;
    }

    public (C3 Color, double Alpha) Get(int x, int y)
    {
        int i = ((y * Width) + x) * 4;
        return (new C3(_rgba[i], _rgba[i + 1], _rgba[i + 2]), _rgba[i + 3]);
    }

    public void Set(int x, int y, C3 premultiplied, double alpha)
    {
        int i = ((y * Width) + x) * 4;
        _rgba[i] = premultiplied.R;
        _rgba[i + 1] = premultiplied.G;
        _rgba[i + 2] = premultiplied.B;
        _rgba[i + 3] = alpha;
    }

    /// <summary>Muestreo bilineal con uv de la imagen (0–1), sujeto al borde como el sampler y el tope de los shaders.</summary>
    public (C3 Color, double Alpha) Sample(double u, double v)
    {
        double x = (Math.Min(u * Width, Width - 0.5)) - 0.5;
        double y = (Math.Min(v * Height, Height - 0.5)) - 0.5;
        int x0 = (int)Math.Floor(x);
        int y0 = (int)Math.Floor(y);
        double fx = x - x0;
        double fy = y - y0;
        (C3 c00, double a00) = Get(Math.Clamp(x0, 0, Width - 1), Math.Clamp(y0, 0, Height - 1));
        (C3 c10, double a10) = Get(Math.Clamp(x0 + 1, 0, Width - 1), Math.Clamp(y0, 0, Height - 1));
        (C3 c01, double a01) = Get(Math.Clamp(x0, 0, Width - 1), Math.Clamp(y0 + 1, 0, Height - 1));
        (C3 c11, double a11) = Get(Math.Clamp(x0 + 1, 0, Width - 1), Math.Clamp(y0 + 1, 0, Height - 1));
        C3 top = C3.Mix(c00, c10, fx);
        C3 bottom = C3.Mix(c01, c11, fx);
        double alphaTop = a00 + ((a10 - a00) * fx);
        double alphaBottom = a01 + ((a11 - a01) * fx);
        return (C3.Mix(top, bottom, fy), alphaTop + ((alphaBottom - alphaTop) * fy));
    }

    /// <summary>La misma pirámide que la GPU: mitades mientras sigma pase de 2 y gaussiano separable de 3 sigmas.</summary>
    public FloatImage Blur(double sigma)
    {
        if (!(sigma >= 0.25))
        {
            return this;
        }

        FloatImage current = this;
        double sigmaX = sigma;
        double sigmaY = sigma;
        while (Math.Max(sigmaX, sigmaY) > 2 && current.Width > 4 && current.Height > 4)
        {
            var half = new FloatImage((current.Width + 1) / 2, (current.Height + 1) / 2);
            FloatImage source = current;
            half.Fill((x, y) => source.Sample((x + 0.5) / half.Width, (y + 0.5) / half.Height));
            sigmaX *= (double)half.Width / current.Width;
            sigmaY *= (double)half.Height / current.Height;
            current = half;
        }

        FloatImage horizontal = current.Pass(1.0 / current.Width, 0, sigmaX);
        return horizontal.Pass(0, 1.0 / current.Height, sigmaY);
    }

    /// <summary>Máscara de enfoque: original + cantidad · (original − desenfocado), recortado a 0–1.</summary>
    public FloatImage Sharpen(double amount, double sigma)
    {
        FloatImage blurred = Blur(sigma);
        var result = new FloatImage(Width, Height);
        result.Fill((x, y) =>
        {
            (C3 original, double alpha) = Get(x, y);
            (C3 soft, double softAlpha) = blurred.Sample((x + 0.5) / Width, (y + 0.5) / Height);
            C3 sharp = (original + ((original - soft) * amount)).Map(EffectReference.Saturate);
            return (sharp, EffectReference.Saturate(alpha + (amount * (alpha - softAlpha))));
        });
        return result;
    }

    private FloatImage Pass(double stepU, double stepV, double sigma)
    {
        int taps = (int)Math.Ceiling(3 * sigma);
        double k = -0.5 / (sigma * sigma);
        var output = new FloatImage(Width, Height);
        output.Fill((x, y) =>
        {
            double u = (x + 0.5) / Width;
            double v = (y + 0.5) / Height;
            (C3 sum, double alpha) = Sample(u, v);
            double total = 1;
            for (int t = 1; t <= taps; t++)
            {
                double w = Math.Exp(k * t * t);
                (C3 plus, double plusAlpha) = Sample(u + (stepU * t), v + (stepV * t));
                (C3 minus, double minusAlpha) = Sample(u - (stepU * t), v - (stepV * t));
                sum += (plus + minus) * w;
                alpha += (plusAlpha + minusAlpha) * w;
                total += 2 * w;
            }

            return (sum * (1 / total), alpha / total);
        });
        return output;
    }

    internal void Fill(Func<int, int, (C3 Color, double Alpha)> pixel)
    {
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                (C3 color, double alpha) = pixel(x, y);
                Set(x, y, color, alpha);
            }
        }
    }
}
