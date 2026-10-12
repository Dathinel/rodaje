using System.Globalization;
using Rodaje.Core.Model;
using P = Rodaje.Core.Model.BuiltInEffects.CropParameters;

namespace Rodaje.Core.Effects;

/// <summary>Lo que se quita de cada borde, en fracción de la imagen de la capa.</summary>
public readonly record struct CropRect(double Left, double Top, double Right, double Bottom)
{
    public static readonly CropRect None = new(0, 0, 0, 0);

    /// <summary>Ancho que queda, en fracción del ancho de la imagen.</summary>
    public double Width => 1 - Left - Right;

    public double Height => 1 - Top - Bottom;

    public bool IsNone => Left == 0 && Top == 0 && Right == 0 && Bottom == 0;
}

/// <summary>Qué parte del recorte se arrastra en el visor.</summary>
public enum CropGrip
{
    None,
    Body,
    TopLeft,
    TopRight,
    BottomRight,
    BottomLeft,
    Left,
    Top,
    Right,
    Bottom,
}

/// <summary>
/// Recorte de <see cref="BuiltInEffects.Crop"/>: lectura, límites y arrastres con proporción fija. Trabaja en píxeles de la
/// imagen en pantalla (con su relación de aspecto y rotación), porque «16:9» es la forma de lo que se ve.
/// </summary>
public static class CropGeometry
{
    /// <summary>Lo menos que queda de cada lado: el 2 % de la imagen.</summary>
    public const double MinSize = 0.02;

    /// <summary>Lo más que se quita de un borde.</summary>
    public const double MaxCrop = 1 - MinSize;

