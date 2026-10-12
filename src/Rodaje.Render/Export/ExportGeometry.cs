using Rodaje.Media.Export;

namespace Rodaje.Render.Export;

/// <summary>Rectángulo, en píxeles de la salida, donde cae la composición entera (puede salirse del cuadro al recortar).</summary>
internal readonly record struct ContentRect(int X, int Y, int Width, int Height)
{
    public bool Covers(int width, int height) => X == 0 && Y == 0 && Width == width && Height == height;
}

/// <summary>Región de la composición que se exporta (recorte espacial), en píxeles de la secuencia.</summary>
public readonly record struct ExportSourceRegion(int X, int Y, int Width, int Height);

/// <summary>Núcleo con que se remuestrea; los valores son los de <c>options.x</c> en <see cref="ExportShaders"/>.</summary>
internal enum ScaleKernel
{
    /// <summary>Copia texel a texel: mismo tamaño en el nivel que se lee.</summary>
    Point = 0,
    Bilinear = 1,
    CatmullRom = 2,
    Lanczos3 = 3,
}

/// <summary>
/// Cómo se pasa de la composición al cuadro de salida: qué nivel de mip se lee, con qué núcleo y cuánto se ensancha
/// (al reducir, el núcleo se estira 1/escala para no producir aliasing).
/// </summary>
/// <param name="PassThrough">El nivel leído ya es el cuadro de salida: la conversión lo lee directamente, sin pasada de escala.</param>
internal readonly record struct ScalePlan(ContentRect Rect, int Level, ScaleKernel Kernel, float WidenX, float WidenY, bool PassThrough);

internal static class ExportGeometry
{
    /// <summary>Lo más que se ensancha el núcleo (una reducción de 8× o más con un filtro que no es Auto).</summary>
    public const float MaxWiden = 8;

    /// <summary>
    /// Dónde cae la composición de <paramref name="sourceWidth"/>×<paramref name="sourceHeight"/> en la salida. Con
    /// croma submuestreado, el borde de las barras cae en una muestra de croma (posición y tamaño pares) para que el
    /// croma de la imagen no se mezcle con el negro.
    /// </summary>
    public static ContentRect Place(int sourceWidth, int sourceHeight, int width, int height, ExportFit fit, bool evenX = false, bool evenY = false)
    {
        if (fit == ExportFit.Stretch || (long)sourceWidth * height == (long)sourceHeight * width)
        {
            return new ContentRect(0, 0, width, height);
        }

        double scale = fit == ExportFit.Bars
            ? Math.Min((double)width / sourceWidth, (double)height / sourceHeight)
            : Math.Max((double)width / sourceWidth, (double)height / sourceHeight);
        (int x, int w) = Axis(sourceWidth * scale, width, fit == ExportFit.Bars && evenX);
        (int y, int h) = Axis(sourceHeight * scale, height, fit == ExportFit.Bars && evenY);
        return new ContentRect(x, y, w, h);
    }

    /// <summary>
    /// Último nivel de mip que se puede leer: la composición y la región deben caer en texels enteros de ese nivel
    /// (cada nivel es el promedio 2×2 exacto del anterior).
    /// </summary>
    public static int MaxLevel(int sourceWidth, int sourceHeight, ExportSourceRegion region)
    {
        int level = (int)Math.Log2(Math.Max(sourceWidth, sourceHeight));
        if (region.Width == sourceWidth && region.Height == sourceHeight)
        {
            return level;
        }

        foreach (int value in (int[])[region.X, region.Y, region.Width, region.Height, sourceWidth, sourceHeight])
        {
            if (value != 0)
            {
                level = Math.Min(level, System.Numerics.BitOperations.TrailingZeroCount(value));
            }
        }

        return level;
    }

    /// <param name="sourceWidth">Ancho de lo que se escala: la composición o la región recortada.</param>
    /// <param name="maxLevel">Último nivel de mip que tiene la textura de la composición (0 sin mips).</param>
    /// <param name="whole">Se lee la composición entera; con recorte nunca se pasa directo.</param>
    public static ScalePlan Plan(int sourceWidth, int sourceHeight, int width, int height, ContentRect rect, ExportScaleFilter filter, int maxLevel,
        bool whole = true)
    {
        double sx = (double)rect.Width / sourceWidth;
        double sy = (double)rect.Height / sourceHeight;
        int level = 0;
        if (filter == ExportScaleFilter.Auto)
        {
            // Mientras la reducción llegue a otro nivel entero sin pasarse, se lee ese nivel del mip (promedio 2×2 exacto).
            while (level < maxLevel)
            {
                int next = 1 << (level + 1);
                if (sx * next > 1 + 1e-9 || sy * next > 1 + 1e-9 || sourceWidth % next != 0 || sourceHeight % next != 0)
                {
                    break;
                }

                level++;
            }
        }

        bool identity = rect.Width == sourceWidth >> level && rect.Height == sourceHeight >> level
            && (sourceWidth >> level) << level == sourceWidth && (sourceHeight >> level) << level == sourceHeight;
        ScaleKernel kernel = identity ? ScaleKernel.Point : filter switch
        {
            ExportScaleFilter.Bilinear => ScaleKernel.Bilinear,
            ExportScaleFilter.Lanczos => ScaleKernel.Lanczos3,
            _ => ScaleKernel.CatmullRom,
        };
        float widenX = Widen(sx * (1 << level));
        float widenY = Widen(sy * (1 << level));
        return new ScalePlan(rect, level, kernel, widenX, widenY, whole && identity && rect.Covers(width, height));
    }

    private static float Widen(double residual) => (float)Math.Clamp(1 / residual, 1, MaxWiden);

    private static (int Offset, int Size) Axis(double exact, int available, bool even)
    {
        int size = (int)Math.Round(exact);
        if (size >= available)
        {
            // Recorte (o barras que no tocan este eje): centrado, sobrando por los dos lados.
            return (-(int)Math.Floor((size - available) / 2.0), size);
        }

        size = Math.Max(1, size);
        if (even)
        {
            size = Math.Max(2, size & ~1);
        }

        int offset = (available - size) / 2;
        return (even ? offset & ~1 : offset, size);
    }
}
