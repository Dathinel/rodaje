using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Playback.Composition;
using TP = Rodaje.Core.Model.BuiltInTransitions.CommonParameters;

namespace Rodaje.Render.Tests.Support;

/// <summary>Color premultiplicado con alfa.</summary>
internal readonly record struct C4(double R, double G, double B, double A)
{
    public static readonly C4 Clear = new(0, 0, 0, 0);

    public static C4 operator +(C4 a, C4 b) => new(a.R + b.R, a.G + b.G, a.B + b.B, a.A + b.A);

    public static C4 operator *(C4 a, double k) => new(a.R * k, a.G * k, a.B * k, a.A * k);

    public static C4 Lerp(C4 a, C4 b, double t) => (a * (1 - t)) + (b * t);
}

/// <summary>
/// Las mezclas de las transiciones en la CPU, escritas desde sus fórmulas (docs/arquitectura/render.md, «Transiciones»):
/// cada lado es la imagen compuesta sola (premultiplicada, transparente fuera del cuadro).
/// </summary>
internal sealed class TransitionReference(FloatImage outgoing, FloatImage incoming)
{
    private readonly int _width = outgoing.Width;
    private readonly int _height = outgoing.Height;

    /// <summary>Imagen gris de una disolución por luma (o nula para el degradado de fábrica), de 0 a 1.</summary>
    public Func<double, double, double>? LumaImage { get; init; }

    /// <summary>El cuadro de la transición <paramref name="type"/> en el avance <paramref name="p"/>, o promediado en el obturador.</summary>
    public C4 At(string type, IReadOnlyDictionary<string, double> values, IReadOnlyDictionary<string, string> options, int x, int y, double p,
        (double From, double To)? shutter = null)
    {
        double u = (x + 0.5) / _width;
        double v = (y + 0.5) / _height;
        if (shutter is not { } open)
        {
            return Mix(type, values, options, u, v, p);
        }

        C4 sum = C4.Clear;
        for (int k = 0; k < 16; k++)
        {
            sum += Mix(type, values, options, u, v, open.From + ((open.To - open.From) * (k + 0.5) / 16));
        }

        return sum * (1.0 / 16);
    }

    private C4 Side(bool b, double u, double v)
    {
        if (u < 0 || u > 1 || v < 0 || v > 1)
        {
            return C4.Clear;
        }

        (C3 c, double a) = (b ? incoming : outgoing).Sample(u, v);
        return new C4(c.R, c.G, c.B, a);
    }

    private C4 Mirror(bool b, double u, double v)
    {
        static double M(double x)
        {
            x = Math.Abs(x);
            x -= 2 * Math.Floor(x * 0.5);
            return x > 1 ? 2 - x : x;
        }

        return Side(b, M(u), M(v));
    }

    private static double Smooth(double a, double b, double x)
    {
        double t = Math.Clamp((x - a) / (b - a), 0, 1);
        return t * t * (3 - (2 * t));
    }

    private static double Saturate(double x) => Math.Clamp(x, 0, 1);

    internal static uint Hash(uint x, uint y, uint z)
    {
        unchecked
        {
            uint h = (x * 0x8da6b343u) ^ (y * 0xd8163841u) ^ (z * 0xcb1ab31fu);
            h ^= h >> 15;
            h *= 0x2c1b3c6du;
            h ^= h >> 12;
            h *= 0x297a2d39u;
            h ^= h >> 15;
            return h;
        }
    }

    internal static double Rand(uint x, uint y, uint z) => (Hash(x, y, z) & 0xFFFFFFu) / 16777216.0;

    private static double Noise(double qx, double qy, uint seed)
    {
        double ix = Math.Floor(qx);
        double iy = Math.Floor(qy);
        double fx = qx - ix;
        double fy = qy - iy;
        double ux = fx * fx * (3 - (2 * fx));
        double uy = fy * fy * (3 - (2 * fy));
        uint x = (uint)(ix + 4096);
        uint y = (uint)(iy + 4096);
        double a = Rand(x, y, seed);
        double b = Rand(x + 1, y, seed);
        double c = Rand(x, y + 1, seed);
        double d = Rand(x + 1, y + 1, seed);
        double top = a + ((b - a) * ux);
        double bottom = c + ((d - c) * ux);
        return top + ((bottom - top) * uy);
    }

