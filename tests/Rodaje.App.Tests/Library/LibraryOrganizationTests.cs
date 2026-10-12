using Rodaje.App.Views.Library;

namespace Rodaje.App.Tests.Library;

public class LibraryOrganizationTests
{
    [Fact]
    public void CrearCarpeta_UsaUnNombreLibreAlLado()
    {
        var org = new LibraryOrganization();
        LibraryFolder a = org.CreateFolder(null);
        LibraryFolder b = org.CreateFolder(null);
        LibraryFolder inner = org.CreateFolder(a.Id);

        Assert.Equal("Carpeta nueva", a.Name);
        Assert.Equal("Carpeta nueva 2", b.Name);
        Assert.Equal("Carpeta nueva", inner.Name);
        Assert.Equal(a.Id, inner.ParentId);
        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public void CrearCarpeta_EnUnPadreQueNoExiste_VaALaRaiz()
    {
        var org = new LibraryOrganization();
        LibraryFolder folder = org.CreateFolder("no-existe", "  Tomas  ");

        Assert.Null(folder.ParentId);
        Assert.Equal("Tomas", folder.Name);
    }

    [Fact]
    public void Renombrar_RechazaVacioYRepetidoSinMirarMayusculasNiTildes()
    {
        var org = new LibraryOrganization();
        LibraryFolder musica = org.CreateFolder(null, "Música");
        LibraryFolder otra = org.CreateFolder(null, "Otra");

        Assert.Equal("Escribe un nombre.", org.NameProblem(otra.Id, "   "));
        Assert.Equal("Ya hay una carpeta «MUSICA» aquí.", org.NameProblem(otra.Id, "MUSICA"));
        Assert.False(org.Rename(otra.Id, "musica"));
        Assert.Equal("Otra", otra.Name);

        Assert.True(org.Rename(musica.Id, "Música de fondo"));
        Assert.Equal("Música de fondo", musica.Name);
        Assert.False(org.Rename(musica.Id, "Música de fondo"));
    }

    [Fact]
    public void Renombrar_QuitaControlesYCortaNombresLargos()
    {
        var org = new LibraryOrganization();
        LibraryFolder folder = org.CreateFolder(null);

        Assert.True(org.Rename(folder.Id, "a\tb\n" + new string('x', 200)));
        Assert.Equal(LibraryOrganization.MaxNameLength, folder.Name.Length);
        Assert.StartsWith("ab", folder.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void MoverMedios_CuentaSoloLosQueCambian_YNullEsLaRaiz()
    {
        var org = new LibraryOrganization();
        LibraryFolder folder = org.CreateFolder(null, "Tomas");

        Assert.Equal(2, org.MoveMedia(["m1", "m2", "m1"], folder.Id));
        Assert.Equal(0, org.MoveMedia(["m1"], folder.Id));
        Assert.Equal(folder.Id, org.FolderOf("m1"));
        Assert.Null(org.FolderOf("m3"));

        Assert.Equal(1, org.MoveMedia(["m2"], null));
        Assert.Null(org.FolderOf("m2"));
        Assert.False(org.MediaFolders.ContainsKey("m2"));
    }

    [Fact]
    public void BorrarCarpeta_SubeSusMediosYSubcarpetasAlPadre()
    {
        var org = new LibraryOrganization();
        LibraryFolder outer = org.CreateFolder(null, "Viaje");
        LibraryFolder middle = org.CreateFolder(outer.Id, "Día 1");
        LibraryFolder inner = org.CreateFolder(middle.Id, "Playa");
        org.CreateFolder(outer.Id, "Playa");
        org.MoveMedia(["m1"], middle.Id);

        Assert.True(org.DeleteFolder(middle.Id));

        Assert.Null(org.FindFolder(middle.Id));
        Assert.Equal(outer.Id, org.FolderOf("m1"));
        Assert.Equal(outer.Id, inner.ParentId);
        Assert.Equal("Playa 2", inner.Name);
        Assert.False(org.DeleteFolder(middle.Id));
    }

    [Fact]
    public void MoverCarpeta_NoDejaMeterlaDentroDeSiMisma()
    {
        var org = new LibraryOrganization();
        LibraryFolder a = org.CreateFolder(null, "A");
        LibraryFolder b = org.CreateFolder(a.Id, "B");
        LibraryFolder c = org.CreateFolder(null, "C");

        Assert.False(org.MoveFolder(a.Id, b.Id));
        Assert.False(org.MoveFolder(a.Id, a.Id));
        Assert.True(org.MoveFolder(c.Id, b.Id));
        Assert.Equal(["A", "B", "C"], org.PathTo(c.Id).Select(f => f.Name));
        Assert.True(org.MoveFolder(b.Id, null));
        Assert.Null(b.ParentId);
    }

    [Fact]
    public void Hijos_YArbol_VanPorNombre()
    {
        var org = new LibraryOrganization();
        LibraryFolder zeta = org.CreateFolder(null, "zeta");
        org.CreateFolder(null, "Árbol");
        org.CreateFolder(zeta.Id, "dentro");

        Assert.Equal(["Árbol", "zeta"], org.Children(null).Select(f => f.Name));
        Assert.Equal([("Árbol", 0), ("zeta", 0), ("dentro", 1)], org.Tree().Select(t => (t.Folder.Name, t.Depth)));
        Assert.Empty(org.PathTo(null));
    }

    [Fact]
    public void Favoritos_SeMarcanYDesmarcan_YLaRevisionSubeSoloSiCambia()
    {
        var org = new LibraryOrganization();
        int start = org.Revision;

        Assert.True(org.ToggleFavorite("m1"));
        Assert.True(org.IsFavorite("m1"));
        org.SetFavorite("m1", true);
        Assert.Equal(start + 1, org.Revision);
        Assert.False(org.ToggleFavorite("m1"));
        Assert.False(org.IsFavorite("m1"));
    }

    [Fact]
    public void Podar_OlvidaMediosQueYaNoEstan()
    {
        var org = new LibraryOrganization();
        LibraryFolder folder = org.CreateFolder(null);
        org.MoveMedia(["m1", "m2"], folder.Id);
        org.SetFavorite("m1", true);
        org.SetFavorite("m3", true);

        org.Prune(["m1"]);

        Assert.Equal(["m1"], org.MediaFolders.Keys);
        Assert.Equal(["m1"], org.Favorites);
    }

    [Fact]
    public void Desde_LoLeido_ArreglaCiclosPadresPerdidosYNombresRepetidos()
    {
        LibraryFolder[] folders =
        [
            new() { Id = "a", Name = "Uno", ParentId = "b" },
            new() { Id = "b", Name = "Dos", ParentId = "a" },
            new() { Id = "c", Name = "uno", ParentId = "perdida" },
            new() { Id = "c", Name = "Repetida" },
            new() { Id = "", Name = "Sin id" },
            new() { Id = "d", Name = "" },
        ];
        var media = new Dictionary<string, string> { ["m1"] = "c", ["m2"] = "perdida" };

        LibraryOrganization org = LibraryOrganization.From(folders, media, ["m1", ""]);

        Assert.Equal(["a", "b", "c", "d"], org.Folders.Select(f => f.Id));
        Assert.Null(org.FindFolder("c")!.ParentId);
        Assert.Contains(org.Folders, f => f.ParentId is null && f.Id is "a" or "b");
        Assert.NotEmpty(org.Tree());
        Assert.Equal(4, org.Tree().Count);
        Assert.Equal("Carpeta nueva", org.FindFolder("d")!.Name);
        Assert.Equal("c", org.FolderOf("m1"));
        Assert.Null(org.FolderOf("m2"));
        Assert.Equal(["m1"], org.Favorites);
    }
}
