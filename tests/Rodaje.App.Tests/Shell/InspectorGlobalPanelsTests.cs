using System.Xml.Linq;
using Rodaje.App.Services.Shortcuts;

namespace Rodaje.App.Tests.Shell;

public sealed class InspectorGlobalPanelsTests
{
    [Fact]
    public void HistoryAndJobsLiveInTheWindowStatusBar()
    {
        string root = RepositoryRoot();
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XElement inspector = XDocument.Load(Path.Combine(root, "src", "Rodaje.App", "Views", "Inspector", "InspectorView.xaml")).Root!;
        XElement window = XDocument.Load(Path.Combine(root, "src", "Rodaje.App", "Shell", "MainWindow.xaml")).Root!;

        Assert.DoesNotContain(inspector.Descendants(), element =>
            ((string?)element.Attribute(x + "Name")) is "HistoryTab" or "JobsTab" or "HistoryPanel" or "JobsPanel");
        XElement statusBar = window.Descendants(p + "Border").Single(element => (string?)element.Attribute("Grid.Row") == "5");
        Assert.Contains(statusBar.Descendants(), element => (string?)element.Attribute(x + "Name") == "HistoryPanel");
        Assert.Contains(statusBar.Descendants(), element => (string?)element.Attribute(x + "Name") == "JobsPanel");
        Assert.Contains(statusBar.Descendants(), element => (string?)element.Attribute(x + "Name") == "JobsProgress");
    }

    [Fact]
    public void HistoryShortcutIsRegisteredAsCtrlH()
    {
        ShortcutAction action = EditorShortcuts.Catalog.Single(item => item.Id == ActionIds.ShowHistory);
        Assert.Equal("Ctrl+H", Assert.Single(action.DefaultKeys(ShortcutPreset.Rodaje)).ToString());
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
