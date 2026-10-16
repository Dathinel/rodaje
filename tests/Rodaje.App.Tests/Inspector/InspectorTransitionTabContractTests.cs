using System.Xml.Linq;

namespace Rodaje.App.Tests.Inspector;

public sealed class InspectorTransitionTabContractTests
{
    [Fact]
    public void TransitionHasAContextualInspectorTab()
    {
        string root = RepositoryRoot();
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XElement inspector = XDocument.Load(Path.Combine(root, "src", "Rodaje.App", "Views", "Inspector", "InspectorView.xaml")).Root!;

        XElement tab = Assert.Single(inspector.Descendants(p + "RadioButton"),
            element => (string?)element.Attribute(x + "Name") == "TransitionTab");
        Assert.Equal("Transición", (string?)tab.Attribute("AutomationProperties.Name"));
        Assert.Equal("Collapsed", (string?)tab.Attribute("Visibility"));
        Assert.Contains(inspector.Descendants(), element => (string?)element.Attribute(x + "Name") == "TransitionPanel");
        string code = File.ReadAllText(Path.Combine(root, "src", "Rodaje.App", "Views", "Inspector", "InspectorView.xaml.cs"));
        Assert.Contains("Show(Tab.Transition)", code, StringComparison.Ordinal);
        Assert.Contains("Show(Tab.Properties)", code, StringComparison.Ordinal);
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
