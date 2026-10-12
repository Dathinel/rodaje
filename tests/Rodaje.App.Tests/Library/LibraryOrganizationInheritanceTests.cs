using Rodaje.App.Views.Library;

namespace Rodaje.App.Tests.Library;

/// <summary>
/// Herencia de la organización por <c>fallbackKey</c> (copia de un <c>.rodaje</c> o proyecto de antes de la tarea 54):
/// vaciarla tiene que quedar guardado.
/// </summary>
public sealed class LibraryOrganizationInheritanceTests : IDisposable
{
    private const string Id = "project-1";
    private const string Copy = @"D:\Respaldo\viaje copia.rodaje";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rodaje-biblioteca-herencia-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Heredada_Vaciar_SeGuardaYNoReaparece()
    {
        var store = new LibraryOrganizationStore(_dir);
        var legacy = new LibraryOrganization();
        LibraryFolder viaje = legacy.CreateFolder(null, "Viaje");
        legacy.MoveMedia(["m1"], viaje.Id);
        legacy.SetFavorite("m2", true);
        store.Save(Id, legacy);

        string key = LibraryOrganizationStore.KeyFor(Id, Copy);
        var copyStore = new LibraryOrganizationStore(_dir);
        LibraryOrganization copy = copyStore.Load(key, Id);
        Assert.Equal(["Viaje"], copy.Folders.Select(f => f.Name));
        copy.DeleteFolder(viaje.Id);
        copy.SetFavorite("m2", false);
        copyStore.Save(key, copy);

        Assert.True(File.Exists(store.PathFor(key)));
        LibraryOrganization reread = new LibraryOrganizationStore(_dir).Load(key, Id);
        Assert.Empty(reread.Folders);
        Assert.False(reread.IsFavorite("m2"));
        Assert.Null(reread.FolderOf("m1"));
    }

    [Fact]
    public void SinNadaQueHeredar_VaciaNoCreaArchivo()
    {
        // Sin archivo por id no hay herencia que tapar: sigue sin crear un archivo vacío.
        var store = new LibraryOrganizationStore(_dir);
        string key = LibraryOrganizationStore.KeyFor(Id, Copy);

        LibraryOrganization org = store.Load(key, Id);
        store.Save(key, org);
        LibrarySaveRequest? request = store.Prepare(key, new LibraryOrganization());
        if (request is not null)
        {
            store.Write(request);
        }

        Assert.False(File.Exists(store.PathFor(key)));
    }
}
