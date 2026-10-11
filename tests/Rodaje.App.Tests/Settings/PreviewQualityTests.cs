using Rodaje.App.Services.Settings;

namespace Rodaje.App.Tests.Settings;

public class PreviewQualityTests
{
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    /// <summary>Muestras cada 50 ms durante <paramref name="seconds"/> desde <paramref name="start"/>; devuelve si alguna cambió el divisor.</summary>
    private static bool Feed(PreviewQualityGovernor governor, double start, double seconds, Func<int, double> gpuMs)
    {
        bool changed = false;
        int count = (int)(seconds * 20);
        for (int i = 0; i <= count; i++)
        {
            changed |= governor.Sample(gpuMs(i), Ms(start + (i * 50)));
        }

        return changed;
    }

    [Fact]
    public void Auto_BajaSiElP95PasaDe10msDuranteUnSegundoYVuelveEnPausa()
    {
        var governor = new PreviewQualityGovernor();
        Assert.Equal(PreviewQuality.Auto, governor.Quality);
        Assert.False(Feed(governor, 0, 2, _ => 8));
        Assert.Equal(1, governor.Divisor);

        // Unos pocos picos (menos del 5 %) no bajan la calidad.
        Assert.False(Feed(governor, 3000, 1, i => i == 3 ? 30 : 8));
        Assert.Equal(1, governor.Divisor);

        Assert.True(Feed(governor, 5000, 1, _ => 14));
        Assert.Equal(2, governor.Divisor);
        Assert.True(Feed(governor, 7000, 1, _ => 12));
        Assert.Equal(4, governor.Divisor);
        Assert.False(Feed(governor, 9000, 1, _ => 40));
        Assert.Equal(4, governor.Divisor);
        Assert.Equal(2, governor.Downgrades);

        Assert.True(governor.Paused());
        Assert.Equal(1, governor.Divisor);
        Assert.False(governor.Paused());
    }

    [Fact]
    public void Auto_NoDecideConMenosDeUnSegundo()
    {
        var governor = new PreviewQualityGovernor();
        Assert.False(Feed(governor, 0, 0.9, _ => 50));
        Assert.Equal(1, governor.Divisor);
        Assert.False(governor.Sample(double.NaN, Ms(2000)));
    }

    [Fact]
    public void LasCalidadesFijasNoCambianConLaGpu()
    {
        var governor = new PreviewQualityGovernor { Quality = PreviewQuality.Half };
        Assert.Equal(2, governor.Divisor);
        Assert.False(Feed(governor, 0, 2, _ => 50));
        Assert.False(governor.Paused());
        Assert.Equal(2, governor.Divisor);
        governor.Quality = PreviewQuality.Quarter;
        Assert.Equal(4, governor.Divisor);
        governor.Quality = PreviewQuality.Full;
        Assert.Equal(1, governor.Divisor);
    }
}
