using Rodaje.App.Services.RenderCache;
using Rodaje.Core.Timing;

namespace Rodaje.App.Views.Timeline;

/// <summary>Un trozo de la franja del render en caché ya en píxeles.</summary>
public readonly record struct CacheStripSegment(double X, double Width, CacheStripState State);

/// <summary>La franja de 4 px sobre la regla (D-060): sin franja lo ligero, amarillo, rojo y verde.</summary>
public static class CacheStripLayout
{
    /// <summary>Texto de la ayuda de cada color (nunca solo el color).</summary>
    public static string Describe(CacheStripState state) => state switch
    {
        CacheStripState.Tight => "Amarillo: sin render en caché; se prevé a tiempo real, con poco margen.",
        CacheStripState.Heavy => "Rojo: sin render en caché; se prevé por encima de lo que da tiempo, o perdió cuadros al reproducirlo. Se calcula en reposo o con Secuencia › Calcular de entrada a salida.",
        CacheStripState.Cached => "Verde: se ve desde el render en caché (ProRes 422 HQ a resolución completa; el croma de líneas de 1 píxel pierde algo frente a lo vivo).",
        _ => "",
    };

    /// <summary>
    /// Los trozos visibles entre 0 y <paramref name="width"/> píxeles, sin lo ligero y juntando los del mismo estado que se
    /// tocan; un tramo de menos de un píxel ocupa uno (un cuadro rojo suelto se ve aunque la vista esté lejos).
    /// </summary>
    public static IReadOnlyList<CacheStripSegment> Segments(IReadOnlyList<CacheStripSpan> strip, Rational rate, Func<Time, double> timeToX, double width)
    {
        ArgumentNullException.ThrowIfNull(strip);
        ArgumentNullException.ThrowIfNull(timeToX);
        var segments = new List<CacheStripSegment>();
        foreach (CacheStripSpan span in strip)
        {
            if (span.State == CacheStripState.Light)
            {
                continue;
            }

            double x0 = timeToX(Time.FromFrames(span.Start, rate));
            double x1 = Math.Max(timeToX(Time.FromFrames(span.End, rate)), x0 + 1);
            if (x1 <= 0 || x0 >= width)
            {
                continue;
            }

            x0 = Math.Max(x0, 0);
            x1 = Math.Min(x1, width);
            if (segments.Count > 0 && segments[^1] is var last && last.State == span.State && x0 <= last.X + last.Width + 0.5)
            {
                segments[^1] = last with { Width = Math.Max(last.Width, x1 - last.X) };
            }
            else if (segments.Count > 0 && segments[^1] is var prev && x1 <= prev.X + prev.Width)
            {
                // Cae dentro de un píxel ya pintado de otro estado: gana el que está.
                continue;
            }
            else
            {
                segments.Add(new CacheStripSegment(x0, x1 - x0, span.State));
            }
        }

        return segments;
    }
}
