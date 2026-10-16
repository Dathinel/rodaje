using System.Xml.Linq;

namespace Rodaje.App.Tests.TimelineLogic;

public sealed class MarkersNavigationTests
{
    [Fact]
    public void MarkersAreOutsideLibraryRailAndAvailableFromSequenceMenu()
    {
        string root = RepositoryRoot();
        XElement library = XDocument.Load(Path.Combine(root, "src", "Rodaje.App", "Views", "Library", "LibraryView.xaml")).Root!;
        Assert.DoesNotContain(library.Descendants(), e => (string?)e.Attribute("Tag") == "Markers");
        XElement title = XDocument.Load(Path.Combine(root, "src", "Rodaje.App", "Shell", "TitleBar.xaml")).Root!;
        Assert.Contains(title.Descendants(), e => (string?)e.Attribute("Text") == "Panel de marcadores");
        string input = File.ReadAllText(Path.Combine(root, "src", "Rodaje.App", "Views", "Timeline", "TimelineView.Input.cs"));
        Assert.Contains("Editar…", input, StringComparison.Ordinal);
        Assert.Contains("Text = \"Quitar\"", input, StringComparison.Ordinal);
        Assert.Contains("MarkerContextRequested", input, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Rodaje.slnx"))) return d.FullName;
        throw new InvalidOperationException("No encuentro Rodaje.slnx.");
    }
}