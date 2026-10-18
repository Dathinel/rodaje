using Rodaje.App.Views.Timeline;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.TimelineLogic;

public class AutoScrollTests
{
    [Fact]
    public void LejosDeLosBordes_NoSeMueve()
    {
        Assert.Equal(0, AutoScroll.Step(500, 1000));
        Assert.Equal(0, AutoScroll.Step(AutoScroll.Zone, 1000));
        Assert.Equal(0, AutoScroll.Step(1000 - AutoScroll.Zone, 1000));
        Assert.Equal(0, AutoScroll.Step(10, 0));
        Assert.Equal(0, AutoScroll.Step(double.NaN, 1000));
    }

    [Fact]
    public void CuantoMasCercaDelBorde_MasRapido()
    {
        double far = AutoScroll.Step(24, 1000);
        double near = AutoScroll.Step(8, 1000);
        Assert.True(far < 0 && near < far);
        Assert.Equal(-AutoScroll.MaxStep, AutoScroll.Step(0, 1000));
        Assert.Equal(-AutoScroll.MaxStep / 2, AutoScroll.Step(AutoScroll.Zone / 2, 1000));
        Assert.Equal(AutoScroll.MaxStep / 2, AutoScroll.Step(1000 - (AutoScroll.Zone / 2), 1000));
    }

    [Fact]
    public void FueraDeLaVista_ElPasoMaximo()
    {
        Assert.Equal(-AutoScroll.MaxStep, AutoScroll.Step(-200, 1000));
        Assert.Equal(AutoScroll.MaxStep, AutoScroll.Step(1300, 1000));
    }

    [Fact]
    public void EnUnaVistaChica_LaFranjaNoPasaDeUnCuarto()
    {
        Assert.Equal(0, AutoScroll.Step(50, 100));
        Assert.Equal(-AutoScroll.MaxStep / 2, AutoScroll.Step(12.5, 100));
    }

    [Fact]
    public void ElZoomSeAjustaSoloConElPrimerClip()
    {
        Assert.True(ZoomFit.FitsAfterEdit(Time.Zero, Time.FromSeconds(10)));
        Assert.False(ZoomFit.FitsAfterEdit(Time.FromSeconds(10), Time.FromSeconds(20)));
        Assert.False(ZoomFit.FitsAfterEdit(Time.FromSeconds(10), Time.Zero));
        Assert.False(ZoomFit.FitsAfterEdit(Time.Zero, Time.Zero));
    }

    [Fact]
    public void AjustarALaSecuencia_DejaUn5PorCientoDeMargen()
    {
        ViewFrame frame = ZoomFit.Whole(Time.FromSeconds(20), 1000);
        Assert.Equal(950 / 20.0, frame.PixelsPerSecond, 3);
        Assert.Equal(0, frame.ScrollX);
    }
}
