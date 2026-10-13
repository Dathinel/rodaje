using System.Text.Json.Nodes;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Export;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Export;

namespace Rodaje.App.Tests.Export;

/// <summary>
/// Una entrada rota de <c>cola-exportaciones.json</c> se saltaba y se contaba en <c>Skipped</c>, pero nadie lo leía: el
/// siguiente <c>Save</c> reescribía el archivo sin ella y se perdía sin rastro ni aviso (tampoco si estaban todas rotas).
/// </summary>
public sealed class ExportQueueStoreDamagedCopyTests
{
    private static ExportRequest Request(string name)
    {
        Project project = ExportTestData.Project();
        MediaSamples.Video(project.Media[0].Path, 10, 1920, 1080, Rational.Fps30, audio: true).ApplyTo(project.Media[0]);
        return new(project, "sec", Path.Combine(Path.GetTempPath(), name), ExportPresets.YouTube1080p.Settings);
    }

    private static ExportQueueStore SaveAndBreak(TempDirectory dir, int count, params int[] broken)
    {
        var store = new ExportQueueStore(Path.Combine(dir.Path, ExportQueueStore.FileName));
        store.Save([.. Enumerable.Range(0, count).Select(i => (Request($"salida{i}.mp4"), false))]);
        JsonNode file = JsonNode.Parse(File.ReadAllText(store.Path))!;
        foreach (int i in broken)
        {
            file["exports"]![i]!["projectJson"] = "{ roto";
        }

        File.WriteAllText(store.Path, file.ToJsonString());
        return store;
    }

    [Fact]
    public void UnaBuenaYUnaRota_TrasGuardarLaRotaQuedaEnElDanadoYSeAvisa()
    {
        using var dir = new TempDirectory();
        ExportQueueStore store = SaveAndBreak(dir, 2, 1);

        IReadOnlyList<(ExportRequest Request, bool ContinueWithoutMissing)> loaded = store.Load();
        store.Save([.. loaded]);

        Assert.Equal(["salida0.mp4"], loaded.Select(l => Path.GetFileName(l.Request.OutputPath)));
        Assert.Equal(store.DamagedCopyPath, store.DamagedPath);
        Assert.True(File.Exists(store.DamagedCopyPath));
        JsonNode damaged = JsonNode.Parse(File.ReadAllText(store.DamagedCopyPath))!;
        Assert.Equal(2, damaged["exports"]!.AsArray().Count);
        Assert.Equal("{ roto", (string?)damaged["exports"]![1]!["projectJson"]);
        Assert.Equal("Una exportación de la última vez no se pudo leer; el archivo original quedó en «" + store.DamagedCopyPath + "».", store.DamagedNotice);

        // El archivo de la cola ya solo tiene la buena y la siguiente carga no avisa.
        Assert.Single(store.Load());
        Assert.Null(store.DamagedNotice);
        Assert.Null(store.DamagedPath);
    }

    [Fact]
    public void TodasRotas_SeAvisaAunqueNoQuedeNingunaPendiente()
    {
        using var dir = new TempDirectory();
        ExportQueueStore store = SaveAndBreak(dir, 3, 0, 1, 2);

        Assert.Empty(store.Load());

        Assert.Equal(3, store.Skipped);
        Assert.StartsWith("3 exportaciones de la última vez no se pudieron leer", store.DamagedNotice, StringComparison.Ordinal);
        Assert.True(File.Exists(store.DamagedCopyPath));
    }

    [Fact]
    public void ElArchivoEnteroRoto_SeApartaYSeAvisa()
    {
        using var dir = new TempDirectory();
        var store = new ExportQueueStore(Path.Combine(dir.Path, ExportQueueStore.FileName));
        File.WriteAllText(store.Path, "{ no es json");

        Assert.Empty(store.Load());

        Assert.False(File.Exists(store.Path));
        Assert.Equal(store.DamagedCopyPath, store.DamagedPath);
        Assert.True(File.Exists(store.DamagedCopyPath));
        Assert.StartsWith("La cola de exportaciones de la última vez no se pudo leer", store.DamagedNotice, StringComparison.Ordinal);
    }
}
