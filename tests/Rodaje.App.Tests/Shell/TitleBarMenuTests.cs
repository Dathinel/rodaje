using System.Xml.Linq;
using Rodaje.App.Views.Analysis;

namespace Rodaje.App.Tests.Shell;

public sealed class TitleBarMenuTests
{
    [Fact]
    public void SequenceMenuEntriesInvokeTheirRegisteredActions()
    {
        string root = RepositoryRoot();
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XElement menu = XDocument.Load(Path.Combine(root, "src", "Rodaje.App", "Shell", "TitleBar.xaml"))
            .Descendants(xaml + "MenuBar").Single();
        (string Label, string Id)[] actions =
        [
            ("Insertar desde el origen…", "edit.insertSource"),
            ("Sobrescribir desde el origen…", "edit.overwriteSource"),
            ("Reemplazar desde el origen…", "edit.replaceSource"),
            ("Ajustar a rellenar desde el origen…", "edit.fitToFillSource"),
            ("Superponer desde el origen…", "edit.superimposeSource"),
        ];

        foreach ((string label, string id) in actions)
        {
            XElement entry = menu.Descendants(xaml + "MenuFlyoutItem")
                .Single(item => (string?)item.Attribute("Tag") == id);
            Assert.Equal(label, (string?)entry.Attribute("Text"));
            Assert.Equal("OnActionClick", (string?)entry.Attribute("Click"));
            Assert.Equal("Edición de tres puntos", entry.Ancestors(xaml + "MenuFlyoutSubItem").First().Attribute("Text")?.Value);
            Assert.EndsWith("…", label, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void IntelligentToolsAreGroupedUnderToolsAndAwayFromEdit()
    {
        string root = RepositoryRoot();
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XElement menu = XDocument.Load(Path.Combine(root, "src", "Rodaje.App", "Shell", "TitleBar.xaml"))
            .Descendants(xaml + "MenuBar").Single();

        Assert.Equal("Editar", ParentMenu(menu, "edit.split"));
        Assert.Equal("Secuencia", ParentMenu(menu, "clip.addText"));
        Assert.Equal("Secuencia", ParentMenu(menu, "timeline.subtitleTrack"));
        string[] actionIds =
        [
            "analysis.silences", "analysis.beats", "analysis.ducking", "analysis.syncAudio", "analysis.noise",
            "analysis.stabilize", "analysis.autocolor", "analysis.whitebalance", "vision.removeBackground", "vision.reframe",
            "tools.autoSubtitles", "tools.translateTrack", "voice.textToSpeech", "audio.voiceOver",
        ];
        string[] quickToolIds = QuickTools.All.Select(tool => tool.ActionId).Where(id => id != QuickTools.AddText).ToArray();
        Assert.Equal(quickToolIds.Append("tools.translateTrack").Order(), actionIds.Order());
        Assert.All(actionIds, id => Assert.Equal("Herramientas", ParentMenu(menu, id)));
        Assert.Equal("Herramientas", ParentNamedElement(menu, "AiQualityAuto"));
        Assert.All(actionIds.Take(5), id => Assert.Equal("Audio", ParentSubMenu(menu, id)));
        Assert.All(actionIds.Skip(5).Take(5), id => Assert.Equal("Imagen", ParentSubMenu(menu, id)));
        Assert.All(actionIds.Skip(10), id => Assert.Equal("Texto y voz", ParentSubMenu(menu, id)));
        Assert.DoesNotContain("analysis.silences", ItemsUnder(menu, "Editar"));
    }

    [Fact]
    public void SequenceMenuExposesProjectFormatTracksAndGapClosing()
    {
        string root = RepositoryRoot();
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XElement menu = XDocument.Load(Path.Combine(root, "src", "Rodaje.App", "Shell", "TitleBar.xaml"))
            .Descendants(xaml + "MenuBar").Single();
        string[] expected = ["project.format", "timeline.videoTrack", "timeline.audioTrack", "timeline.closeGaps"];

        Assert.All(expected, id => Assert.Equal("Secuencia", ParentMenu(menu, id)));
        Assert.Equal(expected, ItemsUnder(menu, "Secuencia").Take(expected.Length));
    }

    private static string? ParentMenu(XElement menu, string tag) => menu
        .Descendants()
        .SingleOrDefault(element => (string?)element.Attribute("Tag") == tag)
        ?.Ancestors().FirstOrDefault(element => element.Name.LocalName == "MenuBarItem")
        ?.Attribute("Title")?.Value;

    private static IEnumerable<string> ItemsUnder(XElement menu, string title) => menu
        .Elements().Where(element => (string?)element.Attribute("Title") == title)
        .Descendants().Select(element => (string?)element.Attribute("Tag"))
        .OfType<string>();

    private static string? ParentNamedElement(XElement menu, string name)
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        return menu.Descendants().SingleOrDefault(element => (string?)element.Attribute(xaml + "Name") == name)
            ?.Ancestors().FirstOrDefault(element => element.Name.LocalName == "MenuBarItem")
            ?.Attribute("Title")?.Value;
    }

    private static string? ParentSubMenu(XElement menu, string tag) => menu
        .Descendants().SingleOrDefault(element => (string?)element.Attribute("Tag") == tag)
        ?.Ancestors().FirstOrDefault(element => element.Name.LocalName == "MenuFlyoutSubItem")
        ?.Attribute("Text")?.Value;

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
