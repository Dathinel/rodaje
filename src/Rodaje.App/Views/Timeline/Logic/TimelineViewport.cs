using Rodaje.App.Services;
using Rodaje.Core.Timing;

namespace Rodaje.App.Views.Timeline;

/// <summary>
/// La ventana horizontal de la línea de tiempo: zoom (píxeles por segundo), desplazamiento y ancho visible. Convierte
/// entre tiempo de la secuencia y la x del área de pistas (0 es el borde izquierdo visible). Todo en píxeles
/// independientes del DPI.
/// </summary>
public sealed class TimelineViewport
{
    /// <summary>Espacio que se deja después del final de la secuencia para poder arrastrar más allá.</summary>
    public const double TrailingSpaceFraction = 0.5;

    private double _pixelsPerSecond = TimelineState.DefaultPixelsPerSecond;
    private double _scrollX;

    public double PixelsPerSecond
    {
        get => _pixelsPerSecond;
        set => _pixelsPerSecond = Math.Clamp(double.IsFinite(value) ? value : TimelineState.DefaultPixelsPerSecond, TimelineState.MinPixelsPerSecond, TimelineState.MaxPixelsPerSecond);
    }

    /// <summary>Píxeles de contenido que quedan a la izquierda del borde visible.</summary>
    public double ScrollX
    {
        get => _scrollX;
        set => _scrollX = double.IsFinite(value) ? Math.Max(0, value) : 0;
    }

    /// <summary>Ancho visible del área de pistas.</summary>
    public double Width { get; set; }

    public Time VisibleStart => XToTime(0);

    public Time VisibleEnd => XToTime(Width);

    public double SecondsToPixels(double seconds) => seconds * _pixelsPerSecond;

    public double DurationToPixels(Time duration) => duration.ToSeconds() * _pixelsPerSecond;

    /// <summary>La x en el área visible de un instante (puede quedar fuera, negativa o mayor que <see cref="Width"/>).</summary>
    public double TimeToX(Time time) => (time.ToSeconds() * _pixelsPerSecond) - _scrollX;

    /// <summary>El instante bajo una x del área visible; nunca antes de cero.</summary>
    public Time XToTime(double x)
    {
        double seconds = (x + _scrollX) / _pixelsPerSecond;
        return seconds <= 0 ? Time.Zero : Time.FromSeconds(seconds);
    }

    /// <summary>Un tramo de píxeles expresado en tiempo, con el zoom actual.</summary>
    public Time PixelsToDuration(double pixels) => Time.FromSeconds(pixels / _pixelsPerSecond);

    /// <summary>Ancho total del contenido para una secuencia de esta duración (con espacio extra al final).</summary>
    public double ContentWidth(Time duration) => Math.Max(Width, DurationToPixels(duration) + (Width * TrailingSpaceFraction));

    public void ClampScroll(Time duration) => ScrollX = Math.Min(_scrollX, Math.Max(0, ContentWidth(duration) - Width));

    /// <summary>Cambia el zoom dejando quieto el instante que está bajo <paramref name="anchorX"/>.</summary>
    public void ZoomAround(double anchorX, double pixelsPerSecond)
    {
        double anchorSeconds = (anchorX + _scrollX) / _pixelsPerSecond;
        PixelsPerSecond = pixelsPerSecond;
        ScrollX = (anchorSeconds * _pixelsPerSecond) - anchorX;
    }

    /// <summary>Zoom para ver toda la secuencia, con un pequeño margen.</summary>
    public void FitTo(Time duration)
    {
        double seconds = Math.Max(duration.ToSeconds(), 1);
        PixelsPerSecond = Math.Max(1, Width * 0.95) / seconds;
        ScrollX = 0;
    }

    /// <summary>Desplaza lo justo para que el instante quede visible, con un margen.</summary>
    public bool EnsureVisible(Time time, double margin)
    {
        double x = TimeToX(time);
        if (x < margin)
        {
            ScrollX += x - margin;
            return true;
        }

        if (x > Width - margin)
        {
            ScrollX += x - (Width - margin);
            return true;
        }

        return false;
    }
}
