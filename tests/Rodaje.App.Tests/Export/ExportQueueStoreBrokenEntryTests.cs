using System.Text.Json.Nodes;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Export;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Export;

namespace Rodaje.App.Tests.Export;

/// <summary>
/// Una entrada con el proyecto roto en <c>cola-exportaciones.json</c>: apartaba el archivo entero como «.dañado» y se perdían
/// también las exportaciones buenas.
/// </summary>
public sealed class ExportQueueStoreBrokenEntryTests
{
    private static ExportRequest Request(string name)
    {
        Project project = ExportTestData.Project();
        MediaSamples.Video(project.Media[0].Path, 10, 1920, 1080, Rational.Fps30, audio: true).ApplyTo(project.Media[0]);
        return new(project, "sec", Path.Combine(Path.GetTempPath(), name), ExportPresets.YouTube1080p.Settings);
    }

    [Theory]
    [InlineData("{ roto")]
    [InlineData("[]")]
    [InlineData("{\"formatVersion\":\"once\"}")]
    public void UnaEntradaConElProyectoRoto_SeSaltaSolaYSeCuenta(string brokenProject)
    {
        using var dir = new TempDirectory();
        var store = new ExportQueueStore(Path.Combine(dir.Path, ExportQueueStore.FileName));
        store.Save([(Request("primera.mp4"), false), (Request("rota.mp4"), false), (Request("tercera.mp4"), true)]);

        JsonNode file = JsonNode.Parse(File.ReadAllText(store.Path))!;
        file["exports"]![1]!["projectJson"] = brokenProject;
        File.WriteAllText(store.Path, file.ToJsonString());

        IReadOnlyList<(ExportRequest Request, bool ContinueWithoutMissing)> loaded = store.Load();

        Assert.Equal(["primera.mp4", "tercera.mp4"], loaded.Select(l => Path.GetFileName(l.Request.OutputPath)));
        Assert.True(loaded[1].ContinueWithoutMissing);
        Assert.Equal(1, store.Skipped);
        Assert.True(File.Exists(store.Path));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.dañado*"));

        // Otra carga sin entradas rotas pone la cuenta a cero.
        store.Save([.. loaded]);
        Assert.Equal(2, store.Load().Count);
        Assert.Equal(0, store.Skipped);
    }
}
