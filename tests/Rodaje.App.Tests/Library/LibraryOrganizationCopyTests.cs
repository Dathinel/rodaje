using Rodaje.App.Views.Library;

namespace Rodaje.App.Tests.Library;

/// <summary>Un <c>.rodaje</c> copiado conserva el id del proyecto: original y copia no deben compartir la organización.</summary>
public sealed class LibraryOrganizationCopyTests : IDisposable
{
    private const string Id = "project-1";
    private const string Original = @"C:\Proyectos\viaje.rodaje";
    private const string Copy = @"D:\Respaldo\viaje copia.rodaje";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rodaje-biblioteca-copia-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Clave_DistingueOriginalYCopia_SinMirarMayusculas()
    {
        string original = LibraryOrganizationStore.KeyFor(Id, Original);

        Assert.NotEqual(original, LibraryOrganizationStore.KeyFor(Id, Copy));
        Assert.Equal(original, LibraryOrganizationStore.KeyFor(Id, Original.ToUpperInvariant()));
        Assert.Equal(original, LibraryOrganizationStore.KeyFor(Id, Original.Replace('\\', '/')));
        Assert.Equal(Id, LibraryOrganizationStore.KeyFor(Id, null));
    }

    [Fact]
    public void Clave_DaUnArchivoSeguroDentroDeLaCarpeta()
    {
        var store = new LibraryOrganizationStore(_dir);

        string path = store.PathFor(LibraryOrganizationStore.KeyFor(Id, Original));

        Assert.Equal(_dir, Path.GetDirectoryName(path));
        Assert.StartsWith("h-", Path.GetFileName(path), StringComparison.Ordinal);
    }

    [Fact]
    public void OrdenarLaCopia_NoCambiaElOriginal()
    {
        var store = new LibraryOrganizationStore(_dir);
        string originalKey = LibraryOrganizationStore.KeyFor(Id, Original);
        string copyKey = LibraryOrganizationStore.KeyFor(Id, Copy);
        LibraryOrganization original = store.Load(originalKey, Id);
        LibraryFolder viaje = original.CreateFolder(null, "Viaje");
        original.MoveMedia(["m1", "m2"], viaje.Id);
        store.Save(originalKey, original);

        LibraryOrganization copy = new LibraryOrganizationStore(_dir).Load(copyKey, Id);
        copy.MoveMedia(["m2"], null);
        copy.CreateFolder(null, "Solo en la copia");
        new LibraryOrganizationStore(_dir).Save(copyKey, copy);

        LibraryOrganization reread = new LibraryOrganizationStore(_dir).Load(originalKey, Id);
        Assert.Equal(viaje.Id, reread.FolderOf("m1"));
        Assert.Equal(viaje.Id, reread.FolderOf("m2"));
        Assert.Equal(["Viaje"], reread.Folders.Select(f => f.Name));
    }

    [Fact]
    public void SinArchivoPropio_PartePorElDelIdSinTocarlo()
    {
        // Lo guardado antes de esta versión (o antes del primer guardado del proyecto) queda con la clave del id.
        var store = new LibraryOrganizationStore(_dir);
        var legacy = new LibraryOrganization();
        LibraryFolder viaje = legacy.CreateFolder(null, "Viaje");
        legacy.MoveMedia(["m1"], viaje.Id);
        legacy.SetFavorite("m1", true);
        store.Save(Id, legacy);
        string legacyText = File.ReadAllText(store.PathFor(Id));

        string key = LibraryOrganizationStore.KeyFor(Id, Original);
        LibraryOrganization read = new LibraryOrganizationStore(_dir).Load(key, Id);
        Assert.Equal(viaje.Id, read.FolderOf("m1"));
        Assert.True(read.IsFavorite("m1"));

        read.MoveMedia(["m1"], null);
        var writer = new LibraryOrganizationStore(_dir);
        writer.Save(key, read);

        Assert.Equal(legacyText, File.ReadAllText(store.PathFor(Id)));
        Assert.Null(new LibraryOrganizationStore(_dir).Load(key, Id).FolderOf("m1"));
    }

    [Fact]
    public void ConArchivoPropio_NoMiraElDelId()
    {
        var store = new LibraryOrganizationStore(_dir);
        var legacy = new LibraryOrganization();
        legacy.CreateFolder(null, "Del id");
        store.Save(Id, legacy);
        string key = LibraryOrganizationStore.KeyFor(Id, Original);
        var own = new LibraryOrganization();
        own.CreateFolder(null, "Propia");
        store.Save(key, own);

        Assert.Equal(["Propia"], new LibraryOrganizationStore(_dir).Load(key, Id).Folders.Select(f => f.Name));
    }

    [Fact]
    public void AlCargar_NoSeOlvidanLosMediosQueElProyectoNoTiene()
    {
        // La copia puede no tener todos los medios del original; leer no debe borrarlos del archivo.
        var store = new LibraryOrganizationStore(_dir);
        var org = new LibraryOrganization();
        LibraryFolder viaje = org.CreateFolder(null, "Viaje");
        org.MoveMedia(["m1", "ausente"], viaje.Id);
        org.SetFavorite("ausente", true);
        store.Save(Id, org);

        LibraryOrganization read = new LibraryOrganizationStore(_dir).Load(Id);
        read.MoveMedia(["m1"], null);
        new LibraryOrganizationStore(_dir).Save(Id, read);

        LibraryOrganization again = new LibraryOrganizationStore(_dir).Load(Id);
        Assert.Equal(viaje.Id, again.FolderOf("ausente"));
        Assert.True(again.IsFavorite("ausente"));
    }
}
