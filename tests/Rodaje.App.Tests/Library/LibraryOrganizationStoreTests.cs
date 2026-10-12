using Rodaje.App.Views.Library;

namespace Rodaje.App.Tests.Library;

public sealed class LibraryOrganizationStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rodaje-biblioteca-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void GuardarYLeer_ConservaCarpetasMediosYFavoritos()
    {
        var store = new LibraryOrganizationStore(_dir);
        var org = new LibraryOrganization();
        LibraryFolder viaje = org.CreateFolder(null, "Viaje");
        LibraryFolder playa = org.CreateFolder(viaje.Id, "Playa");
        org.MoveMedia(["m1"], playa.Id);
        org.SetFavorite("m2", true);

        store.Save("project-1", org);
        LibraryOrganization read = new LibraryOrganizationStore(_dir).Load("project-1");

        Assert.Equal(["Viaje", "Playa"], read.PathTo(playa.Id).Select(f => f.Name));
        Assert.Equal(playa.Id, read.FolderOf("m1"));
        Assert.True(read.IsFavorite("m2"));
        Assert.True(File.Exists(Path.Combine(_dir, "project-1.json")));
    }

    [Fact]
    public void SinArchivo_EmpiezaVacia_YVaciaNoCreaArchivo()
    {
        var store = new LibraryOrganizationStore(_dir);

        Assert.Empty(store.Load("p").Folders);
        store.Save("p", new LibraryOrganization());
        Assert.False(File.Exists(store.PathFor("p")));
    }

    [Fact]
    public void ArchivoDanado_SeApartaYEmpiezaVacia()
    {
        var store = new LibraryOrganizationStore(_dir);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.PathFor("p"), "{ no es json");

        Assert.Empty(store.Load("p").Folders);
        Assert.False(File.Exists(store.PathFor("p")));
        Assert.True(File.Exists(Path.Combine(_dir, "p.dañado.json")));
    }

    [Fact]
    public void IdRaro_UsaUnNombreDeArchivoSeguro()
    {
        var store = new LibraryOrganizationStore(_dir);

        string path = store.PathFor(@"..\..\con:*");

        Assert.Equal(_dir, Path.GetDirectoryName(path));
        Assert.StartsWith("h-", Path.GetFileName(path), StringComparison.Ordinal);
        Assert.Equal(path, store.PathFor(@"..\..\con:*"));
    }

    [Fact]
    public void ArchivoConValoresNulos_SeLeeSinFallar()
    {
        var store = new LibraryOrganizationStore(_dir);
        Directory.CreateDirectory(_dir);
        File.WriteAllText(store.PathFor("p"), """{"version":1,"folders":[null,{"id":"f","name":null,"parent":null}],"media":null,"favorites":null}""");

        LibraryOrganization org = store.Load("p");

        Assert.Equal("Carpeta nueva", Assert.Single(org.Folders).Name);
        Assert.Empty(org.Favorites);
    }
}