    private static double Fbm(double qx, double qy, uint seed) =>
        (Noise(qx, qy, seed) + (0.5 * Noise(qx * 2, qy * 2, seed + 1)) + (0.25 * Noise(qx * 4, qy * 4, seed + 2))) / 1.75;

    private double Along(double u, double v, double dx, double dy)
    {
        double px = (u - 0.5) * _width;
        double py = (v - 0.5) * _height;
        double extent = 0.5 * ((Math.Abs(dx) * _width) + (Math.Abs(dy) * _height));
        return ((px * dx) + (py * dy) + extent) / (2 * extent);
    }

    private C4 Threshold(double u, double v, double g, double p, double s, double bw, (double R, double G, double B) border)
    {
        double d = -bw + (p * (1 + s + bw)) - g;
        double t = Saturate(d / s);
        C4 m = C4.Lerp(Side(false, u, v), Side(true, u, v), t);
        if (bw > 0)
        {
            double weight = Saturate((d + bw) / 0.002) * (1 - t);
            m = C4.Lerp(m, new C4(border.R, border.G, border.B, 1), weight);
        }

        return m;
    }

    private static (double X, double Y) Direction(double degrees) => (Math.Cos(degrees * Math.PI / 180), Math.Sin(degrees * Math.PI / 180));

    private static (double X, double Y) Push(string direction) => direction switch
    {
        "right" => (-1, 0),
        "up" => (0, 1),
        "down" => (0, -1),
        _ => (1, 0),
    };

