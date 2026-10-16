using System.Xml.Linq;

namespace Rodaje.App.Tests.Inspector;

public sealed class MaskTrackingPlacementTests
{
    [Fact]
    public void MasksDelegatesTrackingToTheTrackingPanelWithItsMaskSelected()
    {
        string root = RepositoryRoot();
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement masks = XDocument.Load(Path.Combine(root, "src", "Rodaje.App", "Views", "Inspector", "Masks", "MasksPanel.xaml")).Root!;
        Assert.Contains(masks.Descendants(), element => (string?)element.Attribute(x + "Name") == "LinkTrackingButton");
        Assert.DoesNotContain(masks.Descendants(), element => (string?)element.Attribute("Click") == "OnTrackClick");

        string masksCode = File.ReadAllText(Path.Combine(root, "src", "Rodaje.App", "Views", "Inspector", "Masks", "MasksPanel.xaml.cs"));
        string sessionCode = File.ReadAllText(Path.Combine(root, "src", "Rodaje.App", "Services", "EditorSession.Tracking.cs"));
        string trackingCode = File.ReadAllText(Path.Combine(root, "src", "Rodaje.App", "Views", "Inspector", "Tracking", "TrackingPanel.xaml.cs"));
        Assert.Contains("ShowTrackPanelForMask(mask.Id)", masksCode, StringComparison.Ordinal);
        Assert.Contains("_requestedTrackUse = TrackUse.Mask", sessionCode, StringComparison.Ordinal);
        Assert.Contains("Session.RequestedTrackUse", trackingCode, StringComparison.Ordinal);
        Assert.Contains("Session.RequestedTrackTargetId", trackingCode, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Rodaje.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("No encuentro Rodaje.slnx subiendo desde las pruebas.");
    }
}
