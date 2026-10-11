using Rodaje.Media.Export;
using Rodaje.Render.Export;

namespace Rodaje.Render.Tests.Export;

/// <summary>Imagen RGBA premultiplicada en la CPU, con los valores ya redondeados a <see cref="Half"/> como en la GPU.</summary>
internal sealed class Picture
{
    private readonly double[] _data;

    public Picture(int width, int height)
    {
        Width = width;
        Height = height;
        _data = new double[width * height * 4];
    }

    public int Width { get; }

    public int Height { get; }

    public (double R, double G, double B, double A) this[int x, int y]
    {
        get
        {
            int i = ((y * Width) + x) * 4;
            return (_data[i], _data[i + 1], _data[i + 2], _data[i + 3]);
        }
    }

    /// <summary>Guarda R'G'B' recto y alfa: se premultiplica y se redondea a Half.</summary>
    public Picture Set(int x, int y, double r, double g, double b, double a = 1)
    {
        int i = ((y * Width) + x) * 4;
        _data[i] = (double)(Half)(r * a);
        _data[i + 1] = (double)(Half)(g * a);
        _data[i + 2] = (double)(Half)(b * a);
        _data[i + 3] = (double)(Half)a;
        return this;
    }

    public Picture SetPremultiplied(int x, int y, double r, double g, double b, double a)
    {
        int i = ((y * Width) + x) * 4;
        (_data[i], _data[i + 1], _data[i + 2], _data[i + 3]) = (r, g, b, a);
        return this;
    }

    public Picture Fill(int x0, int y0, int width, int height, double r, double g, double b, double a = 1)
    {
        for (int y = y0; y < y0 + height; y++)
        {
            for (int x = x0; x < x0 + width; x++)
            {
                Set(x, y, r, g, b, a);
            }
        }

        return this;
    }

    public Half[] ToHalves()
    {
        var halves = new Half[_data.Length];
        for (int i = 0; i < halves.Length; i++)
        {
            halves[i] = (Half)_data[i];
        }

        return halves;
    }

    /// <summary>Nivel <paramref name="level"/> de mip: promedio de cajas de 2^nivel (las medidas son múltiplos).</summary>
    public Picture Mip(int level)
    {
        if (level == 0)
        {
            return this;
        }

        int box = 1 << level;
        var mip = new Picture(Width / box, Height / box);
        for (int y = 0; y < mip.Height; y++)
        {
            for (int x = 0; x < mip.Width; x++)
            {
                double r = 0, g = 0, b = 0, a = 0;
                for (int j = 0; j < box; j++)
                {
                    for (int i = 0; i < box; i++)
                    {
                        (double pr, double pg, double pb, double pa) = this[(x * box) + i, (y * box) + j];
                        r += pr;
                        g += pg;
                        b += pb;
                        a += pa;
                    }
                }

                double n = box * box;
                mip.SetPremultiplied(x, y, r / n, g / n, b / n, a / n);
            }
        }

        return mip;
    }

    /// <summary>Degradados, parches de color con bordes duros y, si se pide, alfa variable: lo peor para el croma.</summary>
    public static Picture Test(int width, int height, bool alpha = false, int seed = 1)
    {
        var picture = new Picture(width, height);
        var random = new Random(seed);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double u = (double)x / Math.Max(1, width - 1);
                double v = (double)y / Math.Max(1, height - 1);
                double a = alpha ? Math.Clamp(1.2 - u, 0, 1) : 1;
                if (((x / 8) + (y / 8)) % 3 == 0)
                {
                    picture.Set(x, y, random.Next(256) / 255.0, random.Next(256) / 255.0, random.Next(256) / 255.0, a);
                }
                else
                {
                    picture.Set(x, y, u, v, 1 - (0.5 * (u + v)), a);
                }
            }
        }

        return picture;
    }
}

/// <summary>
/// Referencia del conversor en la CPU, en doble precisión: geometría de <see cref="ExportGeometry"/> (probada aparte),
/// núcleos, Y'CbCr de BT.601/BT.709 con las ecuaciones de las normas, croma «left» y el mismo tramado de Bayer 4×4.
/// </summary>
internal static class ExportReference
{
    private static readonly int[] Bayer = [0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5];

