using System.Diagnostics;
using Rodaje.Media.Export;

namespace Rodaje.Media.Tests.Export;

/// <summary>
/// La limpieza pendiente de un Abort cuyo ffmpeg.exe no salió (tarea 62): es dueña de sus temporales hasta terminar,
/// así que una exportación nueva a la misma salida no arranca mientras tanto; y un Kill que lanza no deja el proceso sin
/// liberar ni sube la excepción por el callback de Cancel().
/// </summary>
/// <remarks>
/// Con el ffmpeg falso, así que corren también en Linux. Deterministas: <see cref="FfmpegPipeSink.KillOverride"/>
/// decide cuándo muere el proceso y <see cref="FakeFfmpeg.Hang"/> lo mantiene vivo hasta entonces.
/// </remarks>
public sealed class FfmpegPipeSinkPendingCleanupTests
{
    // El falso copia en la salida lo que llega por la tubería: los cuadros enteros, sin el relleno del pitch.
    private const long FrameBytes = 320 * 180 * 3 / 2;

    [Fact]
    public async Task Start_to_the_same_output_fails_while_a_canceled_export_is_still_closing()
    {
        using var scratch = new ExportScratch();
        var journal = new ExportJournal(scratch.Combine("registro"));
        ExportSinkRequest stuckRequest = Request(scratch, "misma salida.mkv", FakeFfmpeg.Hang);
        using var release = new ManualResetEventSlim();
        using FfmpegPipeSink stuck = FakeFfmpeg.Sink(new FfmpegPipeSinkOptions { Journal = journal });
        stuck.KillOverride = KillOnlyAfter(release, firstAttempt: _ => false);
        stuck.Start(stuckRequest, CancellationToken.None);
        int? stuckProcess = stuck.ProcessId;
        WriteSome(stuck, stuckRequest, 6);
        stuck.Abort();
        Task? cleanup = stuck.PendingCleanup;
        Assert.NotNull(cleanup);
        Assert.True(SinkRuns.IsAlive(stuckProcess), "La prueba necesita al ffmpeg falso vivo tras el primer intento.");

        ExportSinkRequest again = Request(scratch, "misma salida.mkv");
        using FfmpegPipeSink fresh = FakeFfmpeg.Sink(new FfmpegPipeSinkOptions { Journal = journal });
        ExportException refused = Assert.Throws<ExportException>(() => fresh.Start(again, CancellationToken.None));
        Assert.Equal(ExportErrorCode.OutputNotWritable, refused.Code);
        Assert.Equal(Path.GetFullPath(again.OutputPath), refused.Culprit);
        Assert.Contains("todavía se está cerrando", refused.Message, StringComparison.Ordinal);
        Assert.Null(fresh.ProcessId);
        // El mismo sink tampoco puede reusar la salida.
        Assert.Throws<ExportException>(() => stuck.Start(again, CancellationToken.None));

        // Otra salida en la misma carpeta no choca.
        ExportSinkRequest other = Request(scratch, "otra salida.mkv");
        using (FfmpegPipeSink otherSink = FakeFfmpeg.Sink())
        {
            otherSink.Start(other, CancellationToken.None);
            WriteSome(otherSink, other, other.FrameCount);
            Assert.True(File.Exists(otherSink.Complete(CancellationToken.None).OutputPath));
        }

        Assert.False(cleanup.IsCompleted);
        Assert.Single(journal.Entries());
        release.Set();
        Assert.Same(cleanup, await Task.WhenAny(cleanup, Task.Delay(TimeSpan.FromSeconds(30))));
        await cleanup;
        Assert.False(SinkRuns.IsAlive(stuckProcess), $"El ffmpeg falso ({stuckProcess}) sigue vivo.");

        // Terminada la limpieza vieja, la exportación nueva arranca y nadie le borra los temporales.
        fresh.Start(again, CancellationToken.None);
        WriteSome(fresh, again, again.FrameCount);
        ExportSinkResult result = fresh.Complete(CancellationToken.None);

        Assert.Equal(again.FrameCount * FrameBytes, new FileInfo(result.OutputPath).Length);
        Assert.Equal(
            [.. new[] { journal.Directory, result.OutputPath, Path.GetFullPath(other.OutputPath) }.Order(StringComparer.Ordinal)],
            scratch.Leftovers().Order(StringComparer.Ordinal));
        Assert.Empty(journal.Entries());
    }

