using Rodaje.App.Services;
using Rodaje.App.Services.Settings;
using Rodaje.App.Tests.Support;

namespace Rodaje.App.Tests.Settings;

/// <summary>Carpeta de los medios generados (D-034): por defecto junto al proyecto, o la elegida, que se recuerda.</summary>
public class GeneratedMediaTests
{
    [Fact]
    public void PorDefectoVaJuntoAlProyectoYSinGuardarEnLosDatosDeLaApp()
    {
        using var dir = new TempDirectory();
        string project = dir.File("Mi viaje.rodaje");
        Assert.Equal(Path.Combine(dir.Path, "Mi viaje.medios"), GeneratedMedia.Folder(project, null));
        Assert.Equal("Junto al proyecto (Mi viaje.medios)", GeneratedMedia.Describe(project, null));
        Assert.StartsWith(AppDataPaths.Root, GeneratedMedia.Folder(null, null), StringComparison.Ordinal);
        Assert.Equal(GeneratedMedia.Folder(project, null), GeneratedMedia.Folder(project, "relativa\\no"));
    }

    [Fact]
    public void LaElegidaManda_SeCreaYSeRecuerdaEnLosAjustes()
    {
        using var dir = new TempDirectory();
        string chosen = Path.Combine(dir.Path, "Medios con tildé");
        string made = GeneratedMedia.Ensure(dir.File("p.rodaje"), chosen);
        Assert.Equal(chosen, made);
        Assert.True(Directory.Exists(made));
        Assert.Equal(chosen, GeneratedMedia.Describe(dir.File("p.rodaje"), chosen));

        var store = new SettingsStore(dir.File("ajustes.json"));
        store.Current.GeneratedMediaFolder = chosen;
        store.Save();
        var loaded = new SettingsStore(dir.File("ajustes.json"));
        Assert.Null(loaded.Load());
        Assert.Equal(chosen, loaded.Current.GeneratedMediaFolder);
    }
}
