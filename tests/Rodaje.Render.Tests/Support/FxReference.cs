using Rodaje.Core.Effects;

namespace Rodaje.Render.Tests.Support;

/// <summary>
/// Referencias en la CPU de los efectos de imagen nuevos (calificador, curvas de tono, protección de piel, viñeta con forma
/// y las pasadas del intermedio), escritas desde las fórmulas de docs/arquitectura/render.md, en doble precisión.
/// </summary>
internal static class FxReference
{
    private const double Kr = 0.2126;
    private const double Kg = 0.7152;
    private const double Kb = 0.0722;

    public static double Luma(C3 c) => (Kr * c.R) + (Kg * c.G) + (Kb * c.B);

    public static (double Cb, double Cr) CbCr(C3 c, double y) => ((c.B - y) / 1.8556, (c.R - y) / 1.5748);

    public static C3 FromYcc(double y, double cb, double cr)
    {
        double r = y + (1.5748 * cr);
        double b = y + (1.8556 * cb);
        return new C3(r, (y - (Kr * r) - (Kb * b)) / Kg, b);
    }

    /// <summary>Tono de HSV de 0 a 1; 0 en los grises.</summary>
    public static double Hue(C3 c)
    {
        double high = Math.Max(c.R, Math.Max(c.G, c.B));
        double d = high - Math.Min(c.R, Math.Min(c.G, c.B));
        if (d < 1e-5)
        {
            return 0;
        }

        double h = high == c.R ? (c.G - c.B) / d : high == c.G ? 2 + ((c.B - c.R) / d) : 4 + ((c.R - c.G) / d);
        h = (h / 6) + 1;
        return h - Math.Floor(h);
    }

    private static double Smooth(double e0, double e1, double x) => EffectReference.Smoothstep(e0, e1, x);

    private static C3 Saturate(C3 c) => c.Map(EffectReference.Saturate);

    // ----- Color -----

    /// <summary>La corrección de Color con saturación y ganancias dadas (la protección de piel la repite sin ellas).</summary>
    public static C3 GradeWith(C3 c, double exposure, double contrast, double saturation, C3 gains, double highlights, double shadows)
    {
        c *= Math.Pow(2, exposure);
        c = c.Map(v => ((v - 0.5) * (1 + contrast)) + 0.5);
        double luma = Luma(c);
        c = c.Map(v => luma + ((v - luma) * saturation));
        c = new C3(c.R * gains.R, c.G * gains.G, c.B * gains.B);
        luma = Luma(c);
        double lift = 0.25 * ((highlights * Smooth(0.5, 1, luma)) + (shadows * (1 - Smooth(0, 0.5, luma))));
        return Saturate(c.Map(v => v + lift));
    }

    public static double SkinWeight(C3 c)
    {
        double y = Luma(c);
        (double cb, double cr) = CbCr(c, y);
        double r = Math.Sqrt((cb * cb) + (cr * cr));
        double angle = Math.Atan2(cr, cb) * 180 / Math.PI;
        return (1 - Smooth(12, 28, Math.Abs(angle - 123))) * Smooth(0.015, 0.05, r) * (1 - Smooth(0.3, 0.45, r));
    }

    /// <summary>Saturación, temperatura y tinte con la protección de piel.</summary>
    public static C3 GradeProtected(C3 c, double saturation, double temperature, double tint, double protection)
    {
        var gains = new C3(1 + (0.2 * temperature), 1 - (0.2 * tint), 1 - (0.2 * temperature));
        C3 full = GradeWith(c, 0, 0, 1 + saturation, gains, 0, 0);
        C3 kept = GradeWith(c, 0, 0, 1, new C3(1, 1, 1), 0, 0);
        return C3.Mix(full, kept, protection * SkinWeight(c));
    }

    /// <summary>Viñeta con redondez y centro: con 0 y el centro en el medio es <see cref="EffectReference.Vignette"/>.</summary>
    public static double Vignette(double u, double v, double aspect, double amount, double size, double softness, double roundness,
        double centerX, double centerY)
    {
        double sx = 1 + ((aspect - 1) * roundness);
        double px = (u - centerX) * 2 * sx;
        double py = (v - centerY) * 2;
        double d = Math.Sqrt((px * px) + (py * py)) / Math.Sqrt((sx * sx) + 1);
        double t = EffectReference.Saturate((d - size) / Math.Max(softness, 1e-3));
        return 1 - (amount * t * t * (3 - (2 * t)));
    }

