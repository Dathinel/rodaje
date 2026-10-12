using Rodaje.Media.Export;
using Rodaje.TestMedia;

namespace Rodaje.Media.Tests.Export;

/// <summary>
/// Cancelar mientras <see cref="FfmpegPipeSink.Complete"/> espera a que ffmpeg.exe cierre el contenedor: siempre
/// <see cref="OperationCanceledException"/>, nunca un error por usar el <c>Process</c> que Abort ya liberó.
/// </summary>
/// <remarks>
/// Deterministas: el gancho <see cref="FfmpegPipeSink.BeforeCompleteWaits"/> para a Complete en el punto exacto, otro
/// hilo cancela y Complete no sigue hasta que ese hilo terminó de abortar (matar, borrar y soltar el proceso).
/// </remarks>
public sealed class FfmpegPipeSinkAbortRaceTests
{
    [ProxyFact]
    public void Canceling_the_token_while_complete_waits_arrives_as_cancellation()
    {
        using var scratch = new ExportScratch();
        var journal = new ExportJournal(scratch.Combine("registro"));
        ExportSinkRequest request = Request(scratch, "cancelada al cerrar.mkv");
        using var cancellation = new CancellationTokenSource();
        using var sink = new FfmpegPipeSink(SinkRuns.Ffmpeg, new FfmpegPipeSinkOptions { Journal = journal });
        sink.Start(request, cancellation.Token);
        int? processId = sink.ProcessId;
        WriteAll(sink, request, cancellation.Token);
        sink.BeforeCompleteWaits = () => OnAnotherThread(cancellation.Cancel);

        Exception? caught = Record.Exception(() => sink.Complete(cancellation.Token));

        OperationCanceledException canceled = Assert.IsAssignableFrom<OperationCanceledException>(caught);
        Assert.Equal(cancellation.Token, canceled.CancellationToken);
        Assert.False(SinkRuns.IsAlive(processId), $"ffmpeg.exe ({processId}) sigue vivo tras cancelar.");
        Assert.Null(sink.ProcessId);
        Assert.Equal([journal.Directory], scratch.Leftovers());
        Assert.Empty(journal.Entries());
    }

    [ProxyFact]
    public void Abort_from_another_thread_while_complete_waits_arrives_as_cancellation()
    {
        using var scratch = new ExportScratch();
        ExportSinkRequest request = Request(scratch, "abortada al cerrar.mkv");
        using var sink = new FfmpegPipeSink(SinkRuns.Ffmpeg);
        sink.Start(request, CancellationToken.None);
        int? processId = sink.ProcessId;
        WriteAll(sink, request, CancellationToken.None);
        sink.BeforeCompleteWaits = () => OnAnotherThread(sink.Abort);

        Exception? caught = Record.Exception(() => sink.Complete(CancellationToken.None));

        Assert.IsAssignableFrom<OperationCanceledException>(caught);
        Assert.False(SinkRuns.IsAlive(processId), $"ffmpeg.exe ({processId}) sigue vivo tras abortar.");
        Assert.Empty(scratch.Leftovers());
    }

    [ProxyFact]
    public void Abort_after_complete_finished_changes_nothing()
    {
        using var scratch = new ExportScratch();
        ExportSinkRequest request = Request(scratch, "terminada.mkv");
        using var sink = new FfmpegPipeSink(SinkRuns.Ffmpeg);
        sink.Start(request, CancellationToken.None);
        WriteAll(sink, request, CancellationToken.None);

        ExportSinkResult result = sink.Complete(CancellationToken.None);
        OnAnotherThread(sink.Abort);

        Assert.True(File.Exists(result.OutputPath));
        Assert.Equal([result.OutputPath], scratch.Leftovers());
    }

    private static ExportSinkRequest Request(ExportScratch scratch, string name)
    {
        ExportVideoFormat format = SinkRuns.Format(320, 180, ExportPixelFormat.Yuv420P);
        return new ExportSinkRequest(scratch.Combine(name), ExportContainer.Mkv, format, SinkEncoders.For("ffv1"), ["-level", "3"],
            null, null, null, [], 30);
    }

    private static void WriteAll(FfmpegPipeSink sink, ExportSinkRequest request, CancellationToken token)
    {
        var source = new SyntheticFrames(request.Video!);
        for (long i = 0; i < request.FrameCount; i++)
        {
            sink.WriteVideo(source.Frame(i), token);
        }
    }

    // Corre en otro hilo y espera a que termine: la cancelación ya pasó entera cuando Complete sigue.
    private static void OnAnotherThread(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => error = Record.Exception(action));
        thread.Start();
        thread.Join();
        Assert.Null(error);
    }
}