    [Fact]
    public void Kill_that_throws_while_canceling_is_noted_and_the_process_is_still_released()
    {
        using var scratch = new ExportScratch();
        var journal = new ExportJournal(scratch.Combine("registro"));
        ExportSinkRequest request = Request(scratch, "kill que lanza.mkv", FakeFfmpeg.Hang);
        using var cancellation = new CancellationTokenSource();
        Process? killed = null;
        using FfmpegPipeSink sink = FakeFfmpeg.Sink(new FfmpegPipeSinkOptions { Journal = journal });
        sink.KillOverride = process =>
        {
            killed = process;
            // Como el Kill de verdad: en BelowNormal y con la CI saturada, el falso tardó más de 10 s en salir y la prueba
            // pasaba por la limpieza pendiente en lugar de por la que tiene que comprobar.
            try
            {
                process.PriorityClass = ProcessPriorityClass.High;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Linux sin permiso para subir la prioridad.
            }

            process.Kill();
            if (!process.WaitForExit(TimeSpan.FromMinutes(1)))
            {
                throw new TimeoutException("El ffmpeg falso no salió en un minuto después de matarlo.");
            }

            throw new NotSupportedException("fallo simulado al matar");
        };
        sink.Start(request, cancellation.Token);
        int? processId = sink.ProcessId;
        var source = new SyntheticFrames(request.Video!);
        for (long i = 0; i < 6; i++)
        {
            sink.WriteVideo(source.Frame(i), cancellation.Token);
        }

        // Antes, la excepción de Kill subía por el callback del token hasta quien cancelaba.
        Assert.Null(Record.Exception(cancellation.Cancel));

        Assert.NotNull(killed);
        Assert.Throws<InvalidOperationException>(() => killed.Id);
        Assert.Null(sink.PendingCleanup);
        Assert.False(SinkRuns.IsAlive(processId), $"El ffmpeg falso ({processId}) sigue vivo.");
        Assert.Contains(sink.Diagnostics, d => d.Contains("NotSupportedException", StringComparison.Ordinal));
        Assert.Equal([journal.Directory], scratch.Leftovers());
        Assert.Empty(journal.Entries());
        Assert.IsAssignableFrom<OperationCanceledException>(
            Record.Exception(() => sink.WriteVideo(source.Frame(6), cancellation.Token)));
    }

    [Fact]
    public async Task Kill_that_throws_with_ffmpeg_alive_leaves_the_cleanup_pending_until_it_exits()
    {
        using var scratch = new ExportScratch();
        var journal = new ExportJournal(scratch.Combine("registro"));
        ExportSinkRequest request = Request(scratch, "kill que lanza vivo.mkv", FakeFfmpeg.Hang);
        using var release = new ManualResetEventSlim();
        Process? stuckProcess = null;
        using FfmpegPipeSink sink = FakeFfmpeg.Sink(new FfmpegPipeSinkOptions { Journal = journal });
        sink.KillOverride = KillOnlyAfter(release, firstAttempt: process =>
        {
            stuckProcess = process;
            throw new NotSupportedException("fallo simulado al matar");
        });
        sink.Start(request, CancellationToken.None);
        int? processId = sink.ProcessId;
        WriteSome(sink, request, 6);

        Assert.Null(Record.Exception(sink.Abort));

        Task? cleanup = sink.PendingCleanup;
        Assert.NotNull(cleanup);
        Assert.False(cleanup.IsCompleted);
        Assert.True(SinkRuns.IsAlive(processId), "La prueba necesita al ffmpeg falso vivo tras el primer intento.");
        Assert.NotNull(stuckProcess);
        // La limpieza pendiente lo tiene prestado: todavía no se liberó.
        Assert.Equal(processId, stuckProcess.Id);
        Assert.Contains(sink.Diagnostics, d => d.Contains("NotSupportedException", StringComparison.Ordinal));
        Assert.Single(journal.Entries());

        release.Set();

        Assert.Same(cleanup, await Task.WhenAny(cleanup, Task.Delay(TimeSpan.FromSeconds(30))));
        await cleanup;
        Assert.Throws<InvalidOperationException>(() => stuckProcess.Id);
        Assert.False(SinkRuns.IsAlive(processId), $"El ffmpeg falso ({processId}) sigue vivo.");
        Assert.Equal([journal.Directory], scratch.Leftovers());
        Assert.Empty(journal.Entries());
    }

    // El primer intento hace lo que diga firstAttempt; los siguientes (la limpieza pendiente) esperan a la prueba y matan.
    private static Func<Process, bool> KillOnlyAfter(ManualResetEventSlim release, Func<Process, bool> firstAttempt)
    {
        int attempts = 0;
        return process =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                return firstAttempt(process);
            }

            release.Wait();
            process.Kill();
            return process.WaitForExit(TimeSpan.FromSeconds(10));
        };
    }

    private static ExportSinkRequest Request(ExportScratch scratch, string name, params string[] fakeOptions)
    {
        ExportVideoFormat format = SinkRuns.Format(320, 180, ExportPixelFormat.Yuv420P);
        return new ExportSinkRequest(scratch.Combine(name), ExportContainer.Mkv, format, SinkEncoders.For("ffv1"), fakeOptions,
            null, null, null, [], 30);
    }

    private static void WriteSome(FfmpegPipeSink sink, ExportSinkRequest request, long count)
    {
        var source = new SyntheticFrames(request.Video!);
        for (long i = 0; i < count; i++)
        {
            sink.WriteVideo(source.Frame(i), CancellationToken.None);
        }
    }
}