    // ----- Calificador y curvas de tono -----

    public readonly record struct Qualifier(double HueCenter, double HueWidth, double HueSoftness, double SatLow, double SatHigh,
        double LumaLow, double LumaHigh, double Softness, double HueShift, double Saturation, double Exposure, bool Invert);

    public static double QualifierMask(C3 c, Qualifier q)
    {
        double high = Math.Max(c.R, Math.Max(c.G, c.B));
        double low = Math.Min(c.R, Math.Min(c.G, c.B));
        double l = 0.5 * (high + low);
        double s = EffectReference.Saturate((high - low) / Math.Max(1 - Math.Abs((2 * l) - 1), 1e-4));
        double y = Luma(c);
        double rel = Hue(c) - (q.HueCenter / 360) + 0.5;
        double dh = Math.Abs(rel - Math.Floor(rel) - 0.5);
        double width = q.HueWidth / 360;
        double hueTerm = 1 - Smooth(width, width + Math.Max(q.HueSoftness / 360, 1e-4), dh);
        double soft = Math.Max(q.Softness, 1e-4);
        double satTerm = Smooth(q.SatLow - soft, q.SatLow, s) * (1 - Smooth(q.SatHigh, q.SatHigh + soft, s));
        double lumaTerm = Smooth(q.LumaLow - soft, q.LumaLow, y) * (1 - Smooth(q.LumaHigh, q.LumaHigh + soft, y));
        double m = hueTerm * satTerm * lumaTerm;
        return q.Invert ? 1 - m : m;
    }

    public static C3 Qualify(C3 c, Qualifier q)
    {
        double m = QualifierMask(c, q);
        double y = Luma(c);
        (double cb, double cr) = CbCr(c, y);
        double a = q.HueShift * Math.PI / 180;
        double gain = 1 + q.Saturation;
        C3 corrected = FromYcc(y, ((cb * Math.Cos(a)) - (cr * Math.Sin(a))) * gain, ((cb * Math.Sin(a)) + (cr * Math.Cos(a))) * gain);
        return C3.Mix(c, Saturate(corrected * Math.Pow(2, q.Exposure)), m);
    }

    /// <summary>Tono contra tono (giro de CbCr), tono contra saturación y luma contra saturación (ganancia 2y cada una).</summary>
    public static C3 HueCurves(C3 c, HueCurve hueHue, HueCurve hueSat, HueCurve lumaSat)
    {
        double y = Luma(c);
        double h = Hue(c);
        double shift = (hueHue.Evaluate(h) - 0.5) * 2 * Math.PI;
        double gain = 4 * hueSat.Evaluate(h) * lumaSat.Evaluate(EffectReference.Saturate(y));
        (double cb, double cr) = CbCr(c, y);
        return Saturate(FromYcc(y, ((cb * Math.Cos(shift)) - (cr * Math.Sin(shift))) * gain, ((cb * Math.Sin(shift)) + (cr * Math.Cos(shift))) * gain));
    }

    // ----- Pasadas del intermedio -----

    private static (C3 Color, double Alpha) Unpremultiply((C3 Color, double Alpha) p) =>
        (p.Alpha > 0 ? p.Color * (1 / p.Alpha) : new C3(0, 0, 0), p.Alpha);

    private static C3 ToLinear(C3 c) => c.Map(v => Math.Pow(Math.Max(v, 0), 2.2));

    private static C3 ToGamma(C3 c) => c.Map(v => Math.Pow(Math.Max(v, 0), 1 / 2.2));

    private static (C3, double) At(FloatImage image, double u, double v) => image.Sample(u, v);

    private static FloatImage Map(FloatImage source, int width, int height, Func<double, double, (C3 Color, double Alpha)> pixel)
    {
        var output = new FloatImage(width, height);
        output.Fill((x, y) => pixel((x + 0.5) / width, (y + 0.5) / height));
        return output;
    }

