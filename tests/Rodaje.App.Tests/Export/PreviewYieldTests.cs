using Rodaje.App.Services.Settings;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Export;
using Rodaje.Engine.Timeline;
using Rodaje.Export;
using Rodaje.Export.Running;

namespace Rodaje.App.Tests.Export;

public class PreviewYieldTests
{
    private const long EightGb = 8L << 30;
    private const long ThreeGb = 3L << 30;

    [Fact]
    public void AltoRendimiento_PausaLaVistaPrevia_YBajoLaDejaSeguir()
    {
        var player = new FakePlayback();
        var yield = new PreviewYield(player, _ => { }, () => new AppSettings());

        yield.Yield(ExportPerformanceProfile.For(ExportPerformance.Fastest, EightGb)).Dispose();
        yield.Yield(ExportPerformanceProfile.For(ExportPerformance.Background, EightGb)).Dispose();

        Assert.Equal(["pause"], player.Commands);
    }

    [Fact]
    public void ConPocaVram_PasaAProxiesYMitad_YAlTerminarVuelveAlAjusteDeEseMomento()
    {
        var player = new FakePlayback();
        var qualities = new List<PreviewQuality>();
        var settings = new AppSettings { ProxyUsage = ProxyUsage.Auto, PreviewQuality = PreviewQuality.Full };
        var yield = new PreviewYield(player, qualities.Add, () => settings);

        IDisposable first = yield.Yield(ExportPerformanceProfile.For(ExportPerformance.Background, ThreeGb));
        IDisposable second = yield.Yield(ExportPerformanceProfile.For(ExportPerformance.Background, ThreeGb));
        settings.ProxyUsage = ProxyUsage.Never;
        first.Dispose();
        first.Dispose();
        Assert.Equal(["proxies:Always"], player.Commands);
        second.Dispose();

        Assert.Equal(["proxies:Always", "proxies:Never"], player.Commands);
        Assert.Equal([PreviewQuality.Half, PreviewQuality.Full], qualities);
    }
}
