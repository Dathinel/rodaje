using Rodaje.App.Views.Library;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;

namespace Rodaje.App.Tests.Library;

public class LibraryFilterTests
{
    private static readonly MediaRef Toma = new() { Id = "toma", Name = "Toma en la montaña", Path = @"C:\v\toma01.mp4", HasVideo = true, HasAudio = true, FrameRate = Rational.Fps30, Width = 1920, Height = 1080 };
    private static readonly MediaRef Foto = new() { Id = "foto", Name = "Cumbre", Path = @"C:\f\cumbre.JPG", HasVideo = true, FrameRate = Rational.Fps25, Width = 4000, Height = 3000 };
    private static readonly MediaRef Fija = new() { Id = "fija", Name = "Fija", Path = @"C:\f\fija.raw", HasVideo = true, Width = 10, Height = 10 };
    private static readonly MediaRef Cancion = new() { Id = "cancion", Name = "Canción", Path = @"C:\a\cancion.mp3", HasAudio = true, SampleRate = 44100, AudioChannels = 2 };

    private static readonly MediaRef[] All = [Toma, Foto, Fija, Cancion];

    private static string[] Ids(LibraryOrganization org, LibraryQuery query) => [.. LibraryFilter.Apply(All, org, query).Select(m => m.Id)];

    [Fact]
    public void Tipo_DistingueVideoImagenYAudio()
    {
        Assert.Equal(LibraryKind.Video, LibraryFilter.KindOf(Toma));
        Assert.Equal(LibraryKind.Image, LibraryFilter.KindOf(Foto));
        Assert.Equal(LibraryKind.Image, LibraryFilter.KindOf(Fija));
        Assert.Equal(LibraryKind.Audio, LibraryFilter.KindOf(Cancion));
    }

    [Fact]
    public void SinFiltro_EnLaRaiz_MuestraLosDeLaRaizEnElOrdenDelProyecto()
    {
        var org = new LibraryOrganization();
        LibraryFolder folder = org.CreateFolder(null, "Fotos");
        org.MoveMedia(["foto", "fija"], folder.Id);

        Assert.Equal(["toma", "cancion"], Ids(org, new LibraryQuery()));
        Assert.Equal(["foto", "fija"], Ids(org, new LibraryQuery(folder.Id)));
        Assert.Equal(["Fotos"], LibraryFilter.Folders(org, new LibraryQuery()).Select(f => f.Name));
        Assert.Empty(LibraryFilter.Folders(org, new LibraryQuery(folder.Id)));
    }

    [Fact]
    public void Buscar_MiraTodaLaBiblioteca_SinMayusculasNiTildes_YPorPalabras()
    {
        var org = new LibraryOrganization();
        LibraryFolder folder = org.CreateFolder(null, "Videos");
        org.MoveMedia(["toma"], folder.Id);

        Assert.Equal(["toma"], Ids(org, new LibraryQuery(null, "MONTANA")));
        Assert.Equal(["toma"], Ids(org, new LibraryQuery(null, "montaña toma")));
        Assert.Equal(["toma"], Ids(org, new LibraryQuery(null, "toma01")));
        Assert.Equal(["cancion"], Ids(org, new LibraryQuery(folder.Id, "cancion")));
        Assert.Empty(Ids(org, new LibraryQuery(null, "montaña playa")));
        Assert.Empty(LibraryFilter.Folders(org, new LibraryQuery(null, "videos")));
    }

    [Fact]
    public void Tipo_SeAplicaEnLaCarpetaAbierta()
    {
        var org = new LibraryOrganization();

        Assert.Equal(["foto", "fija"], Ids(org, new LibraryQuery(Kind: LibraryKind.Image)));
        Assert.Equal(["cancion"], Ids(org, new LibraryQuery(Kind: LibraryKind.Audio)));
        Assert.Equal(["toma"], Ids(org, new LibraryQuery(Kind: LibraryKind.Video)));
    }

    [Fact]
    public void SoloFavoritos_MiraTodasLasCarpetas()
    {
        var org = new LibraryOrganization();
        LibraryFolder folder = org.CreateFolder(null);
        org.MoveMedia(["foto"], folder.Id);
        org.SetFavorite("foto", true);
        org.SetFavorite("cancion", true);

        Assert.Equal(["foto", "cancion"], Ids(org, new LibraryQuery(FavoritesOnly: true)));
        Assert.Equal(["cancion"], Ids(org, new LibraryQuery(FavoritesOnly: true, Kind: LibraryKind.Audio)));
    }

    [Fact]
    public void BuscaCombinaTextoTipoFavoritosYEtiqueta()
    {
        var org = new LibraryOrganization();
        org.SetFavorite("toma", true);
        org.SetFavorite("cancion", true);
        org.ToggleTag(["toma", "cancion"], "Viaje", true);

        var query = new LibraryQuery(Text: "MONTAÑA", Kind: LibraryKind.Video, FavoritesOnly: true, Tag: "viaje");

        Assert.Equal(["toma"], Ids(org, query));
    }

    [Fact]
    public void TextoPlegado_QuitaTildesYMayusculas()
    {
        Assert.Equal("arbol nino cafe ss", SearchText.Fold("Árbol NIÑO café ß"));
        Assert.Equal(["un", "dos"], SearchText.Words("  Un\tDOS "));
        Assert.Empty(SearchText.Words("   "));
    }

    [Fact]
    public void MensajeVacio_DicePorQueNoSeVeNada()
    {
        Assert.Equal("Nada coincide con «hola».", LibraryFilter.EmptyMessage(new LibraryQuery(null, " hola "), true));
        Assert.StartsWith("Aún no hay favoritos", LibraryFilter.EmptyMessage(new LibraryQuery(FavoritesOnly: true), true), StringComparison.Ordinal);
        Assert.Equal("No hay medios de este tipo aquí.", LibraryFilter.EmptyMessage(new LibraryQuery(Kind: LibraryKind.Audio), true));
        Assert.StartsWith("Carpeta vacía", LibraryFilter.EmptyMessage(new LibraryQuery("f"), false), StringComparison.Ordinal);
        Assert.Equal("", LibraryFilter.EmptyMessage(new LibraryQuery(), false));
    }

    [Fact]
    public void FichaDeCarpeta_CuentaMediosYSubcarpetas()
    {
        var org = new LibraryOrganization();
        LibraryFolder folder = org.CreateFolder(null);
        org.MoveMedia(["toma", "foto"], folder.Id);

        Assert.Equal(2, LibraryFilter.CountIn(All, org, folder.Id));
        Assert.Equal("2 medios · 1 carpeta", LibraryFilter.FolderSummary(2, 1));
        Assert.Equal("1 medio", LibraryFilter.FolderSummary(1, 0));
        Assert.Equal("3 carpetas", LibraryFilter.FolderSummary(0, 3));
        Assert.Equal("Vacía", LibraryFilter.FolderSummary(0, 0));
    }
}