    private C4 Mix(string type, IReadOnlyDictionary<string, double> values, IReadOnlyDictionary<string, string> options, double u, double v, double p)
    {
        double V(string name) => values.TryGetValue(name, out double value) ? value : EffectRegistry.DefaultValue(type, name);
        string O(string name) => options.TryGetValue(name, out string? value) ? value : EffectRegistry.DefaultOption(type, name);
        if (type != BuiltInTransitions.Crossfade && type != BuiltInTransitions.Wipe && type != BuiltInTransitions.Push)
        {
            if (p <= 0)
            {
                return Side(false, u, v);
            }

            if (p >= 1)
            {
                return Side(true, u, v);
            }
        }

        switch (type)
        {
            case BuiltInTransitions.Wipe:
            {
                (double dx, double dy) = Direction(V(TP.Angle));
                return Threshold(u, v, Along(u, v, dx, dy), p, Math.Max(V(TP.Softness), 1e-4), V(TP.BorderWidth),
                    (V(TP.BorderRed), V(TP.BorderGreen), V(TP.BorderBlue)));
            }

            case BuiltInTransitions.Push:
            {
                (double dx, double dy) = Push(O(BuiltInTransitions.PushOptions.Direction));
                C4 a = Side(false, u + (dx * p), v + (dy * p));
                C4 b = Side(true, u - (dx * (1 - p)), v - (dy * (1 - p)));
                return b + (a * (1 - b.A));
            }

            case BuiltInTransitions.Slide:
            {
                (double dx, double dy) = Direction(V(TP.Angle));
                double scale = Math.Max(Math.Abs(dx), Math.Abs(dy));
                (dx, dy) = (dx / scale, dy / scale);
                double parallax = V(BuiltInTransitions.SlideParameters.Parallax);
                C4 b = Side(true, u + (dx * (1 - p)), v + (dy * (1 - p)));
                C4 a = Side(false, u - (dx * p * parallax), v - (dy * p * parallax));
                return b + (a * (1 - b.A));
            }

            case BuiltInTransitions.ZoomBlur:
            {
                double strength = V(BuiltInTransitions.ZoomBlurParameters.Strength);
                double cx = V(BuiltInTransitions.ZoomBlurParameters.CenterX);
                double cy = V(BuiltInTransitions.ZoomBlurParameters.CenterY);
                double length = strength * p * (1 - p);
                double t = Smooth(0.35, 0.65, p);
                C4 Zoomed(bool b, double scale)
                {
                    C4 sum = C4.Clear;
                    for (int k = 0; k < 16; k++)
                    {
                        double f = (1 - (length * k / 15)) / scale;
                        sum += Side(b, cx + ((u - cx) * f), cy + ((v - cy) * f));
                    }

                    return sum * (1.0 / 16);
                }

                return C4.Lerp(t < 1 ? Zoomed(false, 1 + (strength * p)) : C4.Clear, t > 0 ? Zoomed(true, 1 + (strength * (1 - p))) : C4.Clear, t);
            }

            case BuiltInTransitions.Spin:
            {
                double angle = V(BuiltInTransitions.SpinParameters.Angle);
                double scale = 1 + (V(BuiltInTransitions.SpinParameters.Zoom) * 4 * p * (1 - p));
                double t = Smooth(0.4, 0.6, p);
                C4 Spun(bool b, double degrees)
                {
                    double r = degrees * Math.PI / 180;
                    double px = (u - 0.5) * _width;
                    double py = (v - 0.5) * _height;
                    double qx = ((Math.Cos(r) * px) + (Math.Sin(r) * py)) / scale;
                    double qy = ((-Math.Sin(r) * px) + (Math.Cos(r) * py)) / scale;
                    return Mirror(b, (qx / _width) + 0.5, (qy / _height) + 0.5);
                }

                return C4.Lerp(t < 1 ? Spun(false, angle * p) : C4.Clear, t > 0 ? Spun(true, -angle * (1 - p)) : C4.Clear, t);
            }

            case BuiltInTransitions.LightWipe:
            {
                (double dx, double dy) = Direction(V(TP.Angle));
                double s = Math.Max(V(TP.Softness), 1e-4);
                double w = V(BuiltInTransitions.LightWipeParameters.Width);
                double d = -w + (p * (1 + s + (2 * w))) - Along(u, v, dx, dy);
                double t = Saturate(d / s);
                double glow = V(TP.Intensity) * Math.Pow(1 - Saturate(Math.Abs(d - (0.5 * s)) / w), 2);
                C4 m = C4.Lerp(Side(false, u, v), Side(true, u, v), t);
                return m with { R = m.R + (V(TP.Red) * glow * m.A), G = m.G + (V(TP.Green) * glow * m.A), B = m.B + (V(TP.Blue) * glow * m.A) };
            }

            case BuiltInTransitions.Glitch:
            {
                double py = Math.Floor(v * _height);
                uint band = (uint)Math.Floor(py * Math.Round(V(BuiltInTransitions.GlitchParameters.Bands)) / _height);
                uint step = (uint)Math.Floor(p * 12);
                uint seed = (uint)Math.Round(V(TP.Seed)) * 4;
                double w = V(BuiltInTransitions.GlitchParameters.Amount) * 4 * p * (1 - p);
                bool b = Rand(band, step, seed + 1) < p;
                double offset = Rand(band, step, seed + 2) < 0.6 ? (Rand(band, step, seed + 3) - 0.5) * 0.25 * w : 0;
                double split = V(BuiltInTransitions.GlitchParameters.Split) * w;
                C4 r = Side(b, u + offset + split, v);
                C4 g = Side(b, u + offset, v);
                C4 bl = Side(b, u + offset - split, v);
                return new C4(r.R, g.G, bl.B, Math.Max(r.A, Math.Max(g.A, bl.A)));
            }

            case BuiltInTransitions.FilmBurn:
            {
                double scale = V(BuiltInTransitions.FilmBurnParameters.Scale);
                double n = Fbm(u * _width / (_height * scale), v * _height / (_height * scale), (uint)Math.Round(V(TP.Seed)) * 4);
                double x = ((p * 1.75) - 0.25 - n) / 0.25;
                double t = Saturate(x - 1);
                double heat = Saturate(1 - Math.Abs(x - 1));
                C4 m = C4.Lerp(Side(false, u, v), Side(true, u, v), t);
                double light = (1.5 * heat) + (1.4 * p * (1 - p));
                return new C4((m.R * (1 + (2 * heat))) + (V(TP.Red) * light * m.A), (m.G * (1 + (2 * heat))) + (V(TP.Green) * light * m.A),
                    (m.B * (1 + (2 * heat))) + (V(TP.Blue) * light * m.A), m.A);
            }

            case BuiltInTransitions.Flash:
            {
                double width = V(BuiltInTransitions.FlashParameters.Width);
                double t = Saturate(((p - 0.5) / (width * 0.25)) + 0.5);
                double f = Saturate(1 - (Math.Abs(p - 0.5) / (width * 0.5)));
                f = V(TP.Intensity) * f * f;
                C4 m = C4.Lerp(Side(false, u, v), Side(true, u, v), t);
                return new C4((m.R * (1 + f)) + (V(TP.Red) * f * m.A), (m.G * (1 + f)) + (V(TP.Green) * f * m.A), (m.B * (1 + f)) + (V(TP.Blue) * f * m.A), m.A);
            }

            case BuiltInTransitions.Luma:
            {
                (double dx, double dy) = Direction(V(TP.Angle));
                double px = (u - 0.5) * _width;
                double py = (v - 0.5) * _height;
                double g = LumaImage is { } image ? image(u, v) : O(BuiltInTransitions.LumaOptions.Gradient) switch
                {
                    "radial" => Math.Sqrt((px * px) + (py * py)) / Math.Sqrt((0.25 * _width * _width) + (0.25 * _height * _height)),
                    "angular" => Frac((Math.Atan2(px, -py) / (2 * Math.PI)) + 1 + (V(TP.Angle) / 360)),
                    "diamond" => (Math.Abs(px) + Math.Abs(py)) / (0.5 * (_width + _height)),
                    "noise" => Fbm(u * _width / (_height * 0.18), v * _height / (_height * 0.18), 7),
                    "bars" => Frac(Along(u, v, dx, dy) * 8),
                    _ => Along(u, v, dx, dy),
                };
                g = O(BuiltInTransitions.LumaOptions.Invert) == "on" ? 1 - g : g;
                return Threshold(u, v, g, p, Math.Max(V(TP.Softness), 1e-4), V(TP.BorderWidth), (V(TP.BorderRed), V(TP.BorderGreen), V(TP.BorderBlue)));
            }

            case BuiltInTransitions.Bounce:
            {
                (double dx, double dy) = Push(O(BuiltInTransitions.PushOptions.Direction));
                double w = p - 1;
                double c1 = 4 * V(BuiltInTransitions.BounceParameters.Bounce);
                double q = 1 + ((c1 + 1) * w * w * w) + (c1 * w * w);
                C4 a = Side(false, u + (dx * q), v + (dy * q));
                double bu = u - (dx * (1 - q));
                double bv = v - (dy * (1 - q));
                C4 b = Side(true, bu, bv);
                if (b.A <= 0 && a.A <= 0 && q > 1)
                {
                    b = Mirror(true, bu, bv);
                }

                return b + (a * (1 - b.A));
            }

            default:
                return C4.Lerp(Side(false, u, v), Side(true, u, v), p);
        }
    }

    private static double Frac(double x) => x - Math.Floor(x);
}

/// <summary>Valores como los arma el motor: parámetros por nombre y opciones de elección por su índice tras los parámetros.</summary>
internal static class TransitionValues
{
    public static EffectValues Of(string type, IReadOnlyDictionary<string, double> values, IReadOnlyDictionary<string, string> options)
    {
        EffectDefinition definition = EffectRegistry.Find(type)!;
        EffectValues slots = default;
        foreach (ParameterDefinition parameter in definition.Parameters)
        {
            slots[parameter.Slot] = (float)(values.TryGetValue(parameter.Name, out double v) ? v : parameter.DefaultValue);
        }

        int slot = definition.Parameters.Length;
        foreach (OptionDefinition option in definition.Options.Where(o => o.Kind == OptionKind.Choice))
        {
            slots[slot++] = Math.Max(0, option.Choices.IndexOf(options.TryGetValue(option.Name, out string? chosen) ? chosen : option.DefaultValue));
        }

        return slots;
    }
}
