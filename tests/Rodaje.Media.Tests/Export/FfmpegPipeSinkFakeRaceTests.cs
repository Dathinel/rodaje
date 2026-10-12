using Rodaje.Media.Export;

namespace Rodaje.Media.Tests.Export;

/// <summary>
/// Las carreras de la tarea 44 (<see cref="FfmpegPipeSinkAbortRaceTests"/> y <see cref="FfmpegPipeSinkStuckKillTests"/>)
/// con el ffmpeg falso en lugar de ffmpeg.exe, para que corran también donde no hay FFmpeg (la CI y Linux).
/// </summary>
/// <remarks>
/// Deterministas, igual que las originales: <see cref="FfmpegPipeSink.BeforeCompleteWaits"/> para a Complete en el
/// punto exacto y <see cref="FfmpegPipeSink.KillOverride"/> decide cuándo muere el proceso. Con <see cref="FakeFfmpeg.Hang"/>
/// el falso sigue vivo después de que se cierra la tubería, así que la cancelación siempre encuentra un proceso que matar.
/// </remarks>
public sealed class FfmpegPipeSinkFakeRaceTests
{
    [Fact]
    public void Canceling_the_token_while_complete_waits_arrives_as_cancellation()
    {
        using var scratch = new ExportScratch();
        var journal = new ExportJournal(scratch.Combine("registro"));
        ExportSinkRequest request = Request(scratch, "cancelada al cerrar.mkv", FakeFfmpeg.Hang);
        using var cancellation = new CancellationTokenSource();
        using FfmpegPipeSink sink = FakeFfmpeg.Sink(new FfmpegPipeSinkOptions { Journal = journal });
        sink.Start(request, cancellation.Token);
        int? processId = sink.ProcessId;
        WriteAll(sink, request, cancellation.Token);
        bool aliveBeforeCancel = false;
        sink.BeforeCompleteWaits = () =>
        {
            aliveBeforeCancel = SinkRuns.IsAlive(processId);
            OnAnotherThread(cancellation.Cancel);
        };

        Exception? caught = Record.Exception(() => sink.Complete(cancellation.Token));

        OperationCanceledException canceled = Assert.IsAssignableFrom<OperationCanceledException>(caught);
        Assert.Equal(cancellation.Token, canceled.CancellationToken);
        Assert.True(aliveBeforeCancel, "La prueba necesita al ffmpeg falso vivo cuando se cancela.");
        Assert.False(SinkRuns.IsAlive(processId), $"El ffmpeg falso ({processId}) sigue vivo tras cancelar.");
        Assert.Null(sink.ProcessId);
        Assert.Equal([journal.Directory], scratch.Leftovers());
        Assert.Empty(journal.Entries());
    }

    [Fact]
    public void Abort_from_another_thread_while_complete_waits_arrives_as_cancellation()
    {
        using var scratch = new ExportScratch();
        ExportSinkRequest request = Request(scratch, "abortada al cerrar.mkv", FakeFfmpeg.Hang);
        using FfmpegPipeSink sink = FakeFfmpeg.Sink();
        sink.Start(request, CancellationToken.None);
        int? processId = sink.ProcessId;
        WriteAll(sink, request, CancellationToken.None);
        bool aliveBeforeAbort = false;
        sink.BeforeCompleteWaits = () =>
        {
            aliveBeforeAbort = SinkRuns.IsAlive(processId);
            OnAnotherThread(sink.Abort);
        };

        Exception? caught = Record.Exception(() => sink.Complete(CancellationToken.None));

        Assert.IsAssignableFrom<OperationCanceledException>(caught);
        Assert.True(aliveBeforeAbort, "La prueba necesita al ffmpeg falso vivo cuando se aborta.");
        Assert.False(SinkRuns.IsAlive(processId), $"El ffmpeg falso ({processId}) sigue vivo tras abortar.");
        Assert.Null(sink.ProcessId);
        Assert.Empty(scratch.Leftovers());
    }

