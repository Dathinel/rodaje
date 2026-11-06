using Rodaje.App.Services.Settings;
using Rodaje.App.Tests.Support;

namespace Rodaje.App.Tests.Settings;

public class TimelineProportionTests
{
    [Theory]
    [InlineData(1040, 437)]
    [InlineData(728, 306)]
    public void Equilibrada_Es42PorCientoDelAlto(double root, double expected)
    {
        Assert.Equal(expected, TimelineProportion.Height(TimelineProportion.Balanced, root, 90), 0);
    }

    [Fact]
    public void NuncaBajaDeTresPistas_NiLeQuitaAlVisorSuMinimo()
    {
        Assert.Equal(TimelineProportion.MinHeight, TimelineProportion.Height(TimelineProportion.MoreViewer, 728, 90));
        Assert.True(TimelineProportion.MinHeight >= 250);
        Assert.Equal(1000 - 90 - TimelineProportion.ViewerMinHeight, TimelineProportion.Height(0.85, 1000, 90));
        Assert.Equal(0, TimelineProportion.Height(TimelineProportion.Balanced, 200, 90));
    }

    [Fact]
    public void ElAltoSigueALaVentana()
    {
        double share = TimelineProportion.ShareOf(500, 1000);
        Assert.Equal(0.5, share);
        Assert.Equal(700, TimelineProportion.Height(share, 1400, 90));
        Assert.Equal(TimelineProportion.Max, TimelineProportion.ShareOf(5000, 1000));
        Assert.Equal(TimelineProportion.Balanced, TimelineProportion.ShareOf(300, 0));
    }

    [Fact]
    public void DobleClic_AlternaEntreMasVisorYMasLineaDeTiempo()
    {
        Assert.Equal(TimelineProportion.MoreTimeline, TimelineProportion.Toggle(TimelineProportion.Balanced));
        Assert.Equal(TimelineProportion.MoreViewer, TimelineProportion.Toggle(TimelineProportion.MoreTimeline));
        Assert.Equal(TimelineProportion.MoreTimeline, TimelineProportion.Toggle(TimelineProportion.MoreViewer));
    }

    [Theory]
    [InlineData(450, 900, 0.5)]
    [InlineData(450, 0, 0.5)]
    [InlineData(300, 1200, TimelineProportion.Balanced)]
    [InlineData(160, 900, TimelineProportion.Balanced)]
    [InlineData(200, 1000, 0.2)]
    [InlineData(5000, 900, TimelineProportion.Max)]
    public void ElAltoViejoPasaASuProporcion(double height, double window, double expected)
    {
        Assert.Equal(expected, TimelineProportion.FromLegacyHeight(height, window), 3);
        Assert.Equal(TimelineProportion.Balanced, TimelineProportion.FromLegacyHeight(null, window));
    }

    [Fact]
    public void AjustesVersion3_SeMigranUnaVezYNoSeVuelveAEscribirElAlto()
    {
        using var dir = new TempDirectory();
        string path = dir.File(SettingsStore.FileName);
        File.WriteAllText(path, """{ "version": 3, "timelineHeight": 360, "windowHeight": 900 }""");
        var store = new SettingsStore(path);

        Assert.Null(store.Load());
        Assert.Equal(0.4, store.Current.TimelineShare, 3);
        Assert.Equal(AppSettings.CurrentVersion, store.Current.Version);
        store.Save();

        string json = File.ReadAllText(path);
        Assert.DoesNotContain("timelineHeight", json, StringComparison.Ordinal);
        Assert.Contains("\"timelineShare\": 0.4", json, StringComparison.Ordinal);
    }
}
