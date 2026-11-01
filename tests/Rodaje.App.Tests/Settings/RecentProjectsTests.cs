using Rodaje.App.Services.Settings;

namespace Rodaje.App.Tests.Settings;

/// <summary>Archivo › Proyectos recientes: hasta diez, sin los que ya no existen y con la ruta abreviada.</summary>
public class RecentProjectsTests
{
    [Fact]
    public void Build_QuitaLosQueNoExistenYRespetaElOrden()
    {
        string[] paths = [@"C:\A\uno.rodaje", @"C:\A\dos.rodaje", @"C:\A\tres.rodaje"];

        IReadOnlyList<RecentProjectEntry> entries = RecentProjects.Build(paths, p => !p.EndsWith("dos.rodaje", StringComparison.Ordinal));

        Assert.Equal([@"C:\A\uno.rodaje", @"C:\A\tres.rodaje"], entries.Select(e => e.Path));
    }

    [Fact]
    public void Build_NoPasaDeDiezNiRepiteAunqueCambieLaMayuscula()
    {
        List<string> paths = [.. Enumerable.Range(1, 12).Select(i => $@"C:\P\{i}.rodaje")];
        paths.Insert(1, @"c:\p\1.RODAJE");

        IReadOnlyList<RecentProjectEntry> entries = RecentProjects.Build(paths, _ => true);

        Assert.Equal(AppSettings.MaxRecentProjects, entries.Count);
        Assert.Equal(@"C:\P\1.rodaje", entries[0].Path);
        Assert.Equal(@"C:\P\2.rodaje", entries[1].Path);
    }

    [Fact]
    public void Abbreviate_DejaEnteraUnaRutaCorta()
    {
        Assert.Equal(@"C:\Videos\boda.rodaje", RecentProjects.Abbreviate(@"C:\Videos\boda.rodaje"));
    }

    [Fact]
    public void Abbreviate_ConservaLaRaizElNombreYLasUltimasCarpetasQueQuepan()
    {
        string path = @"C:\Users\Daniel\Documents\Proyectos de video\2026\Bodas\Marta y Juan.rodaje";

        string label = RecentProjects.Abbreviate(path, 48);

        Assert.Equal(@"C:\…\2026\Bodas\Marta y Juan.rodaje", label);
        Assert.True(label.Length <= 48);
    }

    [Fact]
    public void Abbreviate_SiNoCabeNadaMasQuedaRaizYNombre()
    {
        string label = RecentProjects.Abbreviate(@"D:\una carpeta muy larga\otra carpeta larga\archivo.rodaje", 30);

        Assert.Equal(@"D:\…\archivo.rodaje", label);
    }

    [Fact]
    public void Abbreviate_EntiendeRutasDeRedYDeUnix()
    {
        Assert.Equal(@"\\nas\…\final.rodaje", RecentProjects.Abbreviate(@"\\nas\videos\clientes\2026\final.rodaje", 24));
        Assert.Equal("/…/x/final.rodaje", RecentProjects.Abbreviate("/home/daniel/videos/x/final.rodaje", 20));
    }

    [Fact]
    public void AppSettings_PruneRecentProjectsQuitaLosQueFaltan()
    {
        var settings = new AppSettings();
        settings.AddRecentProject(@"C:\A\uno.rodaje");
        settings.AddRecentProject(@"C:\A\dos.rodaje");

        int removed = settings.PruneRecentProjects(p => p.EndsWith("dos.rodaje", StringComparison.Ordinal));

        Assert.Equal(1, removed);
        Assert.Equal([@"C:\A\dos.rodaje"], settings.RecentProjects);
    }
}
