using Rodaje.Media.Video;

namespace Rodaje.Render.Tests.Support;

/// <summary>Códigos Y'CbCr de una muestra, en la profundidad de la imagen (0–255 u 0–1023).</summary>
internal readonly record struct YuvCode(int Y, int Cb, int Cr);

internal readonly record struct Rgb(double R, double G, double B)
{
    public static Rgb Mix(Rgb a, Rgb b, double t) => new(a.R + ((b.R - a.R) * t), a.G + ((b.G - a.G) * t), a.B + ((b.B - a.B) * t));
}

/// <summary>Conversión de referencia con las ecuaciones de las normas ITU-R, independiente de <c>YuvRange</c> y de los shaders.</summary>
internal static class ReferenceColor
{
    public static (double Kr, double Kb) Weights(YuvMatrix matrix) => matrix switch
    {
        YuvMatrix.Bt601 => (0.299, 0.114),
        YuvMatrix.Bt2020 => (0.2627, 0.0593),
        _ => (0.2126, 0.0722),
    };

    /// <summary>R'G'B' de 8 bits a códigos cuantizados.</summary>
    public static YuvCode Encode(int r8, int g8, int b8, VideoColor color, bool tenBit)
    {
        (double kr, double kb) = Weights(color.Matrix);
        double r = r8 / 255.0;
        double g = g8 / 255.0;
        double b = b8 / 255.0;
        double y = (kr * r) + ((1 - kr - kb) * g) + (kb * b);
        double cb = (b - y) / (2 * (1 - kb));
        double cr = (r - y) / (2 * (1 - kr));
        int bits = tenBit ? 10 : 8;
        double max = (1 << bits) - 1;
        double scale = 1 << (bits - 8);
        double center = 1 << (bits - 1);
        int Q(double v) => (int)Math.Clamp(Math.Round(v), 0, max);
        return color.FullRange
            ? new YuvCode(Q(y * max), Q(center + (cb * max)), Q(center + (cr * max)))
            : new YuvCode(Q((16 + (219 * y)) * scale), Q((128 + (224 * cb)) * scale), Q((128 + (224 * cr)) * scale));
    }

    /// <summary>Códigos a R'G'B' en 0–255 (sin redondear), recortado a la gama como lo hace el shader.</summary>
    public static Rgb Decode(YuvCode code, VideoColor color, bool tenBit)
    {
        (double kr, double kb) = Weights(color.Matrix);
        int bits = tenBit ? 10 : 8;
        double max = (1 << bits) - 1;
        double scale = 1 << (bits - 8);
        double y;
        double cb;
        double cr;
        if (color.FullRange)
        {
            y = code.Y / max;
            cb = (code.Cb - (max + 1) / 2) / max;
            cr = (code.Cr - (max + 1) / 2) / max;
        }
        else
        {
            y = ((code.Y / scale) - 16) / 219;
            cb = ((code.Cb / scale) - 128) / 224;
            cr = ((code.Cr / scale) - 128) / 224;
        }

        double kg = 1 - kr - kb;
        double r = y + (2 * (1 - kr) * cr);
        double b = y + (2 * (1 - kb) * cb);
        double g = (y - (kr * r) - (kb * b)) / kg;
        return new Rgb(Clamp(r), Clamp(g), Clamp(b));
    }

    private static double Clamp(double v) => Math.Clamp(v, 0, 1) * 255;
}
