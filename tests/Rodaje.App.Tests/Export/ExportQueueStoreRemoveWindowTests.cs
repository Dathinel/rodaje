using System.Text.Json.Nodes;
using Rodaje.App.Services;
using Rodaje.App.Tests.Crashes;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Export;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Export;

namespace Rodaje.App.Tests.Export;

/// <summary>
/// Tarea 156: al apartar una cola dañada, <c>RemoveOriginalIfUnchanged</c> leía, comparaba y después borraba por nombre;
/// si otra instancia reemplazaba la cola entre la comparación y el borrado, se borraba la cola buena. Ahora compara desde
/// el manejador que borra (compartido solo para lectura). Tarea 184: ese manejador se abría con
/// <see cref="FileOptions.DeleteOnClose"/> y borraba también una cola distinta escrita por otra instancia antes de abrirla,
/// para reescribirla después; si la reescritura fallaba, la cola buena se perdía. Ahora una cola distinta no se toca.
/// </summary>
public sealed class ExportQueueStoreRemoveWindowTests
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

    private static string AllBroken(string path)
    {
        new ExportQueueStore(path).Save([(Request("rota.mp4"), false)]);
        JsonNode file = JsonNode.Parse(File.ReadAllText(path))!;
        file["exports"]![0]!["projectJson"] = "{ roto";
        string text = file.ToJsonString();
        File.WriteAllText(path, text);
        return text;
    }

    // En Linux FileShare solo pone un bloqueo orientativo (flock) que rename() no respeta y el borrado es por nombre: allí
    // la ventana sigue abierta, pero Rodaje solo corre en Windows.
    [WindowsOnlyFact("compartir solo para lectura impide reemplazar el archivo mientras el manejador que lo borra sigue abierto")]
    public void OtraInstanciaReemplazaLaColaJustoAntesDelBorrado_LaColaBuenaSobrevive()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, ExportQueueStore.FileName);
        string good = GoodQueue(Path.Combine(dir.Path, "otra.json"), "buena.mp4");
        string broken = AllBroken(path);
        bool hookRan = false;
        bool blocked = false;
        var store = new ExportQueueStore(path)
        {
            // Ya comparado con lo leído, justo antes de borrarlo: otra instancia guarda su cola (Save escribe en un .tmp y
            // lo mueve encima).
            BeforeRemove = () =>
            {
                hookRan = true;
                try
                {
                    AppDataPaths.WriteAllTextAtomic(path, good);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    blocked = true; // La otra instancia vuelve a guardar en su siguiente cambio.
                }
            },
        };

        Assert.Empty(store.Load());
        if (blocked)
        {
            AppDataPaths.WriteAllTextAtomic(path, good);
        }

        Assert.True(hookRan);
        Assert.True(File.Exists(path));
        Assert.Equal(good, File.ReadAllText(path));
        Assert.Equal(broken, File.ReadAllText(store.DamagedPath!));
        Assert.Equal(["buena.mp4"], new ExportQueueStore(path).Load().Select(l => Path.GetFileName(l.Request.OutputPath)));
    }

    [Fact]
    public void ElOriginalSigueIgual_SeBorraDesdeSuManejadorYNoQuedanTemporales()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, ExportQueueStore.FileName);
        string broken = AllBroken(path);
        int hooks = 0;
        var store = new ExportQueueStore(path) { BeforeRemove = () => hooks++ };

        Assert.Empty(store.Load());

        Assert.Equal(1, hooks);
        Assert.False(File.Exists(path));
        Assert.Equal(broken, File.ReadAllText(store.DamagedPath!));
        Assert.Equal([store.DamagedPath!], Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void ReemplazadaAntesDeAbrirla_SeDejaTalCualYNoQuedanTemporales()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, ExportQueueStore.FileName);
        string good = GoodQueue(Path.Combine(dir.Path, "otra.json"), "buena.mp4");
        File.Delete(Path.Combine(dir.Path, "otra.json"));
        AllBroken(path);
        int hooks = 0;
        var store = new ExportQueueStore(path)
        {
            ReadBytes = p =>
            {
                byte[] read = File.ReadAllBytes(p);
                AppDataPaths.WriteAllTextAtomic(p, good);
                return read;
            },
            BeforeRemove = () => hooks++,
        };

        Assert.Empty(store.Load());

        Assert.Equal(0, hooks);
        Assert.Equal(good, File.ReadAllText(path));
        Assert.Equal(new[] { path, store.DamagedPath! }.Order(StringComparer.Ordinal), Directory.GetFiles(dir.Path).Order(StringComparer.Ordinal));
    }

    // Tarea 184: la cola distinta se borraba al cerrar el manejador (DeleteOnClose) y se reescribía en
    // «.restaurar.tmp»; aquí esa reescritura falla (en su lugar hay una carpeta) y la cola buena se perdía.
    [Fact]
    public void ReemplazadaAntesDeAbrirla_SiReescribirlaFallara_LaColaAjenaSigueIntacta()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, ExportQueueStore.FileName);
        string good = GoodQueue(Path.Combine(dir.Path, "otra.json"), "buena.mp4");
        File.Delete(Path.Combine(dir.Path, "otra.json"));
        string broken = AllBroken(path);
        Directory.CreateDirectory(path + ".restaurar.tmp");
        int hooks = 0;
        var store = new ExportQueueStore(path)
        {
            ReadBytes = p =>
            {
                byte[] read = File.ReadAllBytes(p);
                AppDataPaths.WriteAllTextAtomic(p, good);
                return read;
            },
            BeforeRemove = () => hooks++,
        };

        Assert.Empty(store.Load());

        Assert.Equal(0, hooks);
        Assert.True(File.Exists(path));
        Assert.Equal(good, File.ReadAllText(path));
        Assert.Equal(broken, File.ReadAllText(store.DamagedPath!));
        Assert.Equal(["buena.mp4"], new ExportQueueStore(path).Load().Select(l => Path.GetFileName(l.Request.OutputPath)));
    }
}
