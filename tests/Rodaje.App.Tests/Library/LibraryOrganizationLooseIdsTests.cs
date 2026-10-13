using System.Diagnostics;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Library;

namespace Rodaje.App.Tests.Library;

/// <summary>
/// Ids sueltos (padres y carpetas de medio que no existen) al cargar la biblioteca: cada uno caía a una búsqueda lineal en
/// la lista de carpetas, y la carga era cuadrática.
/// </summary>
public sealed class LibraryOrganizationLooseIdsTests
{
    // En la CI se mira cómo crece el tiempo (Growth), no el reloj: con el runner de Windows cargado, una carga de 0,3 s
    // llegó a tardar 33,7 s en otra prueba de la biblioteca. El tope de 4 s queda en la gemela TiempoReal.
    [Fact]
    public void MilesDePadresYCarpetasDeMedioInexistentes_CarganEnTiempoLineal() =>
        Growth.AssertLinear(LoadLoose, 4_000, 32_000, "Ids sueltos");

    [Fact]
    [Trait("Categoria", "TiempoReal")]
    public void MilesDePadresYCarpetasDeMedioInexistentes_CarganEnMenosDeCuatroSegundos()
    {
        var watch = Stopwatch.StartNew();
        LoadLoose(20_000);
        watch.Stop();

        // Con la búsqueda lineal tardaba unos 8 s en Release; ahora, menos de medio segundo.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"Tardó {watch.Elapsed.TotalSeconds:F1} s");
    }

    private static void LoadLoose(int count)
    {
        List<LibraryFolder> folders = [.. Enumerable.Range(0, count).Select(i => new LibraryFolder { Id = $"f{i}", Name = $"Carpeta {i}", ParentId = $"falta{i}" })];
        Dictionary<string, string> media = Enumerable.Range(0, count).ToDictionary(i => $"m{i}", i => $"nada{i}");

        LibraryOrganization org = LibraryOrganization.From(folders, media, null);

        Assert.Equal(count, org.Folders.Count);
        Assert.All(org.Folders, f => Assert.Null(f.ParentId));
        Assert.Empty(org.MediaFolders);
    }

    [Fact]
    public void UnIdCambiadoAMano_SeEncuentraPorElNuevoYNoPorElViejo()
    {
        LibraryOrganization org = LibraryOrganization.From(
            [new LibraryFolder { Id = "a", Name = "A" }, new LibraryFolder { Id = "b", Name = "B" }, new LibraryFolder { Id = "c", Name = "C" }], null, null);
        LibraryFolder a = org.FindFolder("a")!;
        LibraryFolder b = org.FindFolder("b")!;
        LibraryFolder c = org.FindFolder("c")!;

        a.Id = "nuevo";
        Assert.Same(a, org.FindFolder("nuevo"));
        Assert.Null(org.FindFolder("a"));

        // Con ids repetidos gana la primera de la lista; al borrarla, la siguiente con ese id.
        b.Id = "c";
        Assert.Same(b, org.FindFolder("c"));
        Assert.True(org.DeleteFolder("c"));
        Assert.Same(c, org.FindFolder("c"));
    }
}