    /// <summary>Promedios de 2 × 2 mientras el radio pase del tope; el radio queda en píxeles del nivel.</summary>
    public static FloatImage Reduce(FloatImage image, ref double radius, double limit)
    {
        FloatImage current = image;
        while (radius > limit && current.Width > 4 && current.Height > 4)
        {
            var half = new FloatImage((current.Width + 1) / 2, (current.Height + 1) / 2);
            FloatImage source = current;
            half.Fill((x, y) => source.Sample((x + 0.5) / half.Width, (y + 0.5) / half.Height));
            radius *= (double)half.Width / current.Width;
            current = half;
        }

        return current;
    }

    public static uint Hash(uint x)
    {
        unchecked
        {
            x ^= x >> 16;
            x *= 0x7feb352du;
            x ^= x >> 15;
            x *= 0x846ca68bu;
            x ^= x >> 16;
            return x;
        }
    }

    private static double Noise(int cx, int cy, uint seed, uint channel)
    {
        unchecked
        {
            uint h = Hash(((uint)cx * 0x9e3779b9u) ^ Hash((uint)cy + (0x85ebca6bu * (seed + (channel * 0x632be5abu)))));
            return (h >> 8) * (1.0 / 16777216.0);
        }
    }

    private static double ValueNoise(double px, double py, uint seed, uint channel)
    {
        int cx = (int)Math.Floor(px), cy = (int)Math.Floor(py);
        double fx = px - cx, fy = py - cy;
        fx = fx * fx * (3 - (2 * fx));
        fy = fy * fy * (3 - (2 * fy));
        double a = Noise(cx, cy, seed, channel), b = Noise(cx + 1, cy, seed, channel);
        double d = Noise(cx, cy + 1, seed, channel), e = Noise(cx + 1, cy + 1, seed, channel);
        double top = a + ((b - a) * fx), bottom = d + ((e - d) * fx);
        return top + ((bottom - top) * fy) - 0.5;
    }

    public static FloatImage Grain(FloatImage source, double amount, double size, double color, double response, double seconds)
    {
        uint seed = Hash(BitConverter.SingleToUInt32Bits((float)seconds));
        return Map(source, source.Width, source.Height, (u, v) =>
        {
            (C3 c, double a) = Unpremultiply(At(source, u, v));
            double px = u * source.Width / size, py = v * source.Height / size;
            double mono = ValueNoise(px, py, seed, 0);
            var colored = new C3(ValueNoise(px, py, seed, 1), ValueNoise(px, py, seed, 2), ValueNoise(px, py, seed, 3));
            double y = Luma(c);
            double weight = 1 + (((4 * y * (1 - y)) - 1) * response);
            C3 n = C3.Mix(new C3(mono, mono, mono), colored, color);
            return (Saturate(c + (n * (amount * 0.7 * weight))) * a, a);
        });
    }

    public static FloatImage Glow(FloatImage source, double threshold, double radius, double intensity)
    {
        FloatImage bright = Map(source, source.Width, source.Height, (u, v) =>
        {
            (C3 c, double a) = Unpremultiply(At(source, u, v));
            double k = EffectReference.Saturate((Math.Max(c.R, Math.Max(c.G, c.B)) - threshold) / Math.Max(1 - threshold, 1e-3));
            return (ToLinear(c) * (k * k * a), a);
        });
        FloatImage halo = bright.Blur(radius / 3);
        return Map(source, source.Width, source.Height, (u, v) =>
        {
            (C3 c, double a) = Unpremultiply(At(source, u, v));
            (C3 h, _) = At(halo, u, v);
            return (Saturate(ToGamma(ToLinear(c) + (h * intensity))) * a, a);
        });
    }