    /// <summary>La composición llevada a la salida; fuera de <see cref="ScalePlan.Rect"/>, barras.</summary>
    public static Picture Scale(Picture source, ScalePlan plan, int width, int height, bool transparentBars,
        Picture? second = null, double weight = 0)
    {
        Picture first = source.Mip(plan.Level);
        Picture? other = second?.Mip(plan.Level);
        var result = new Picture(width, height);
        ContentRect rect = plan.Rect;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double px = x + 0.5 - rect.X;
                double py = y + 0.5 - rect.Y;
                if (px < 0 || py < 0 || px >= rect.Width || py >= rect.Height)
                {
                    result.SetPremultiplied(x, y, 0, 0, 0, transparentBars ? 0 : 1);
                    continue;
                }

                double sx = (px / rect.Width * first.Width) - 0.5;
                double sy = (py / rect.Height * first.Height) - 0.5;
                var c = Filter(first, plan, sx, sy);
                if (other is not null)
                {
                    var d = Filter(other, plan, sx, sy);
                    c = (Lerp(c.R, d.R, weight), Lerp(c.G, d.G, weight), Lerp(c.B, d.B, weight), Lerp(c.A, d.A, weight));
                }

                result.SetPremultiplied(x, y, c.R, c.G, c.B, c.A);
            }
        }

        return result;
    }

    /// <summary>Muestras esperadas de cada plano, fila tras fila, sin relleno (las de 16 bits ya sin el desplazamiento de P010).</summary>
    public static int[][] Convert(Picture picture, ExportVideoFormat format, bool dither)
    {
        ExportPixelFormat pixels = format.PixelFormat;
        PlaneKind[] kinds = ExportLayout.Kinds(pixels);
        int bits = pixels.BitDepth();
        double max = (1 << bits) - 1;
        bool useDither = dither && bits == 8;
        bool straight = pixels.HasAlpha();
        bool halfWidth = pixels.Chroma() != ChromaSubsampling.Chroma444;
        bool halfHeight = pixels.HalfHeightChroma();
        (double kr, double kb) = format.Color.Matrix == ExportMatrix.Bt601 ? (0.299, 0.114) : (0.2126, 0.0722);
        double unit = Math.Pow(2, bits - 8);
        (double ys, double yo, double cs, double co) = format.Color.FullRange
            ? (max, 0, max, 1 << (bits - 1))
            : (219 * unit, 16 * unit, 224 * unit, 128 * unit);
        int w = format.Width;
        int h = format.Height;

        (double R, double G, double B, double A) Pixel(int x, int y)
        {
            (double r, double g, double b, double a) = picture[Math.Clamp(x, 0, w - 1), Math.Clamp(y, 0, h - 1)];
            a = Math.Clamp(a, 0, 1);
            if (straight)
            {
                (r, g, b) = a > 0 ? (r / a, g / a, b / a) : (0, 0, 0);
            }

            return (Math.Clamp(r, 0, 1), Math.Clamp(g, 0, 1), Math.Clamp(b, 0, 1), a);
        }

        double Luma((double R, double G, double B, double A) p) => (kr * p.R) + ((1 - kr - kb) * p.G) + (kb * p.B);

        (double Cb, double Cr) CbCr((double R, double G, double B, double A) p)
        {
            double y = Luma(p);
            return ((p.B - y) / (2 * (1 - kb)), (p.R - y) / (2 * (1 - kr)));
        }

        (double Cb, double Cr) Row(int x, int y)
        {
            if (!halfWidth)
            {
                return CbCr(Pixel(x, y));
            }

            var l = CbCr(Pixel(x - 1, y));
            var c = CbCr(Pixel(x, y));
            var r = CbCr(Pixel(x + 1, y));
            return ((l.Cb + (2 * c.Cb) + r.Cb) / 4, (l.Cr + (2 * c.Cr) + r.Cr) / 4);
        }

        (double Cb, double Cr) Chroma(int cx, int cy)
        {
            int x = halfWidth ? cx * 2 : cx;
            int y = halfHeight ? cy * 2 : cy;
            var c = Row(x, y);
            if (halfHeight)
            {
                var d = Row(x, y + 1);
                c = ((c.Cb + d.Cb) / 2, (c.Cr + d.Cr) / 2);
            }

            return c;
        }

        double D(int x, int y) => useDither ? ((Bayer[((y & 3) * 4) + (x & 3)] + 0.5) / 16) - 0.5 : 0;
        int Q(double v, double d) => (int)Math.Clamp(Math.Floor(v + 0.5 + d), 0, max);

        var planes = new int[kinds.Length][];
        for (int p = 0; p < kinds.Length; p++)
        {
            (int samples, int rows) = pixels.PlaneSize(p, w, h);
            var plane = new int[samples * rows];
            planes[p] = plane;
            for (int y = 0; y < rows; y++)
            {
                switch (kinds[p])
                {
                    case PlaneKind.Luma:
                        for (int x = 0; x < samples; x++)
                        {
                            plane[(y * samples) + x] = Q((Luma(Pixel(x, y)) * ys) + yo, D(x, y));
                        }

                        break;
                    case PlaneKind.Alpha:
                        for (int x = 0; x < samples; x++)
                        {
                            plane[(y * samples) + x] = Q(Pixel(x, y).A * max, 0);
                        }

                        break;
                    case PlaneKind.Cb:
                    case PlaneKind.Cr:
                        for (int x = 0; x < samples; x++)
                        {
                            var c = Chroma(x, y);
                            plane[(y * samples) + x] = Q(((kinds[p] == PlaneKind.Cb ? c.Cb : c.Cr) * cs) + co, D(x, y));
                        }

                        break;
                    case PlaneKind.ChromaPairs:
                        for (int x = 0; x < samples / 2; x++)
                        {
                            var c = Chroma(x, y);
                            plane[(y * samples) + (2 * x)] = Q((c.Cb * cs) + co, D(x, y));
                            plane[(y * samples) + (2 * x) + 1] = Q((c.Cr * cs) + co, D(x, y));
                        }

                        break;
                    default:
                        int channels = kinds[p] == PlaneKind.Rgb24 ? 3 : 4;
                        for (int x = 0; x < w; x++)
                        {
                            var px = Pixel(x, y);
                            double d = D(x, y);
                            int i = (y * samples) + (x * channels);
                            plane[i] = Q(px.R * max, d);
                            plane[i + 1] = Q(px.G * max, d);
                            plane[i + 2] = Q(px.B * max, d);
                            if (channels == 4)
                            {
                                plane[i + 3] = Q(px.A * max, 0);
                            }
                        }

                        break;
                }
            }
        }

        return planes;
    }

    /// <summary>Muestras de un plano del cuadro bajado, en el mismo orden que <see cref="Convert"/>.</summary>
    public static int[] Read(in ExportVideoFrame frame, int plane)
    {
        ExportPixelFormat pixels = frame.Format.PixelFormat;
        (int samples, int rows) = pixels.PlaneSize(plane, frame.Format.Width, frame.Format.Height);
        bool wide = pixels.BytesPerSample() == 2;
        int shift = pixels == ExportPixelFormat.P010 ? 6 : 0;
        var values = new int[samples * rows];
        for (int y = 0; y < rows; y++)
        {
            ReadOnlySpan<byte> row = frame.Row(plane, y);
            for (int x = 0; x < samples; x++)
            {
                int value = wide ? row[2 * x] | (row[(2 * x) + 1] << 8) : row[x];
                if (shift > 0 && (value & ((1 << shift) - 1)) != 0)
                {
                    throw new InvalidOperationException($"P010 con bits bajos en el plano {plane} ({x}, {y}): {value}.");
                }

                values[(y * samples) + x] = value >> shift;
            }
        }

        return values;
    }

    private static double Lerp(double a, double b, double t) => a + ((b - a) * t);

    private static (double R, double G, double B, double A) Filter(Picture level, ScalePlan plan, double sx, double sy)
    {
        switch (plan.Kernel)
        {
            case ScaleKernel.Point:
                return level[Math.Clamp((int)Math.Round(sx), 0, level.Width - 1), Math.Clamp((int)Math.Round(sy), 0, level.Height - 1)];
            case ScaleKernel.Bilinear:
            {
                int x0 = (int)Math.Floor(sx);
                int y0 = (int)Math.Floor(sy);
                double fx = sx - x0;
                double fy = sy - y0;
                var acc = (R: 0.0, G: 0.0, B: 0.0, A: 0.0);
                for (int j = 0; j < 2; j++)
                {
                    for (int i = 0; i < 2; i++)
                    {
                        double wgt = (i == 0 ? 1 - fx : fx) * (j == 0 ? 1 - fy : fy);
                        var p = level[Math.Clamp(x0 + i, 0, level.Width - 1), Math.Clamp(y0 + j, 0, level.Height - 1)];
                        acc = (acc.R + (wgt * p.R), acc.G + (wgt * p.G), acc.B + (wgt * p.B), acc.A + (wgt * p.A));
                    }
                }

                return acc;
            }

            default:
            {
                double support = plan.Kernel == ScaleKernel.CatmullRom ? 2 : 3;
                double reachX = support * plan.WidenX;
                double reachY = support * plan.WidenY;
                var acc = (R: 0.0, G: 0.0, B: 0.0, A: 0.0);
                double total = 0;
                for (int y = (int)Math.Floor(sy - reachY) + 1; y <= (int)Math.Floor(sy + reachY); y++)
                {
                    double wy = Weight(plan.Kernel, (y - sy) / plan.WidenY);
                    for (int x = (int)Math.Floor(sx - reachX) + 1; x <= (int)Math.Floor(sx + reachX); x++)
                    {
                        double wgt = wy * Weight(plan.Kernel, (x - sx) / plan.WidenX);
                        var p = level[Math.Clamp(x, 0, level.Width - 1), Math.Clamp(y, 0, level.Height - 1)];
                        acc = (acc.R + (wgt * p.R), acc.G + (wgt * p.G), acc.B + (wgt * p.B), acc.A + (wgt * p.A));
                        total += wgt;
                    }
                }

                return (acc.R / total, acc.G / total, acc.B / total, acc.A / total);
            }
        }
    }

    private static double Weight(ScaleKernel kernel, double t)
    {
        t = Math.Abs(t);
        if (kernel == ScaleKernel.CatmullRom)
        {
            return t < 1 ? (((1.5 * t) - 2.5) * t * t) + 1
                : t < 2 ? (((((-0.5 * t) + 2.5) * t) - 4) * t) + 2
                : 0;
        }

        if (t < 1e-5)
        {
            return 1;
        }

        if (t >= 3)
        {
            return 0;
        }

        double pt = Math.PI * t;
        return 3 * Math.Sin(pt) * Math.Sin(pt / 3) / (pt * pt);
    }
}
