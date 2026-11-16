using Rodaje.App.Services.RenderCache;
using Rodaje.App.Views.Timeline;
using Rodaje.Core.Timing;
using Xunit;

namespace Rodaje.App.Tests.RenderCache;

public sealed class CacheStripLayoutTests
{
    // A 300 px por segundo y 30 fps, cada cuadro son 10 px; la vista empieza en el cuadro 10.
    private static double X(Time t) => t.ToSeconds() * 300 - 100;

    [Fact]
    public void Sin_franja_lo_ligero_recorta_a_la_vista_y_junta_los_que_se_tocan()
    {
        CacheStripSpan[] strip = [new(0, 5, CacheStripState.Heavy), new(5, 9, CacheStripState.Light), new(9, 12, CacheStripState.Heavy),
            new(12, 20, CacheStripState.Heavy), new(20, 100, CacheStripState.Cached)];

        IReadOnlyList<CacheStripSegment> segments = CacheStripLayout.Segments(strip, Rational.Fps30, X, 500);

        // El primer rojo queda a la izquierda de la vista; los dos siguientes se tocan; el verde se corta al ancho.
        Assert.Equal([new(0, 100, CacheStripState.Heavy), new(100, 400, CacheStripState.Cached)], segments);
    }

    [Fact]
    public void Un_cuadro_rojo_suelto_ocupa_al_menos_un_pixel_aunque_la_vista_este_lejos()
    {
        CacheStripSpan[] strip = [new(0, 3000, CacheStripState.Cached), new(3000, 3001, CacheStripState.Heavy), new(3001, 6000, CacheStripState.Cached)];

        IReadOnlyList<CacheStripSegment> segments = CacheStripLayout.Segments(strip, Rational.Fps30, t => t.ToSeconds(), 1000);

        Assert.Equal(CacheStripState.Heavy, segments[1].State);
        Assert.Equal(1, segments[1].Width);
    }

    [Fact]
    public void Cada_color_dice_lo_que_significa()
    {
        Assert.Empty(CacheStripLayout.Describe(CacheStripState.Light));
        Assert.StartsWith("Amarillo", CacheStripLayout.Describe(CacheStripState.Tight), StringComparison.Ordinal);
        Assert.StartsWith("Rojo", CacheStripLayout.Describe(CacheStripState.Heavy), StringComparison.Ordinal);
        Assert.StartsWith("Verde", CacheStripLayout.Describe(CacheStripState.Cached), StringComparison.Ordinal);
    }

    [Fact]
    public void La_fila_de_trabajos_dice_que_hace_y_cuanto_falta()
    {
        var status = new RenderCacheStatus(RenderCacheState.Working, new RenderCacheWork(60, 60, 1), 0.5, 3, 2, 1);

        Assert.Equal("Calculando desde 2 s (60 cuadros) · 3 trozos en cola · 2 calculados · 1 falló", RenderCacheStatusText.Describe(status, Rational.Fps30));
        Assert.Equal("Nada pendiente", RenderCacheStatusText.Describe(new RenderCacheStatus(RenderCacheState.Idle, null, 0, 0, 0, 0), Rational.Fps30));
    }
}