    public static FloatImage Aberration(FloatImage source, bool lateral, double amount, double degrees)
    {
        double halfDiagonal = 0.5 * Math.Sqrt((source.Width * source.Width) + (source.Height * source.Height));
        double radians = degrees * Math.PI / 180;
        return Map(source, source.Width, source.Height, (u, v) =>
        {
            double ru, rv, bu, bv;
            if (lateral)
            {
                double du = Math.Cos(radians) * amount / source.Width, dv = Math.Sin(radians) * amount / source.Height;
                (ru, rv, bu, bv) = (u + du, v + dv, u - du, v - dv);
            }
            else
            {
                double k = amount / halfDiagonal;
                (ru, rv) = (0.5 + ((u - 0.5) * (1 + k)), 0.5 + ((v - 0.5) * (1 + k)));
                (bu, bv) = (0.5 + ((u - 0.5) * (1 - k)), 0.5 + ((v - 0.5) * (1 - k)));
            }

            (C3 g, double a) = At(source, u, v);
            return (new C3(At(source, ru, rv).Item1.R, g.G, At(source, bu, bv).Item1.B), a);
        });
    }

    public static FloatImage Mirror(FloatImage source, int mode, double axis, double tiles) =>
        Map(source, source.Width, source.Height, (u, v) =>
        {
            double mu = u, mv = v;
            if ((mode is 0 or 4) && mu > axis) mu = (2 * axis) - mu;
            if (mode == 1 && mu < axis) mu = (2 * axis) - mu;
            if ((mode is 2 or 4) && mv > axis) mv = (2 * axis) - mv;
            if (mode == 3 && mv < axis) mv = (2 * axis) - mv;
            if (mode == 5)
            {
                mu = (u * tiles) - Math.Floor(u * tiles);
                mv = (v * tiles) - Math.Floor(v * tiles);
            }

            return At(source, EffectReference.Saturate(mu), EffectReference.Saturate(mv));
        });

    public static FloatImage Reframe360(FloatImage source, double yaw, double pitch, double roll, double fov, bool stereographic) =>
        Map(source, source.Width, source.Height, (u, v) =>
        {
            (double su, double sv) = Rodaje.Core.Effects.Equirectangular.SourceUv(u, v, yaw, pitch, roll, fov, (double)source.Width / source.Height, stereographic);
            return At(source, su, sv);
        });

    public static FloatImage Distortion(FloatImage source, double amount, double zoom)
    {
        double aspect = (double)source.Width / source.Height;
        double k = 0.5 * amount;
        return Map(source, source.Width, source.Height, (u, v) =>
        {
            double dx = (u - 0.5) * aspect, dy = v - 0.5;
            double r2 = ((dx * dx) + (dy * dy)) * 4 / ((aspect * aspect) + 1);
            double su = 0.5 + (dx * (1 + (k * r2)) / zoom / aspect), sv = 0.5 + (dy * (1 + (k * r2)) / zoom);
            return su < 0 || sv < 0 || su > 1 || sv > 1 ? (new C3(0, 0, 0), 0) : At(source, su, sv);
        });
    }

    private static FloatImage Average(FloatImage source, int taps, Func<double, double, int, (double U, double V)> at) =>
        Map(source, source.Width, source.Height, (u, v) =>
        {
            C3 sum = new(0, 0, 0);
            double alpha = 0;
            for (int t = -taps; t <= taps; t++)
            {
                (double su, double sv) = at(u, v, t);
                (C3 c, double a) = At(source, su, sv);
                sum += c;
                alpha += a;
            }

            return (sum * (1.0 / ((2 * taps) + 1)), alpha / ((2 * taps) + 1));
        });

    public static FloatImage LineBlur(FloatImage source, double radius, double degrees, int taps = 12)
    {
        FloatImage level = Reduce(source, ref radius, 48);
        double radians = degrees * Math.PI / 180;
        double sx = Math.Cos(radians) * radius / taps / level.Width, sy = Math.Sin(radians) * radius / taps / level.Height;
        FloatImage coarse = Average(level, taps, (u, v, t) => (u + (sx * t), v + (sy * t)));
        double fine = 1.0 / ((2 * taps) + 1);
        return Average(coarse, taps, (u, v, t) => (u + (sx * fine * t), v + (sy * fine * t)));
    }

    public static FloatImage ZoomBlur(FloatImage source, double radius, double centerX, double centerY, int taps = 12)
    {
        double halfDiagonal = 0.5 * Math.Sqrt((source.Width * source.Width) + (source.Height * source.Height));
        double span = radius / halfDiagonal / taps;
        FloatImage coarse = Average(source, taps, (u, v, t) => (centerX + ((u - centerX) * (1 + (span * t))), centerY + ((v - centerY) * (1 + (span * t)))));
        double fine = span / ((2 * taps) + 1);
        return Average(coarse, taps, (u, v, t) => (centerX + ((u - centerX) * (1 + (fine * t))), centerY + ((v - centerY) * (1 + (fine * t)))));
    }

