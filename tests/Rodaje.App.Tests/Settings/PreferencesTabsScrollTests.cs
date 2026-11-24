using System.Xml.Linq;

namespace Rodaje.App.Tests.Settings;

/// <summary>R5 del repaso del 19-nov: en una ventana de 1440×900, Rendimiento no mostraba el tope de GB ni «Borrar el render en caché».</summary>
public sealed class PreferencesTabsScrollTests
{
    [Fact]
    public void EveryTabWithManyControlsScrolls()
    {
        string path = Path.Combine(RepositoryRoot(), "src", "Rodaje.App", "Views", "Preferences", "PreferencesView.xaml");
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XElement[] tabs = [.. XDocument.Load(path).Descendants(ui + "TabViewItem")];

        Assert.NotEmpty(tabs);
        foreach (XElement tab in tabs)
        {
            string header = (string?)tab.Attribute("Header") ?? "?";
            XElement? content = tab.Elements().FirstOrDefault();
            bool scrolls = content?.Name == ui + "ScrollViewer";
            int controls = tab.Descendants().Count(e => e.Name != ui + "StackPanel" && e.Name != ui + "ScrollViewer");
            Assert.True(scrolls || controls <= 4, $"La pestaña «{header}» tiene {controls} controles y no se desplaza: en una ventana baja quedan fuera de vista.");
        }

        Assert.Contains(tabs, t => (string?)t.Attribute("Header") == "Rendimiento" && t.Elements().First().Name == ui + "ScrollViewer");
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
