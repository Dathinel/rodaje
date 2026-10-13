using System.Text.Json.Nodes;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Export;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Export;

namespace Rodaje.App.Tests.Export;

/// <summary>
/// Revisión de #133: <c>Load</c> leía el archivo y <c>KeepDamaged</c> lo volvía a leer antes de borrarlo o moverlo, así que
/// una cola buena que otra instancia escribiera entretanto se apartaba; las copias «.dañado-N» no tenían tope; un
/// <see cref="IOException"/> pasajero apartaba como dañada una cola válida; y quitar el borrado del original cuando ya había
/// una copia idéntica no lo detectaba ninguna prueba.
/// </summary>
public sealed class ExportQueueStoreRaceTests
{
    private static ExportRequest Request(string name)
    {
        Project project = ExportTestData.Project();
        MediaSamples.Video(project.Media[0].Path, 10, 1920, 1080, Rational.Fps30, audio: true).ApplyTo(project.Media[0]);
        return new(project, "sec", Path.Combine(Path.GetTempPath(), name), ExportPresets.YouTube1080p.Settings);
    }

    private static string GoodQueue(string path, params string[] names)
    {
        new ExportQueueStore(path).Save([.. names.Select(n => (Request(n), false))]);
        return File.ReadAllText(path);
    }

    private static string AllBroken(string path, int count)
    {
        new ExportQueueStore(path).Save([.. Enumerable.Range(0, count).Select(i => (Request($"salida{i}.mp4"), false))]);
        JsonNode file = JsonNode.Parse(File.ReadAllText(path))!;
        for (int i = 0; i < count; i++)
        {
            file["exports"]![i]!["projectJson"] = "{ roto";
        }

        string text = file.ToJsonString();
        File.WriteAllText(path, text);
        return text;
    }

    [Fact]
    public void TodoRotoYUnaCopiaIdenticaPrevia_ElArchivoDeLaColaDesaparece()
    {
        using var dir = new TempDirectory();
        var store = new ExportQueueStore(Path.Combine(dir.Path, ExportQueueStore.FileName));
        string broken = AllBroken(store.Path, 2);
        File.WriteAllText(store.DamagedCopyPath, broken);

        Assert.Empty(store.Load());

        Assert.Equal(store.DamagedCopyPath, store.DamagedPath);
        Assert.False(File.Exists(store.Path));
        Assert.Single(Directory.GetFiles(dir.Path, "*dañado*"));
    }

    [Fact]
    public void OtraInstanciaEscribeUnaColaBuenaTrasLaLectura_NoSeBorraYLaCopiaGuardaLoLeido()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, ExportQueueStore.FileName);
        string good = GoodQueue(Path.Combine(dir.Path, "otra.json"), "buena.mp4");
        string broken = AllBroken(path, 2);
        var store = new ExportQueueStore(path)
        {
            // Justo después de leer los bytes dañados, otra instancia guarda su cola.
            ReadBytes = p =>
            {
                byte[] read = File.ReadAllBytes(p);
                File.WriteAllText(p, good);
                return read;
            },
        };

        Assert.Empty(store.Load());

        Assert.True(File.Exists(path));
        Assert.Equal(good, File.ReadAllText(path));
        Assert.Equal(broken, File.ReadAllText(store.DamagedPath!));
        Assert.Equal(["buena.mp4"], new ExportQueueStore(path).Load().Select(l => Path.GetFileName(l.Request.OutputPath)));
    }

    [Fact]
    public void ElArchivoEnteroRotoYOtraInstanciaLoReescribe_NoSeApartaLoNuevo()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, ExportQueueStore.FileName);
        string good = GoodQueue(Path.Combine(dir.Path, "otra.json"), "buena.mp4");
        File.WriteAllText(path, "{ no es json");
        var store = new ExportQueueStore(path)
        {
            ReadBytes = p =>
            {
                byte[] read = File.ReadAllBytes(p);
                File.WriteAllText(p, good);
                return read;
            },
        };

        Assert.Empty(store.Load());

        Assert.Equal(good, File.ReadAllText(path));
        Assert.Equal("{ no es json", File.ReadAllText(store.DamagedPath!));
    }

    [Fact]
    public void LasCopiasDanadasTienenTope_YSeReemplazaLaMasVieja()
    {
        using var dir = new TempDirectory();
        var store = new ExportQueueStore(Path.Combine(dir.Path, ExportQueueStore.FileName));
        DateTime start = DateTime.UtcNow.AddHours(-1);
        for (int i = 0; i < ExportQueueStore.MaxDamagedCopies + 3; i++)
        {
            File.WriteAllText(store.Path, $"{{ roto {i}");
            Assert.Empty(store.Load());
            Assert.Equal($"{{ roto {i}", File.ReadAllText(store.DamagedPath!));
            // Fechas crecientes y explícitas: la resolución del reloj del sistema de archivos no decide cuál es la más vieja.
            File.SetLastWriteTimeUtc(store.DamagedPath!, start.AddMinutes(i));
        }

        string[] copies = Directory.GetFiles(dir.Path, "*dañado*");
        Assert.Equal(ExportQueueStore.MaxDamagedCopies, copies.Length);
        // Quedan las cinco más nuevas.
        Assert.Equal(
            Enumerable.Range(3, ExportQueueStore.MaxDamagedCopies).Select(i => $"{{ roto {i}"),
            copies.Select(File.ReadAllText).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void UnFalloDeLecturaPasajero_SeReintentaYNoSeApartaUnaColaValida()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, ExportQueueStore.FileName);
        GoodQueue(path, "primera.mp4", "segunda.mp4");
        int calls = 0;
        var store = new ExportQueueStore(path)
        {
            ReadBytes = p => ++calls == 1 ? throw new IOException("en uso por otro proceso") : File.ReadAllBytes(p),
            Pause = _ => { },
        };

        Assert.Equal(["primera.mp4", "segunda.mp4"], store.Load().Select(l => Path.GetFileName(l.Request.OutputPath)));

        Assert.Equal(2, calls);
        Assert.Null(store.DamagedNotice);
        Assert.Null(store.DamagedPath);
        Assert.Empty(Directory.GetFiles(dir.Path, "*dañado*"));
    }

    [Fact]
    public void UnFalloDeLecturaQueNoPasa_DejaElArchivoDondeEstaYAvisa()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, ExportQueueStore.FileName);
        string good = GoodQueue(path, "primera.mp4");
        var store = new ExportQueueStore(path)
        {
            ReadBytes = _ => throw new IOException("en uso por otro proceso"),
            Pause = _ => { },
        };

        Assert.Empty(store.Load());

        Assert.Equal(good, File.ReadAllText(path));
        Assert.Null(store.DamagedPath);
        Assert.NotNull(store.DamagedNotice);
        Assert.Empty(Directory.GetFiles(dir.Path, "*dañado*"));
        Assert.Single(new ExportQueueStore(path).Load());
    }
}