    public static FloatImage LensBlur(FloatImage source, double radius)
    {
        FloatImage linear = Map(source, source.Width, source.Height, (u, v) =>
        {
            (C3 c, double a) = Unpremultiply(At(source, u, v));
            return (ToLinear(c) * a, a);
        });
        FloatImage level = Reduce(linear, ref radius, 8);
        int taps = (int)Math.Clamp(Math.Ceiling(3.2 * radius * radius), 16, 160);
        double ru = radius / level.Width, rv = radius / level.Height;
        FloatImage disc = Map(level, level.Width, level.Height, (u, v) =>
        {
            C3 sum = new(0, 0, 0);
            double alpha = 0;
            for (int t = 0; t < taps; t++)
            {
                double r = Math.Sqrt((t + 0.5) / taps);
                double a = t * 2.39996323;
                (C3 c, double sa) = At(level, u + (Math.Cos(a) * r * ru), v + (Math.Sin(a) * r * rv));
                sum += c;
                alpha += sa;
            }

            return (sum * (1.0 / taps), alpha / taps);
        });
        return Map(disc, disc.Width, disc.Height, (u, v) =>
        {
            (C3 c, double a) = Unpremultiply(At(disc, u, v));
            return (ToGamma(c) * a, a);
        });
    }

    public static FloatImage Bilateral(FloatImage source, double strength, double detail)
    {
        double spatial = 0.8 + (1.2 * strength);
        double range = 0.12 - (0.095 * detail);
        double ks = 0.5 / (spatial * spatial), kr = 0.5 / (range * range);
        return Map(source, source.Width, source.Height, (u, v) =>
        {
            (C3 center, double ca) = At(source, u, v);
            double yc = Luma(Unpremultiply((center, ca)).Color);
            C3 sum = new(0, 0, 0);
            double alpha = 0, total = 0;
            for (int dy = -2; dy <= 2; dy++)
            {
                for (int dx = -2; dx <= 2; dx++)
                {
                    (C3 s, double sa) = At(source, u + ((double)dx / source.Width), v + ((double)dy / source.Height));
                    double dl = Luma(Unpremultiply((s, sa)).Color) - yc;
                    double w = Math.Exp((-((dx * dx) + (dy * dy)) * ks) - (dl * dl * kr));
                    sum += s * w;
                    alpha += sa * w;
                    total += w;
                }
            }

            return (C3.Mix(center, sum * (1 / total), strength), ca + (((alpha / total) - ca) * strength));
        });
    }

    public static FloatImage Temporal(FloatImage now, FloatImage before, double temporal, double detail) =>
        Map(now, now.Width, now.Height, (u, v) =>
        {
            (C3 n, double na) = At(now, u, v);
            (C3 b, double ba) = At(before, u, v);
            double diff = Math.Abs(Luma(Unpremultiply((n, na)).Color - Unpremultiply((b, ba)).Color));
            double w = 0.85 * temporal * (1 - Smooth(0.015 + (0.02 * (1 - detail)), 0.06 + (0.06 * (1 - detail)), diff));
            return (C3.Mix(n, b, w), na + ((ba - na) * w));
        });

    /// <summary>Máscara de enfoque con umbral: la diferencia de luma por debajo del umbral no se realza (rampa hasta el doble).</summary>
    public static FloatImage SharpenWithThreshold(FloatImage source, double amount, double sigma, double threshold)
    {
        FloatImage blurred = source.Blur(sigma);
        return Map(source, source.Width, source.Height, (u, v) =>
        {
            (C3 original, double alpha) = At(source, u, v);
            (C3 soft, double softAlpha) = At(blurred, u, v);
            C3 detail = original - soft;
            double k = threshold > 0 ? Smooth(threshold, 2 * threshold, Math.Abs(Luma(detail))) : 1;
            return (Saturate(original + (detail * (amount * k))), EffectReference.Saturate(alpha + (amount * k * (alpha - softAlpha))));
        });
    }
}
