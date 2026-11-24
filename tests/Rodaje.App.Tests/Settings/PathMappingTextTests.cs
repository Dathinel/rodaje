using Rodaje.App.Services.Settings;
using Rodaje.Core.Model;

namespace Rodaje.App.Tests.Settings;

public sealed class PathMappingTextTests
{
    [Fact]
    public void Lee_una_fila_por_linea_y_vuelve_al_mismo_texto()
    {
        List<PathMapping> rows = PathMappingText.Parse("D:\\Material | /media/daniel/Material\r\n\n  /home/a/b  |  E:\\b  \nsin separador\n | /solo/destino");

        Assert.Equal(new PathMapping[] { new(@"D:\Material", "/media/daniel/Material"), new("/home/a/b", @"E:\b") }, rows);
        Assert.Equal(rows, PathMappingText.Parse(PathMappingText.Format(rows)));
    }

    [Fact]
    public void Sin_texto_no_hay_filas() => Assert.Empty(PathMappingText.Parse(null));

    [Fact]
    public void Los_ajustes_guardan_y_leen_el_mapeo()
    {
        string path = Path.Combine(Path.GetTempPath(), "rodaje-mapeo-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new SettingsStore(path);
            store.Load();
            store.Current.PathMappings = [new PathMapping(@"D:\Material", "/media/Material")];
            store.Save();

            var reread = new SettingsStore(path);
            Assert.Null(reread.Load());
            Assert.Equal(new PathMapping[] { new(@"D:\Material", "/media/Material") }, reread.Current.PathMappings);
        }
        finally
        {
            File.Delete(path);
        }
    }
}