    [Fact]
    public void Abort_after_complete_finished_changes_nothing()
    {
        using var scratch = new ExportScratch();
        ExportSinkRequest request = Request(scratch, "terminada.mkv");
        using FfmpegPipeSink sink = FakeFfmpeg.Sink();
        sink.Start(request, CancellationToken.None);
        WriteAll(sink, request, CancellationToken.None);

        ExportSinkResult result = sink.Complete(CancellationToken.None);
        OnAnotherThread(sink.Abort);

        // El falso copia en la salida lo que llegó por la tubería: los cuadros enteros, sin el relleno del pitch.
        long frameBytes = 320 * 180 * 3 / 2;
        Assert.Equal(request.FrameCount, result.FramesWritten);
        Assert.Equal(request.FrameCount * frameBytes, result.Bytes);
        Assert.True(File.Exists(result.OutputPath));
        Assert.Equal(result.Bytes, new FileInfo(result.OutputPath).Length);
        Assert.Equal([result.OutputPath], scratch.Leftovers());
    }

    [Fact]
    public void Ffmpeg_exiting_with_an_error_is_a_failure_and_not_a_cancellation()
    {
        using var scratch = new ExportScratch();
        var journal = new ExportJournal(scratch.Combine("registro"));
        ExportSinkRequest request = Request(scratch, "falla al cerrar.mkv", FakeFfmpeg.ExitCode, "3");
        using FfmpegPipeSink sink = FakeFfmpeg.Sink(new FfmpegPipeSinkOptions { Journal = journal });
        sink.Start(request, CancellationToken.None);
        WriteAll(sink, request, CancellationToken.None);

        Exception? caught = Record.Exception(() => sink.Complete(CancellationToken.None));

        Assert.IsType<ExportException>(caught);
        Assert.Null(sink.ProcessId);
        Assert.Equal([journal.Directory], scratch.Leftovers());
        Assert.Empty(journal.Entries());
    }

    [Fact]
    public async Task Abort_leaves_cleanup_pending_while_ffmpeg_lives_and_finishes_it_when_it_exits()
    {
        using var scratch = new ExportScratch();
        var journal = new ExportJournal(scratch.Combine("registro"));
        ExportSinkRequest request = Request(scratch, "no muere.mkv", FakeFfmpeg.Hang);
        using var release = new ManualResetEventSlim();
        int attempts = 0;
        using FfmpegPipeSink sink = FakeFfmpeg.Sink(new FfmpegPipeSinkOptions { Journal = journal });
        sink.KillOverride = process =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                return false;
            }

            release.Wait();
            process.Kill();
            return process.WaitForExit(TimeSpan.FromSeconds(10));
        };
        sink.Start(request, CancellationToken.None);
        int? processId = sink.ProcessId;
        var source = new SyntheticFrames(request.Video!);
        for (long i = 0; i < 6; i++)
        {
            sink.WriteVideo(source.Frame(i), CancellationToken.None);
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        sink.Abort();
        sink.Abort();
        watch.Stop();

        Task? cleanup = sink.PendingCleanup;
        Assert.NotNull(cleanup);
        Assert.False(cleanup.IsCompleted);
        Assert.True(watch.ElapsedMilliseconds < 1000, $"Abort tardó {watch.ElapsedMilliseconds} ms.");
        Assert.True(SinkRuns.IsAlive(processId), "La prueba necesita al ffmpeg falso vivo tras el primer intento.");
        Assert.Null(sink.ProcessId);
        Assert.Single(journal.Entries());
        Assert.Contains(sink.Diagnostics, d => d.Contains("no salió", StringComparison.Ordinal));
        Assert.IsAssignableFrom<OperationCanceledException>(
            Record.Exception(() => sink.WriteVideo(source.Frame(6), CancellationToken.None)));

        release.Set();

        Assert.Same(cleanup, await Task.WhenAny(cleanup, Task.Delay(TimeSpan.FromSeconds(30))));
        await cleanup;
        Assert.False(SinkRuns.IsAlive(processId), $"El ffmpeg falso ({processId}) sigue vivo.");
        Assert.Equal([journal.Directory], scratch.Leftovers());
        Assert.Empty(journal.Entries());
        Assert.Contains(sink.Diagnostics, d => d.Contains("temporales borrados", StringComparison.Ordinal));
    }

    private static ExportSinkRequest Request(ExportScratch scratch, string name, params string[] fakeOptions)
    {
        ExportVideoFormat format = SinkRuns.Format(320, 180, ExportPixelFormat.Yuv420P);
        return new ExportSinkRequest(scratch.Combine(name), ExportContainer.Mkv, format, SinkEncoders.For("ffv1"), fakeOptions,
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
