using Rodaje.App.Services;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Timeline;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.TimelineLogic;

public class ZoomFitTests
{
    private static Time S(double seconds) => Time.FromSeconds(seconds);

    /// <summary>Pista principal: a [0,10) y b [10,20) con su audio enlazado.</summary>
    private static async Task<EditorSession> TwoClips()
    {
        var prober = new FakeProber().Video("a.mp4", 10).Video("b.mp4", 10);
        var session = new EditorSession(new FakePlayback(), prober, new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([Path.GetFullPath("a.mp4"), Path.GetFullPath("b.mp4")]);
        return session;
    }

    /// <summary>Instantes en los bordes visibles de un encuadre, en segundos.</summary>
    private static (double Start, double End) Visible(ViewFrame frame, double width) =>
        (frame.ScrollX / frame.PixelsPerSecond, (frame.ScrollX + width) / frame.PixelsPerSecond);

    [Fact]
    public void AjustarALaVentana_CoincideConFitToDelViewport()
    {
        foreach (double seconds in new[] { 0.2, 1, 37.5, 600 })
        {
            var vp = new TimelineViewport { PixelsPerSecond = 300, ScrollX = 900, Width = 1200 };
            vp.FitTo(S(seconds));

            ViewFrame frame = ZoomFit.Whole(S(seconds), 1200);

            Assert.Equal(vp.PixelsPerSecond, frame.PixelsPerSecond, 9);
            Assert.Equal(0, frame.ScrollX);
        }
    }

    [Fact]
    public void AjustarALaVentana_TodaLaSecuenciaCabeConMargenYElZoomSeLimita()
    {
        ViewFrame frame = ZoomFit.Whole(S(40), 1000);
        Assert.Equal(950.0 / 40, frame.PixelsPerSecond, 9);
        Assert.Equal(40, Visible(frame, 1000).End * 0.95, 6);

        // Tres horas caben enteras; una secuencia muy larga no puede bajar del zoom mínimo; una vacía se ve como un segundo.
        Assert.Equal(950.0 / 10_800, ZoomFit.Whole(S(10_800), 1000).PixelsPerSecond, 9);
        Assert.Equal(TimelineState.MinPixelsPerSecond, ZoomFit.Whole(S(36_000), 1000).PixelsPerSecond);
        Assert.Equal(950, ZoomFit.Whole(Time.Zero, 1000).PixelsPerSecond, 9);
    }

    [Theory]
    [InlineData(10, 20, 1000)]
    [InlineData(95.5, 96.25, 1400)]
    [InlineData(300, 500, 800)]
    public void ZoomALaSeleccion_ElTramoLlenaLaVistaCentradoConMargen(double start, double end, double width)
    {
        ViewFrame frame = ZoomFit.Span(S(start), S(end), width);
        (double visibleStart, double visibleEnd) = Visible(frame, width);
        double margin = (visibleEnd - visibleStart) * ZoomFit.SelectionMarginFraction;

        Assert.Equal(start - margin, visibleStart, 6);
        Assert.Equal(end + margin, visibleEnd, 6);
    }

    [Fact]
    public void ZoomALaSeleccion_ElTramoAlInicioNoDejaDesplazamientoNegativoYSigueEntero()
    {
        // El tramo es corto pero el zoom máximo no deja llenarla: quedaría centrado más allá del inicio.
        ViewFrame frame = ZoomFit.Span(S(0), S(0.1), 1000);
        Assert.Equal(TimelineState.MaxPixelsPerSecond, frame.PixelsPerSecond);
        Assert.Equal(0, frame.ScrollX);
        Assert.True(Visible(frame, 1000).End >= 0.1);
    }

    [Fact]
    public void ZoomALaSeleccion_TramoVacioOAlReves()
    {
        ViewFrame point = ZoomFit.Span(S(30), S(30), 1000);
        Assert.Equal(TimelineState.MaxPixelsPerSecond, point.PixelsPerSecond);
        Assert.Equal(30, (point.ScrollX + 500) / point.PixelsPerSecond, 6);

        Assert.Equal(ZoomFit.Span(S(4), S(9), 1000), ZoomFit.Span(S(9), S(4), 1000));

        // Un tramo larguísimo se queda en el zoom mínimo, centrado.
        ViewFrame wide = ZoomFit.Span(S(0), S(100_000), 1000);
        Assert.Equal(TimelineState.MinPixelsPerSecond, wide.PixelsPerSecond);
        Assert.Equal(50_000, (wide.ScrollX + 500) / wide.PixelsPerSecond, 6);
    }

    [Fact]
    public void TramoDeLaSeleccion_ClipsPrimeroLuegoElTramoMarcado() => UiThread.Run(async () =>
    {
        EditorSession session = await TwoClips();
        Sequence sequence = session.Sequence;
        Track main = session.MainTrack!;

        Assert.Equal((S(10), S(20)), ZoomFit.SelectionSpan(sequence, [main.Clips[1].Id], (S(1), S(2))));
        Assert.Equal((S(0), S(20)), ZoomFit.SelectionSpan(sequence, [main.Clips[1].Id, "nada", main.Clips[0].Id], null));
        Assert.Equal((S(0), S(10)), ZoomFit.SelectionSpan(sequence, [sequence.AudioTracks[0].Clips[0].Id], null));
        Assert.Equal((S(1), S(2)), ZoomFit.SelectionSpan(sequence, ["nada"], (S(1), S(2))));
        Assert.Null(ZoomFit.SelectionSpan(sequence, [], (S(2), S(2))));
        Assert.Null(ZoomFit.SelectionSpan(sequence, [], null));
    });

    [Fact]
    public void Animacion_EmpiezaYTerminaExactoYFrenaAlFinal()
    {
        var from = new ViewFrame(60, 300);
        var to = new ViewFrame(600, 5400);

        Assert.Equal(from, ZoomAnimation.At(from, to, 1000, 0));
        Assert.Equal(from, ZoomAnimation.At(from, to, 1000, -1));
        Assert.Equal(to, ZoomAnimation.At(from, to, 1000, 1));
        Assert.Equal(to, ZoomAnimation.At(from, to, 1000, 7));
        Assert.Equal(to, ZoomAnimation.At(from, to, 1000, double.NaN));

        Assert.Equal(0, ZoomAnimation.Ease(0));
        Assert.Equal(1, ZoomAnimation.Ease(1));
        Assert.Equal(0.875, ZoomAnimation.Ease(0.5), 9);
        Assert.True(ZoomAnimation.Ease(0.1) - ZoomAnimation.Ease(0) > ZoomAnimation.Ease(1) - ZoomAnimation.Ease(0.9));
        Assert.True(ZoomAnimation.Duration.TotalMilliseconds is > 0 and <= 300);
    }

    [Fact]
    public void Animacion_ElZoomVaEnEscalaLogaritmicaYElCentroEnLineaRecta()
    {
        var from = new ViewFrame(10, 0);
        var to = new ViewFrame(1000, 99_500);
        const double width = 1000;
        double previousPps = 0;
        double previousCenter = double.NegativeInfinity;
        for (int i = 1; i < 20; i++)
        {
            double progress = i / 20.0;
            double e = ZoomAnimation.Ease(progress);
            ViewFrame frame = ZoomAnimation.At(from, to, width, progress);

            // A mitad de la curva el zoom está en la media geométrica: 10 → 100 → 1000.
            Assert.Equal(10 * Math.Pow(100, e), frame.PixelsPerSecond, 6);
            double center = (frame.ScrollX + (width / 2)) / frame.PixelsPerSecond;
            Assert.Equal(50 + ((100 - 50) * e), center, 6);

            Assert.True(frame.PixelsPerSecond > previousPps);
            Assert.True(center > previousCenter);
            Assert.True(frame.ScrollX >= 0);
            previousPps = frame.PixelsPerSecond;
            previousCenter = center;
        }
    }

    [Fact]
    public void Animacion_AlAlejarseHastaElInicioNoPasaDeCero()
    {
        var from = new ViewFrame(500, 20_000);
        ViewFrame to = ZoomFit.Whole(S(60), 1000);
        for (int i = 0; i <= 10; i++)
        {
            Assert.True(ZoomAnimation.At(from, to, 1000, i / 10.0).ScrollX >= 0);
        }
    }
}