    /// <summary>Ancho entre alto de <paramref name="aspect"/> (<c>16:9</c>…); nulo si es libre o no se reconoce.</summary>
    public static double? Ratio(string? aspect)
    {
        if (aspect is null || aspect.IndexOf(':', StringComparison.Ordinal) is not (> 0 and var colon)
            || !double.TryParse(aspect.AsSpan(0, colon), NumberStyles.Float, CultureInfo.InvariantCulture, out double w)
            || !double.TryParse(aspect.AsSpan(colon + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double h)
            || !(w > 0) || !(h > 0) || !double.IsFinite(w / h))
        {
            return null;
        }

        return w / h;
    }

    /// <summary>Los bordes del efecto en <paramref name="sourceTime"/>, ya dentro de los límites.</summary>
    public static CropRect Evaluate(EffectInstance effect, Timing.Time sourceTime)
    {
        ArgumentNullException.ThrowIfNull(effect);
        double Value(string name) => effect.Parameters.TryGetValue(name, out Parameter? p) ? p.Evaluate(sourceTime) : 0;
        return Sanitize(new CropRect(Value(P.Left), Value(P.Top), Value(P.Right), Value(P.Bottom)));
    }

    /// <summary>Cada borde entre 0 y <see cref="MaxCrop"/>, sin NaN, y si dos opuestos se cruzan se reducen a la par hasta dejar <see cref="MinSize"/>.</summary>
    public static CropRect Sanitize(CropRect crop)
    {
        static double Side(double v) => double.IsFinite(v) ? Math.Clamp(v, 0, MaxCrop) : 0;
        double l = Side(crop.Left), t = Side(crop.Top), r = Side(crop.Right), b = Side(crop.Bottom);
        Fit(ref l, ref r);
        Fit(ref t, ref b);
        return new CropRect(l, t, r, b);

        static void Fit(ref double a, ref double b)
        {
            double sum = a + b;
            if (sum > MaxCrop)
            {
                a *= MaxCrop / sum;
                b *= MaxCrop / sum;
            }
        }
    }

    /// <summary>
    /// El recorte más grande con proporción <paramref name="ratio"/> (ancho:alto en pantalla) centrado donde está el actual,
    /// corrido lo justo para no salirse de la imagen.
    /// </summary>
    public static CropRect FitAspect(CropRect current, double ratio, double imageWidth, double imageHeight)
    {
        if (!(ratio > 0) || !(imageWidth > 0) || !(imageHeight > 0))
        {
            return current;
        }

        current = Sanitize(current);
        double cx = (current.Left + (current.Width / 2)) * imageWidth;
        double cy = (current.Top + (current.Height / 2)) * imageHeight;
        double w = Math.Min(imageWidth, imageHeight * ratio);
        double h = w / ratio;
        double x0 = Math.Clamp(cx - (w / 2), 0, imageWidth - w);
        double y0 = Math.Clamp(cy - (h / 2), 0, imageHeight - h);
        return FromPixels(x0, y0, x0 + w, y0 + h, imageWidth, imageHeight);
    }

    /// <summary>
    /// El recorte tras arrastrar <paramref name="grip"/> desde <paramref name="start"/> un desplazamiento de
    /// (<paramref name="dx"/>, <paramref name="dy"/>) en fracción de la imagen. Con <paramref name="ratio"/>, una esquina
    /// crece desde la opuesta con la proporción y un borde cambia también el otro lado, centrado; nunca sale de la imagen.
    /// </summary>
    public static CropRect Drag(CropRect start, CropGrip grip, double dx, double dy, double? ratio, double imageWidth, double imageHeight)
    {
        double width = imageWidth, height = imageHeight;
        if (!(width > 0) || !(height > 0) || !double.IsFinite(dx) || !double.IsFinite(dy) || grip == CropGrip.None)
        {
            return start;
        }

        start = Sanitize(start);
        double x0 = start.Left * width, x1 = (1 - start.Right) * width, y0 = start.Top * height, y1 = (1 - start.Bottom) * height;
        if (grip == CropGrip.Body)
        {
            double mx = Math.Clamp(dx * width, -x0, width - x1);
            double my = Math.Clamp(dy * height, -y0, height - y1);
            return FromPixels(x0 + mx, y0 + my, x1 + mx, y1 + my, width, height);
        }

        (int sx, int sy) = grip switch
        {
            CropGrip.TopLeft => (-1, -1),
            CropGrip.TopRight => (1, -1),
            CropGrip.BottomRight => (1, 1),
            CropGrip.BottomLeft => (-1, 1),
            CropGrip.Left => (-1, 0),
            CropGrip.Right => (1, 0),
            CropGrip.Top => (0, -1),
            _ => (0, 1),
        };
        double w = x1 - x0, h = y1 - y0;
        if (sx != 0)
        {
            w = sx < 0 ? x1 - (x0 + (dx * width)) : x1 + (dx * width) - x0;
        }

        if (sy != 0)
        {
            h = sy < 0 ? y1 - (y0 + (dy * height)) : y1 + (dy * height) - y0;
        }

        double maxW = sx < 0 ? x1 : sx > 0 ? width - x0 : width;
        double maxH = sy < 0 ? y1 : sy > 0 ? height - y0 : height;
        double min = MinSize * Math.Min(width, height);
        if (ratio is { } r && r > 0 && double.IsFinite(r))
        {
            if (sx != 0 && sy != 0)
            {
                w = Math.Max(w, h * r);
            }
            else if (sy != 0)
            {
                w = h * r;
            }

            double limit = Math.Min(maxW, maxH * r);
            w = Math.Clamp(w, Math.Min(Math.Max(min, min * r), limit), limit);
            h = w / r;
        }
        else
        {
            w = Math.Clamp(w, Math.Min(min, maxW), maxW);
            h = Math.Clamp(h, Math.Min(min, maxH), maxH);
        }

        double nx = sx < 0 ? x1 - w : sx > 0 ? x0 : Math.Clamp(((x0 + x1) / 2) - (w / 2), 0, width - w);
        double ny = sy < 0 ? y1 - h : sy > 0 ? y0 : Math.Clamp(((y0 + y1) / 2) - (h / 2), 0, height - h);
        return FromPixels(nx, ny, nx + w, ny + h, width, height);
    }

    private static CropRect FromPixels(double x0, double y0, double x1, double y1, double width, double height) =>
        Sanitize(new CropRect(
            Math.Round(x0 / width, 5),
            Math.Round(y0 / height, 5),
            Math.Round(1 - (x1 / width), 5),
            Math.Round(1 - (y1 / height), 5)));
}
