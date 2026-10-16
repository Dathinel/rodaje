using System.Xml.Linq;

namespace Rodaje.App.Tests.Shell;

public sealed class ProjectFormatMenuTests
{
    [Fact]
    public void SequenceMenuOpensTheExistingProjectFormatAction()
    {
        string root = RepositoryRoot();
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XElement menu = XDocument.Load(Path.Combine(root, "src", "Rodaje.App", "Shell", "TitleBar.xaml"))
            .Descendants(presentation + "MenuBar").Single();
        XElement item = menu.Descendants(presentation + "MenuFlyoutItem")
            .Single(element => (string?)element.Attribute("Tag") == "project.format");

        Assert.Equal("Secuencia", item.Ancestors().First(element => element.Name.LocalName == "MenuBarItem").Attribute("Title")?.Value);
        Assert.Equal("OnActionClick", item.Attribute("Click")?.Value);

        string formatSource = File.ReadAllText(Path.Combine(root, "src", "Rodaje.App", "Views", "Player", "PlayerView.xaml.cs"));
        Assert.Contains("public const string ProjectFormat = \"project.format\"", formatSource, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Rodaje.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("No encuentro Rodaje.slnx subiendo desde las pruebas.");
    }
}
