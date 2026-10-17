using System.Xml.Linq;
using Rodaje.App.Shell;

namespace Rodaje.App.Tests.Shell;

public sealed class NarrowWindowTests
{
    [Theory]
    [InlineData(1000, 320, 320)]
    [InlineData(788, 220, 240)]
    [InlineData(787, 320, 0)]
    [InlineData(700, 320, 0)]
    [InlineData(544, 220, 0)]
    [InlineData(543, 0, 0)]
    public void PanelsFitWithoutClipping(double width, double library, double inspector)
    {
        var actual = SidePanelLayout.Fit(width, 320, 320);
        Assert.Equal((library, inspector), actual);
        Assert.True(actual.Library + actual.Inspector + (library > 0 ? 4 : 0) + (inspector > 0 ? 4 : 0) + 320 <= width);
    }

    [Fact]
    public void NarrowingDoesNotChangePreferredWidths()
    {
        var settings = new Rodaje.App.Services.Settings.AppSettings { LibraryWidth = 410, InspectorWidth = 480 };
        foreach (double width in new double[] { 1400, 1000, 700, 500, 544, 788, 1400 })
        {
            SidePanelLayout.Fit(width, settings.LibraryWidth, settings.InspectorWidth);
        }

        Assert.Equal((410d, 480d), SidePanelLayout.Fit(1400, settings.LibraryWidth, settings.InspectorWidth));
    }

    [Fact]
    public void InspectorOverflowAndPlayerControlsHaveTheirOwnSpace()
    {
        XElement inspector = Load("Views/Inspector/InspectorView.xaml");
        XElement scroller = Named(inspector, "TabScroller");
        Assert.Equal("Visible", scroller.Attribute("HorizontalScrollBarVisibility")?.Value);
        Assert.Contains(Named(inspector, "DynamicsTab"), scroller.Descendants());
        XElement player = Load("Views/Player/PlayerView.xaml");
        XElement quality = Named(player, "QualityButton");
        Assert.Equal("1", quality.Ancestors().First(e => e.Name.LocalName == "ScrollViewer").Attribute("Grid.Row")?.Value);
        Assert.DoesNotContain(quality.Ancestors(), e => e.Descendants().Any(t => (string?)t.Attribute("Text") == "Reproductor") && e.Name.LocalName == "StackPanel");
    }

    private static XElement Named(XElement root, string name) => root.Descendants().Single(e =>
        (string?)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == name);

    private static XElement Load(string path)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Rodaje.slnx")))
            {
                return XElement.Load(Path.Combine(dir.FullName, "src/Rodaje.App", path));
            }
        }

        throw new InvalidOperationException("No encuentro Rodaje.slnx.");
    }
}
