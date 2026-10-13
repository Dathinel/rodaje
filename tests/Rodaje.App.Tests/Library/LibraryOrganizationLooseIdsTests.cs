using System.Diagnostics;
using Rodaje.App.Views.Library;

namespace Rodaje.App.Tests.Library;

/// <summary>
/// Ids sueltos (padres y carpetas de medio que no existen) al cargar la biblioteca: cada uno caía a una búsqueda lineal en
/// la lista de carpetas, y la carga era cuadrática.
/// </summary>
public sealed class LibraryOrganizationLooseIdsTests
{
    // En la CI se cuentan las lecturas del id, no el reloj: con el runner de Windows cargado, una carga de 0,3 s llegó a
    // tardar 33,7 s en otra prueba de la biblioteca. El tope de 4 s queda en la gemela TiempoReal.
    [Fact]
    public void MilesDePadresYCarpetasDeMedioInexistentes_CarganEnTiempoLineal() => LoadLoose(20_000);

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

        // Se cuentan las lecturas del id (una búsqueda lineal lee el de cada carpeta) en vez de medir segundos, que dependen
        // de la máquina y de la carga de la CI.
        LibraryFolder.IdReads = 0;
        LibraryOrganization org = LibraryOrganization.From(folders, media, null);
        long reads = LibraryFolder.IdReads;

        Assert.Equal(count, org.Folders.Count);
        Assert.All(org.Folders, f => Assert.Null(f.ParentId));
        Assert.Empty(org.MediaFolders);
        // Con la búsqueda lineal eran del orden de count² (4·10⁸); ahora, unas pocas por carpeta.
        Assert.True(reads <= 20L * count, $"Leyó el id {reads} veces para {count} carpetas.");
    }

    [Fact]
    public void ElIdSoloSeDaAlCrear_YElIndiceSigueALaLista()
    {
        // Un id cambiado a mano dejaba el índice atrasado (de ahí el contador global que lo rehacía): ahora es de solo inicio.
        System.Reflection.MethodInfo setter = typeof(LibraryFolder).GetProperty(nameof(LibraryFolder.Id))!.SetMethod!;
        Assert.Contains(typeof(System.Runtime.CompilerServices.IsExternalInit), setter.ReturnParameter.GetRequiredCustomModifiers());

        LibraryOrganization org = LibraryOrganization.From(
            [new LibraryFolder { Id = "a", Name = "A" }, new LibraryFolder { Id = "b", Name = "B" }, new LibraryFolder { Id = "a", Name = "Otra A" }], null, null);
        Assert.Equal(["a", "b"], org.Folders.Select(f => f.Id));
        Assert.Equal("A", org.FindFolder("a")!.Name);

        LibraryFolder created = org.CreateFolder("b", "Dentro");
        Assert.Same(created, org.FindFolder(created.Id));
        Assert.True(org.DeleteFolder("a"));
        Assert.Null(org.FindFolder("a"));
        Assert.True(org.DeleteFolder(created.Id));
        Assert.Null(org.FindFolder(created.Id));
        Assert.All(org.Folders, f => Assert.Same(f, org.FindFolder(f.Id)));
    }
}
