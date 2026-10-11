using Rodaje.App.Tests.Support;
using Rodaje.App.Views.Export;
using Rodaje.Core.Model;
using Rodaje.Export;
using Rodaje.Media.Export;

namespace Rodaje.App.Tests.Export;

public class ExportJobListTests
{
    private static ExportRequest Request(string name)
    {
        Project project = ExportTestData.Project();
        return new ExportRequest(project, "sec", Path.Combine(Path.GetTempPath(), name), ExportPresets.YouTube1080p.Settings);
    }

    private static SimulatedExportRunner FastRunner(ExportException? failWith = null) =>
        new() { FramesPerSecond = 3000, Step = TimeSpan.FromMilliseconds(5), FailWith = failWith };

    [Fact]
    public void UnaALaVez_LaSegundaEsperaALaPrimera() => UiThread.Run(async () =>
    {
        SimulatedExportRunner runner = FastRunner();
        using var list = new ExportJobList(runner, SynchronizationContext.Current);
        var finished = new List<string>();
        list.Finished += (_, e) => finished.Add(e.Title);

        ExportEntry first = list.Enqueue(Request("uno.mp4"));
        ExportEntry second = list.Enqueue(Request("dos.mp4"));

        Assert.Same(second, list.Entries[0]);
        Assert.NotNull(first.Job);
        Assert.Null(second.Job);
        Assert.Equal(ExportJobState.Queued, second.Progress.State);
        Assert.Equal(2, list.ActiveCount);

        await WaitAsync(() => finished.Count == 2);

        Assert.Equal(["uno.mp4", "dos.mp4"], finished);
        Assert.Equal(ExportJobState.Completed, first.Progress.State);
        Assert.Equal(1, first.Progress.Fraction);
        Assert.NotNull(first.Thumbnail);
        Assert.Equal(0, list.ActiveCount);

        list.ClearFinished();
        Assert.Empty(list.Entries);
    });

    [Fact]
    public void Cancelar_DejaElTrabajoCancelado() => UiThread.Run(async () =>
    {
        var runner = new SimulatedExportRunner { FramesPerSecond = 30, Step = TimeSpan.FromMilliseconds(20) };
        using var list = new ExportJobList(runner, SynchronizationContext.Current);
        ExportEntry entry = list.Enqueue(Request("largo.mp4"));
        ExportEntry waiting = list.Enqueue(Request("espera.mp4"));

        list.Cancel(waiting);
        Assert.DoesNotContain(waiting, list.Entries);

        await WaitAsync(() => entry.Progress.State == ExportJobState.Encoding);
        list.Cancel(entry);
        await WaitAsync(() => entry.IsFinished);

        Assert.Equal(ExportJobState.Canceled, entry.Progress.State);
        Assert.Null(entry.Failure);
    });

    [Fact]
    public void UnFallo_SeExplicaYSePuedeRepetirSinLoQueFalta() => UiThread.Run(async () =>
    {
        var missing = new ExportException(ExportErrorCode.ContentMissing, "Falta la LUT") { Culprit = "filtros@1.0/calido" };
        SimulatedExportRunner runner = FastRunner(missing);
        using var list = new ExportJobList(runner, SynchronizationContext.Current);
        ExportEntry entry = list.Enqueue(Request("falla.mp4"));

        await WaitAsync(() => entry.IsFinished);

        Assert.Equal(ExportJobState.Failed, entry.Progress.State);
        ExportErrorView view = ExportErrorText.For(entry.Failure!);
        Assert.Equal(ExportErrorAction.ContinueWithout, view.Action);
        Assert.Equal("Exportar sin él", view.ActionText);
        Assert.Contains("calido", view.Message, StringComparison.Ordinal);

        ExportEntry retried = list.Retry(entry, continueWithoutMissing: true);
        await WaitAsync(() => retried.IsFinished);

        Assert.Equal(ExportJobState.Completed, retried.Progress.State);
        Assert.True(runner.Started[^1].ContinueWithoutMissing);
        Assert.Single(list.Entries);
    });

    [Theory]
    [InlineData(ExportErrorCode.DiskFull, ExportErrorAction.ChooseOutput)]
    [InlineData(ExportErrorCode.OutputNotWritable, ExportErrorAction.ChooseOutput)]
    [InlineData(ExportErrorCode.EncoderRejected, ExportErrorAction.ChangeSettings)]
    [InlineData(ExportErrorCode.GpuFailed, ExportErrorAction.Retry)]
    [InlineData(ExportErrorCode.EncoderMissing, ExportErrorAction.None)]
    [InlineData(ExportErrorCode.EffectFailed, ExportErrorAction.ContinueWithout)]
    public void CadaErrorTraeSuAccion(ExportErrorCode code, ExportErrorAction action)
    {
        ExportErrorView view = ExportErrorText.For(new ExportException(code, "x"));

        Assert.Equal(action, view.Action);
        Assert.False(string.IsNullOrWhiteSpace(view.Title));
        Assert.EndsWith(".", view.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TodosLosCodigosTienenTexto()
    {
        foreach (ExportErrorCode code in Enum.GetValues<ExportErrorCode>())
        {
            ExportErrorView view = ExportErrorText.For(new ExportException(code, "x") { Frame = 12 });
            Assert.False(string.IsNullOrWhiteSpace(view.Message));
            Assert.Equal(view.ActionText is null, view.Action == ExportErrorAction.None);
        }
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
