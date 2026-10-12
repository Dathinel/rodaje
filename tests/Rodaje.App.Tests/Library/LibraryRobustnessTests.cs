using Rodaje.App.Views.Library;
using Rodaje.Core.Model;

namespace Rodaje.App.Tests.Library;

/// <summary>Los detalles de la revisión de la tarea 30 que no tenían prueba.</summary>
public sealed class LibraryRobustnessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rodaje-biblioteca-robusta-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void VersionFutura_SeApartaYGuardarNoLaPisa()
    {
        var store = new LibraryOrganizationStore(_dir);
        Directory.CreateDirectory(_dir);
        string future = """{"version":99,"folders":[{"id":"f","name":"De la versión nueva","parent":null}],"media":{},"favorites":[]}""";
        File.WriteAllText(store.PathFor("p"), future);

        LibraryOrganization org = store.Load("p");
        org.CreateFolder(null, "Nueva");
        store.Save("p", org);

        Assert.Equal(["Nueva"], org.Folders.Select(f => f.Name));
        Assert.Equal(future, File.ReadAllText(Path.Combine(_dir, "p.v99.json")));
        Assert.Equal(["Nueva"], new LibraryOrganizationStore(_dir).Load("p").Folders.Select(f => f.Name));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("Aux")]
    [InlineData("prn")]
    [InlineData("COM1")]
    [InlineData("lpt9")]
    public void NombreReservadoDeWindows_UsaElResumen(string id)
    {
        var store = new LibraryOrganizationStore(_dir);

        Assert.StartsWith("h-", Path.GetFileName(store.PathFor(id)), StringComparison.Ordinal);
    }

    [Fact]
    public void NombreParecidoAUnReservado_SeUsaTalCual()
    {
        var store = new LibraryOrganizationStore(_dir);

        Assert.Equal("console.json", Path.GetFileName(store.PathFor("console")));
        Assert.Equal("COM10.json", Path.GetFileName(store.PathFor("COM10")));
    }

    [Fact]
    public void Tipo_VideoSinTasaDeCuadros_SigueSiendoVideo()
    {
        var sinTasaConSonido = new MediaRef { Id = "a", Path = @"C:\v\vfr.mp4", HasVideo = true, HasAudio = true, Duration = Rodaje.Core.Timing.Time.FromSeconds(30) };
        var sinTasaMudo = new MediaRef { Id = "b", Path = @"C:\v\pantalla.mkv", HasVideo = true, Duration = Rodaje.Core.Timing.Time.FromSeconds(12) };
        var fotoRara = new MediaRef { Id = "c", Path = @"C:\f\foto.raw", HasVideo = true, Duration = Rodaje.Core.Timing.Time.FromSeconds(0.04) };

        Assert.Equal(LibraryKind.Video, LibraryFilter.KindOf(sinTasaConSonido));
        Assert.Equal(LibraryKind.Video, LibraryFilter.KindOf(sinTasaMudo));
        Assert.Equal(LibraryKind.Image, LibraryFilter.KindOf(fotoRara));
    }

    [Fact]
    public void Plegar_QuitaLasTildesDescompuestas()
    {
        // «Montaña» y «Canción» en NFD, como llegan algunos nombres de archivo copiados de un Mac.
        string montana = "Montan\u0303a";
        string cancion = "Cancio\u0301n";

        Assert.Equal("montana", SearchText.Fold(montana));
        Assert.Equal("cancion", SearchText.Fold(cancion));
        Assert.Equal(SearchText.Fold("Montaña"), SearchText.Fold(montana));
        Assert.True(LibraryFilter.Matches(new MediaRef { Name = montana, Path = @"C:\v\x.mp4" }, SearchText.Words("montaña")));
    }

    [Fact]
    public void IdDeCarpeta_NoSeReutilizaTrasBorrar()
    {
        var org = new LibraryOrganization();
        LibraryFolder a = org.CreateFolder(null, "A");
        LibraryFolder b = org.CreateFolder(null, "B");
        org.DeleteFolder(b.Id);

        LibraryFolder c = org.CreateFolder(null, "C");

        Assert.NotEqual(b.Id, c.Id);
        Assert.NotEqual(a.Id, c.Id);
        Assert.StartsWith("folder-", c.Id, StringComparison.Ordinal);
    }

    [Fact]
    public void PrepararYEscribir_PorSeparado_ComoGuardar()
    {
        var store = new LibraryOrganizationStore(_dir);
        var org = new LibraryOrganization();
        org.SetFavorite("m1", true);

        LibrarySaveRequest request = Assert.IsType<LibrarySaveRequest>(store.Prepare("p", org));
        Assert.False(File.Exists(store.PathFor("p")));
        store.Write(request);

        Assert.True(new LibraryOrganizationStore(_dir).Load("p").IsFavorite("m1"));
        Assert.Null(store.Prepare("p", org));
    }

    [Fact]
    public void PrepararVacia_SinArchivo_NoCreaArchivo()
    {
        // Vale para una clave que no se cargó por herencia; si se heredó, el vacío sí se escribe
        // (LibraryOrganizationInheritanceTests).
        var store = new LibraryOrganizationStore(_dir);

        LibrarySaveRequest? request = store.Prepare("p", new LibraryOrganization());
        if (request is not null)
        {
            store.Write(request);
        }

        Assert.False(File.Exists(store.PathFor("p")));
    }
}
