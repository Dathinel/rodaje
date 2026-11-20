using System.Xml.Linq;

namespace Rodaje.App.Tests.Shell;

public sealed class MenuPanelsConsistencyTests
{
    [Fact]
    public void View_menu_lists_the_inspector_tabs_in_order()
    {
        string root = RepositoryRoot();
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XElement menu = XDocument.Load(Path.Combine(root, "src", "Rodaje.App", "Shell", "TitleBar.xaml"))
            .Descendants(xaml + "MenuBar").Single();
        XElement panels = menu.Descendants(xaml + "MenuFlyoutSubItem").Single(e => (string?)e.Attribute("Text") == "Paneles");
        string[] expected = ["inspector:Properties", "inspector:Text", "inspector:Speed", "inspector:Filters", "inspector:Masks", "inspector:Graph", "inspector:Tracking", "inspector:Dynamics", "inspector:Transition", "inspector:History", "inspector:Jobs"];

        Assert.Equal(expected, panels.Elements(xaml + "MenuFlyoutItem").Select(e => (string?)e.Attribute("Tag")));
        string inspector = File.ReadAllText(Path.Combine(root, "src", "Rodaje.App", "Views", "Inspector", "InspectorView.xaml.cs"));
        Assert.True(expected.Select(tag => tag["inspector:".Length..]).SequenceEqual(EnumNames(inspector)));
    }

    [Fact]
    public void Player_quality_control_uses_the_short_auto_label()
    {
        string root = RepositoryRoot();
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement player = XDocument.Load(Path.Combine(root, "src", "Rodaje.App", "Views", "Player", "PlayerView.xaml")).Root!;

        Assert.Equal("Auto", player.Descendants(xaml + "DropDownButton").Single(e => (string?)e.Attribute(x + "Name") == "QualityButton").Attribute("Content")?.Value);
        Assert.Equal("Auto", player.Descendants(xaml + "RadioMenuFlyoutItem").Single(e => (string?)e.Attribute("Tag") == "Auto").Attribute("Text")?.Value);
    }

    private static IEnumerable<string> EnumNames(string source)
    {
        string body = source.Split("public enum Tab", 2)[1].Split('}', 2)[0];
        return body.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Last());
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Rodaje.slnx"))) return dir.FullName;
        }

        throw new InvalidOperationException("No encuentro Rodaje.slnx subiendo desde las pruebas.");
    }
}
