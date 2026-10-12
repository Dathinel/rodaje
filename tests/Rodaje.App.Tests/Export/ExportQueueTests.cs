using Rodaje.App.Services.Settings;
using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Export;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Export;
using Rodaje.Media.Export;

namespace Rodaje.App.Tests.Export;

public class ExportQueueTests
{
    private static ExportRequest Request(string name, ExportPerformance performance = ExportPerformance.Background)
    {
        // Un proyecto que se puede abrir (el guardado de la cola lo vuelve a leer con sus reglas).
        Project project = ExportTestData.Project();
        MediaSamples.Video(project.Media[0].Path, 10, 1920, 1080, Rational.Fps30, audio: true).ApplyTo(project.Media[0]);
        return new(project, "sec", Path.Combine(Path.GetTempPath(), name), ExportPresets.YouTube1080p.Settings with { Performance = performance });
    }

    private static SimulatedExportRunner SlowRunner() => new() { FramesPerSecond = 30, Step = TimeSpan.FromMilliseconds(20) };

    [Fact]
    public void Pausada_NoEmpiezaNadaHastaReanudar() => UiThread.Run(async () =>
    {
        using var list = new ExportJobList(new SimulatedExportRunner { FramesPerSecond = 3000, Step = TimeSpan.FromMilliseconds(5) },
            SynchronizationContext.Current);
        list.IsPaused = true;
        ExportEntry entry = list.Enqueue(Request("pausa.mp4"));
        await Task.Delay(50);
        Assert.Null(entry.Job);
        Assert.Equal(1, list.QueuePosition(entry));

        list.IsPaused = false;
        Assert.NotNull(entry.Job);
        await WaitAsync(() => entry.IsFinished);
    });

    [Fact]
    public void Reordenar_CambiaCualEmpiezaPrimero() => UiThread.Run(async () =>
    {
        SimulatedExportRunner runner = SlowRunner();
        using var list = new ExportJobList(runner, SynchronizationContext.Current);
        ExportEntry running = list.Enqueue(Request("primera.mp4"));
        ExportEntry second = list.Enqueue(Request("segunda.mp4"));
        ExportEntry third = list.Enqueue(Request("tercera.mp4"));

        Assert.Equal([second, third], list.Waiting);
        Assert.False(list.Move(running, earlier: true));
        Assert.False(list.Move(second, earlier: true));
        Assert.True(list.Move(third, earlier: true));
        Assert.Equal([third, second], list.Waiting);
        Assert.Equal(1, list.QueuePosition(third));

        running.Job!.Cancel();
        await WaitAsync(() => third.Job is not null);
        Assert.Null(second.Job);
        third.Job!.Cancel();
        await WaitAsync(() => second.Job is not null);
        second.Job!.Cancel();
        await WaitAsync(() => list.ActiveCount == 0);
    });

    [Fact]
    public void DosALaVez_SoloConEquipoDeSobraYEnSegundoPlano()
    {
        var big = new ExportConcurrency(16, () => 8L << 30);
        ExportSettings background = ExportPresets.YouTube1080p.Settings;
        ExportSettings fastest = background with { Performance = ExportPerformance.Fastest };

        Assert.True(big.Allows([], fastest));
        Assert.True(big.Allows([background], background));
        Assert.False(big.Allows([background], fastest));
        Assert.False(big.Allows([background, background], background));
        Assert.False(new ExportConcurrency(4, () => 8L << 30).Allows([background], background));
        Assert.False(new ExportConcurrency(16, () => 2L << 30).Allows([background], background));
    }

    [Fact]
    public void ConEquipoDeSobra_LaSegundaArrancaEnseguida() => UiThread.Run(async () =>
    {
        using var list = new ExportJobList(SlowRunner(), SynchronizationContext.Current) { Concurrency = new ExportConcurrency(16, () => 8L << 30) };
        ExportEntry first = list.Enqueue(Request("a.mp4"));
        ExportEntry second = list.Enqueue(Request("b.mp4"));
        ExportEntry third = list.Enqueue(Request("c.mp4"));
        Assert.NotNull(first.Job);
        Assert.NotNull(second.Job);
        Assert.Null(third.Job);
        foreach (ExportEntry entry in list.Entries.ToArray())
        {
            list.Cancel(entry);
        }

        await WaitAsync(() => list.ActiveCount == 0);
    });

    [Fact]
    public void LaColaSeGuardaYSeRecuperaAlVolverAAbrir() => UiThread.Run(async () =>
    {
        using var dir = new TempDirectory();
        var store = new ExportQueueStore(Path.Combine(dir.Path, ExportQueueStore.FileName));
        var list = new ExportJobList(SlowRunner(), SynchronizationContext.Current) { Store = store };
        ExportRequest withRange = Request("rango.mp4") with { Range = new ExportRange(Time.FromSeconds(1), Time.FromSeconds(2)), ProjectDirectory = dir.Path };
        list.Enqueue(Request("en-marcha.mp4"));
        list.Enqueue(withRange, continueWithoutMissing: true);
        Assert.True(File.Exists(store.Path));

        // Cerrar la app: lo que no terminó queda guardado, también la que estaba a medias.
        list.Dispose();
        await Task.Delay(100);
        IReadOnlyList<(ExportRequest Request, bool ContinueWithoutMissing)> loaded = store.Load();
        Assert.Equal(["en-marcha.mp4", "rango.mp4"], loaded.Select(l => Path.GetFileName(l.Request.OutputPath)));
        (ExportRequest request, bool without) = loaded[1];
        Assert.True(without);
        Assert.Equal(withRange.Range, request.Range);
        Assert.Equal(dir.Path, request.ProjectDirectory);
        Assert.Equal(withRange.Settings.Video, request.Settings.Video);
        Assert.Equal(withRange.Settings.Audio, request.Settings.Audio);
        Assert.Equal(withRange.Project.Sequences[0].Duration, request.Project.Sequences[0].Duration);

        store.Clear();
        Assert.Empty(store.Load());
        File.WriteAllText(store.Path, "{ no es json");
        Assert.Empty(store.Load());
    });

    [Fact]
    public void VariasVersiones_CadaPreajusteConSuNombreYSuForma()
    {
        using var dir = new TempDirectory();
        Project project = ExportTestData.Project();
        var model = new ExportDialogModel(new ExportPreferences { Folder = dir.Path }, project.Sequences[0], [], SimulatedEncoders.All, "Viaje", null);
        IReadOnlyList<ExportRequest> requests = model.CreateVersionRequests(
            [ExportPresets.YouTube1080p.Id, ExportPresets.Vertical.Id], project.Clone(), "sec", null);

        Assert.Equal(2, requests.Count);
        Assert.Equal("Viaje - Web 1080p.mp4", Path.GetFileName(requests[0].OutputPath));
        Assert.Equal("Viaje - Vertical 9_16.mp4", Path.GetFileName(requests[1].OutputPath));
        Assert.Equal((1920, 1080), (requests[0].Settings.Video!.Width, requests[0].Settings.Video!.Height));
        Assert.Equal((1080, 1920), (requests[1].Settings.Video!.Width, requests[1].Settings.Video!.Height));
        Assert.Equal(ExportFit.Crop, requests[1].Settings.Video!.Fit);
        Assert.All(requests, r => Assert.NotNull(r.Settings.Video!.EncoderId));
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (int i = 0; i < 1000 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "No llegó al estado esperado en 10 s.");
    }
}
