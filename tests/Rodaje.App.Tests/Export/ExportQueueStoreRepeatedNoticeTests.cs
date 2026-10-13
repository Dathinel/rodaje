using System.Text.Json.Nodes;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Export;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Export;

namespace Rodaje.App.Tests.Export;

/// <summary>
/// Con todas las entradas de <c>cola-exportaciones.json</c> rotas no quedaba nada que ofrecer y nadie limpiaba el archivo:
/// el aviso y la copia «.dañado» se repetían en cada arranque. Y la copia se hacía con <c>overwrite: true</c>, así que
/// pisaba una copia anterior con otro contenido.
/// </summary>
public sealed class ExportQueueStoreRepeatedNoticeTests
{
    private static ExportRequest Request(string name)
    {
        Project project = ExportTestData.Project();
        MediaSamples.Video(project.Media[0].Path, 10, 1920, 1080, Rational.Fps30, audio: true).ApplyTo(project.Media[0]);
        return new(project, "sec", Path.Combine(Path.GetTempPath(), name), ExportPresets.YouTube1080p.Settings);
    }

    private static void SaveAndBreak(ExportQueueStore store, int count, params int[] broken)
    {
        store.Save([.. Enumerable.Range(0, count).Select(i => (Request($"salida{i}.mp4"), false))]);
        JsonNode file = JsonNode.Parse(File.ReadAllText(store.Path))!;
        foreach (int i in broken)
        {
            file["exports"]![i]!["projectJson"] = "{ roto";
        }

        File.WriteAllText(store.Path, file.ToJsonString());
    }

    [Fact]
    public void TodasRotas_ElSiguienteArranqueNoVuelveAAvisar()
    {
        using var dir = new TempDirectory();
        var store = new ExportQueueStore(Path.Combine(dir.Path, ExportQueueStore.FileName));
        SaveAndBreak(store, 2, 0, 1);

        Assert.Empty(store.Load());
        Assert.NotNull(store.DamagedNotice);
        string kept = File.ReadAllText(store.DamagedPath!);

        // Nada que ofrecer: la cola se limpia (lo dañado ya está a salvo en la copia) y no se avisa otra vez.
        Assert.False(File.Exists(store.Path));
        Assert.Empty(store.Load());
        Assert.Null(store.DamagedNotice);
        Assert.Equal(kept, File.ReadAllText(store.DamagedCopyPath));
    }

    [Fact]
    public void UnaCopiaAnteriorConOtroContenido_NoSePisa()
    {
        using var dir = new TempDirectory();
        var store = new ExportQueueStore(Path.Combine(dir.Path, ExportQueueStore.FileName));
        File.WriteAllText(store.DamagedCopyPath, "copia de otro día");
        SaveAndBreak(store, 2, 1);

        Assert.Single(store.Load());

        Assert.Equal("copia de otro día", File.ReadAllText(store.DamagedCopyPath));
        Assert.NotNull(store.DamagedPath);
        Assert.NotEqual(store.DamagedCopyPath, store.DamagedPath);
        Assert.Equal(File.ReadAllText(store.Path), File.ReadAllText(store.DamagedPath));
        Assert.Contains(store.DamagedPath!, store.DamagedNotice, StringComparison.Ordinal);
    }

    [Fact]
    public void ElMismoArchivoDosVeces_ReusaLaCopiaSinMultiplicarla()
    {
        using var dir = new TempDirectory();
        var store = new ExportQueueStore(Path.Combine(dir.Path, ExportQueueStore.FileName));
        SaveAndBreak(store, 2, 1);

        // Se cierra la app sin contestar: el archivo sigue igual y el siguiente arranque vuelve a leerlo.
        Assert.Single(store.Load());
        Assert.Single(store.Load());

        Assert.Equal(store.DamagedCopyPath, store.DamagedPath);
        Assert.Single(Directory.GetFiles(dir.Path, "*dañado*"));
    }

    [Fact]
    public void ElArchivoEnteroRoto_NoPisaUnaCopiaAnterior()
    {
        using var dir = new TempDirectory();
        var store = new ExportQueueStore(Path.Combine(dir.Path, ExportQueueStore.FileName));
        File.WriteAllText(store.DamagedCopyPath, "copia de otro día");
        File.WriteAllText(store.Path, "{ no es json");

        Assert.Empty(store.Load());

        Assert.False(File.Exists(store.Path));
        Assert.Equal("copia de otro día", File.ReadAllText(store.DamagedCopyPath));
        Assert.Equal("{ no es json", File.ReadAllText(store.DamagedPath!));
    }
}
