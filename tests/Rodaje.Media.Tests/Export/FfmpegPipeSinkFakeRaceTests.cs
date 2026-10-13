using System.Diagnostics;
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
        bool reached = false;
        bool aliveBeforeCancel = false;
        sink.BeforeCompleteWaits = () =>
        {
            reached = true;
            aliveBeforeCancel = SinkRuns.IsAlive(processId);
            OnAnotherThread(cancellation.Cancel);
        };

        Exception? caught = Record.Exception(() => sink.Complete(cancellation.Token));

        OperationCanceledException canceled = AssertCanceled(caught, sink, reached);
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
        bool reached = false;
        bool aliveBeforeAbort = false;
        sink.BeforeCompleteWaits = () =>
        {
            reached = true;
            aliveBeforeAbort = SinkRuns.IsAlive(processId);
            OnAnotherThread(sink.Abort);
        };

        Exception? caught = Record.Exception(() => sink.Complete(CancellationToken.None));

        AssertCanceled(caught, sink, reached);
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
        using var killWaits = new ManualResetEventSlim();
        int attempts = 0;
        using FfmpegPipeSink sink = FakeFfmpeg.Sink(new FfmpegPipeSinkOptions { Journal = journal });
        sink.KillOverride = process =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                return false;
            }

            killWaits.Set();
            release.Wait();
            process.Kill();
            return process.WaitForExit(TimeSpan.FromSeconds(10));
        };
        sink.Start(request, CancellationToken.None);
        int? processId = sink.ProcessId;
        using Process? fake = Open(processId);
        Task? cleanup = null;
        try
        {
            var source = new SyntheticFrames(request.Video!);
            for (long i = 0; i < 6; i++)
            {
                sink.WriteVideo(source.Frame(i), CancellationToken.None);
            }

            sink.Abort();
            sink.Abort();

            // Sin reloj: Abort ya volvió, y la limpieza de fondo está parada dentro del kill que espera a release, que
            // nadie ha soltado. Si Abort esperara a esa limpieza, la prueba se quedaría aquí colgada.
            cleanup = sink.PendingCleanup;
            Assert.NotNull(cleanup);
            Assert.True(killWaits.Wait(TimeSpan.FromSeconds(30)), "La limpieza pendiente no volvió a intentar matar.");
            Assert.False(release.IsSet);
            Assert.False(cleanup.IsCompleted);
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
        finally
        {
            // Si una aserción falló antes de soltarlo, el kill de la limpieza seguiría en release.Wait() y el falso
            // colgado, vivo: se suelta, se mata y se espera a la limpieza antes de desechar release y la carpeta.
            release.Set();
            KillIfAlive(fake);
            try
            {
                (cleanup ?? sink.PendingCleanup)?.Wait(TimeSpan.FromSeconds(30));
            }
            catch (AggregateException)
            {
                // Lo que falle en la limpieza ya lo dicen las aserciones; aquí no debe tapar el fallo original.
            }
        }
    }

    [Fact]
    public void Hanging_fake_exits_by_itself_when_the_test_process_is_gone()
    {
        string path = FakeFfmpeg.Path;
        int deadParent;
        using (Process gone = StartFake(path, null))
        {
            gone.StandardInput.Close();
            Assert.True(gone.WaitForExit(TimeSpan.FromSeconds(30)), "El ffmpeg falso sin colgar no salió.");
            deadParent = gone.Id;
        }

        using Process hanging = StartFake(path, deadParent, FakeFfmpeg.Hang);
        try
        {
            hanging.StandardInput.Close();

            // El tope de dos minutos no llega a contar: sale porque el «padre» ya no existe.
            Assert.True(hanging.WaitForExit(TimeSpan.FromSeconds(60)), "El ffmpeg falso colgado no salió al faltar el padre.");
            Assert.Equal(FakeFfmpeg.HangExitCode, hanging.ExitCode);
        }
        finally
        {
            KillIfAlive(hanging);
        }
    }

    // En la CI de Windows (fa87f112) Complete salió una vez con ExportException en vez de cancelación, y el registro solo
    // decía el tipo. Una ExportException ahí solo puede salir antes de BeforeCompleteWaits (de Flush o del cierre de la
    // tubería, si el falso ya no lee), así que el mensaje dice si se llegó a abortar, la excepción entera (con el código
    // de salida y la última línea del falso) y el diagnóstico del sink.
    private static OperationCanceledException AssertCanceled(Exception? caught, FfmpegPipeSink sink, bool reachedTheWait)
    {
        if (caught is OperationCanceledException canceled)
        {
            return canceled;
        }

        throw new Xunit.Sdk.XunitException(
            $"Se esperaba una cancelación. ¿Llegó a la espera donde se aborta? {(reachedTheWait ? "sí" : "no")}.{Environment.NewLine}" +
            $"Excepción: {caught?.ToString() ?? "ninguna"}{Environment.NewLine}" +
            $"Diagnóstico del sink: {string.Join(" | ", sink.Diagnostics)}");
    }

    private static Process StartFake(string path, int? parent, params string[] options)
    {
        var info = new ProcessStartInfo(path) { RedirectStandardInput = true, UseShellExecute = false };
        foreach (string option in options)
        {
            info.ArgumentList.Add(option);
        }

        if (parent is { } id)
        {
            info.Environment[FakeFfmpeg.ParentVariable] = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return Process.Start(info) ?? throw new InvalidOperationException("No arrancó el ffmpeg falso.");
    }

    // Se abre el proceso al arrancar, por si su id se reusa antes de que haga falta matarlo.
    private static Process? Open(int? processId)
    {
        if (processId is not { } id)
        {
            return null;
        }

        try
        {
            return Process.GetProcessById(id);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static void KillIfAlive(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(TimeSpan.FromSeconds(10));
            }
        }
        catch (InvalidOperationException)
        {
            // Ya salió.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Está saliendo.
        }
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
