using Rodaje.App.Services;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Views.Timeline;

/// <summary>Un encuadre de la línea de tiempo: zoom (píxeles por segundo) y desplazamiento horizontal en píxeles.</summary>
public readonly record struct ViewFrame(double PixelsPerSecond, double ScrollX);

/// <summary>
/// Encuadres de «Ajustar a la ventana» (toda la secuencia visible) y «Zoom a la selección» (los clips elegidos llenan la
/// vista). Solo calcula: la vista los aplica, con la animación de <see cref="ZoomAnimation"/>.
/// </summary>
public static class ZoomFit
{
    /// <summary>Parte del ancho que queda libre a cada lado de la selección.</summary>
    public const double SelectionMarginFraction = 0.05;

    /// <summary>
    /// Toda la secuencia desde el borde izquierdo: el encuadre de <see cref="TimelineViewport.FitTo"/> (el 95 % del
    /// ancho), calculado sin tocar la vista; una secuencia de menos de un segundo se muestra como si durara uno.
    /// </summary>
    public static ViewFrame Whole(Time duration, double width)
    {
        var viewport = new TimelineViewport { Width = width };
        viewport.FitTo(duration);
        return new ViewFrame(viewport.PixelsPerSecond, viewport.ScrollX);
    }

    /// <summary>
    /// El tramo centrado, ocupando el ancho menos <see cref="SelectionMarginFraction"/> a cada lado. El zoom no pasa de
    /// los límites de <see cref="TimelineState"/> (un tramo de un cuadro queda en el zoom máximo) y el desplazamiento
    /// nunca es negativo (un tramo pegado al inicio no se centra del todo).
    /// </summary>
    public static ViewFrame Span(Time start, Time end, double width)
    {
        if (end < start)
        {
            (start, end) = (end, start);
        }

        double usable = Math.Max(1, width * (1 - (2 * SelectionMarginFraction)));
        double seconds = (end - start).ToSeconds();
        double pps = seconds > 0 ? ClampZoom(usable / seconds) : TimelineState.MaxPixelsPerSecond;
        double center = (start.ToSeconds() + end.ToSeconds()) / 2;
        return new ViewFrame(pps, Math.Max(0, (center * pps) - (Math.Max(0, width) / 2)));
    }

    /// <summary>
    /// El tramo que llena la vista en «Zoom a la selección»: del inicio del primer clip seleccionado al final del último;
    /// sin clips seleccionados, el tramo marcado en la regla; sin ninguno de los dos, nada.
    /// </summary>
    public static (Time Start, Time End)? SelectionSpan(Sequence sequence, IEnumerable<string> selectedClipIds, (Time Start, Time End)? markedRange)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        ArgumentNullException.ThrowIfNull(selectedClipIds);
        bool any = false;
        Time start = Time.Zero;
        Time end = Time.Zero;
        foreach (string id in selectedClipIds)
        {
            if (sequence.FindClip(id)?.Clip is not { } clip)
            {
                continue;
            }

            start = any ? Time.Min(start, clip.Start) : clip.Start;
            end = any ? Time.Max(end, clip.End) : clip.End;
            any = true;
        }

        if (any)
        {
            return (start, end);
        }

        return markedRange is { } range && range.End > range.Start ? range : null;
    }

    private static double ClampZoom(double pixelsPerSecond) =>
        Math.Clamp(pixelsPerSecond, TimelineState.MinPixelsPerSecond, TimelineState.MaxPixelsPerSecond);
}

/// <summary>
/// La animación corta entre dos encuadres: el zoom avanza en escala logarítmica (cada cuadro acerca lo mismo en
/// proporción) y el instante del centro de la vista va en línea recta, con una curva que frena al final.
/// </summary>
public static class ZoomAnimation
{
    /// <summary>Lo que dura: corta, para orientar sin hacer esperar.</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(200);

    /// <summary>Salida cúbica: arranca rápido y frena; fuera de 0–1 se acota.</summary>
    public static double Ease(double progress)
    {
        double t = double.IsFinite(progress) ? Math.Clamp(progress, 0, 1) : 1;
        double rest = 1 - t;
        return 1 - (rest * rest * rest);
    }

    /// <summary>
    /// El encuadre en <paramref name="progress"/> (0 a 1, del tiempo transcurrido sobre <see cref="Duration"/>). En 0
    /// devuelve <paramref name="from"/> y desde 1 exactamente <paramref name="to"/>.
    /// </summary>
    public static ViewFrame At(ViewFrame from, ViewFrame to, double width, double progress)
    {
        if (!double.IsFinite(progress) || progress >= 1)
        {
            return to;
        }

        if (progress <= 0)
        {
            return from;
        }

        double e = Ease(progress);
        double half = Math.Max(0, width) / 2;
        double pps = Math.Exp(Lerp(Math.Log(from.PixelsPerSecond), Math.Log(to.PixelsPerSecond), e));
        double centerFrom = (from.ScrollX + half) / from.PixelsPerSecond;
        double centerTo = (to.ScrollX + half) / to.PixelsPerSecond;
        double center = Lerp(centerFrom, centerTo, e);
        return new ViewFrame(pps, Math.Max(0, (center * pps) - half));
    }

    private static double Lerp(double a, double b, double t) => a + ((b - a) * t);
}
