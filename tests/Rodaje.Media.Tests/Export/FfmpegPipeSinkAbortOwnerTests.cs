using System.Diagnostics;
using Rodaje.Media.Export;

namespace Rodaje.Media.Tests.Export;

/// <summary>
/// Tarea 80: los temporales de un Abort tienen dueño desde que Abort marca la cancelación, no solo cuando la limpieza
/// queda pendiente. Antes, mientras Abort esperaba a que ffmpeg.exe muriera (hasta ~900 ms), un Start a la misma salida
/// no encontraba dueño, arrancaba, y luego el Abort viejo le borraba los temporales (deterministas).
/// </summary>
/// <remarks>
/// Con el ffmpeg falso, así que corre también en Linux. Sin reloj: <see cref="FfmpegPipeSink.KillOverride"/> se queda
/// parado en el primer intento hasta que la prueba lo suelta, así que el Start ocurre siempre dentro del hueco.
/// </remarks>
public sealed class FfmpegPipeSinkAbortOwnerTests
{
    // El falso copia en la salida lo que llega por la tubería: los cuadros enteros, sin el relleno del pitch.
    private const long FrameBytes = 320 * 180 * 3 / 2;

    [Fact]
    public void Start_to_the_same_output_while_abort_is_still_killing_does_not_lose_its_temporaries()
    {
        using var scratch = new ExportScratch();
        var journal = new ExportJournal(scratch.Combine("registro"));
        ExportSinkRequest request = Request(scratch, "abortada en curso.mkv", FakeFfmpeg.Hang);
        using var killEntered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using FfmpegPipeSink aborted = FakeFfmpeg.Sink(new FfmpegPipeSinkOptions { Journal = journal });
        aborted.KillOverride = process =>
        {
            killEntered.Set();
            release.Wait();
            process.Kill();
            return HasExitedWithin(process, TimeSpan.FromSeconds(10));
        };
        aborted.Start(request, CancellationToken.None);
        int? processId = aborted.ProcessId;
        using Process? fake = Open(processId);
        WriteSome(aborted, request, 6);

        Exception? abortError = null;
        var abortThread = new Thread(() => abortError = Record.Exception(aborted.Abort));
        using FfmpegPipeSink fresh = FakeFfmpeg.Sink(new FfmpegPipeSinkOptions { Journal = journal });
        try
        {
            abortThread.Start();
            Assert.True(killEntered.Wait(TimeSpan.FromSeconds(30)), "Abort no llegó a matar.");

            // Abort sigue dentro del primer intento de matar: sus temporales ya son suyos y la salida no se reusa.
            ExportSinkRequest again = Request(scratch, "abortada en curso.mkv");
            ExportException refused = Assert.Throws<ExportException>(() => fresh.Start(again, CancellationToken.None));
            Assert.Equal(ExportErrorCode.OutputNotWritable, refused.Code);
            Assert.Equal(Path.GetFullPath(again.OutputPath), refused.Culprit);
            Assert.Null(fresh.ProcessId);
            Assert.False(release.IsSet);

            release.Set();
            Assert.True(abortThread.Join(TimeSpan.FromSeconds(30)), "Abort no volvió tras soltar el kill.");
            Assert.Null(abortError);
            Assert.Null(aborted.PendingCleanup);
            Assert.False(SinkRuns.IsAlive(processId), $"El ffmpeg falso ({processId}) sigue vivo.");
            Assert.Equal([journal.Directory], scratch.Leftovers());
            Assert.Empty(journal.Entries());

            // Terminado el Abort, la exportación nueva arranca, y nada le borra los temporales por el camino.
            fresh.Start(again, CancellationToken.None);
            WriteSome(fresh, again, again.FrameCount);
            ExportSinkResult result = fresh.Complete(CancellationToken.None);

            Assert.Equal(again.FrameCount * FrameBytes, new FileInfo(result.OutputPath).Length);
            Assert.Equal(
                [.. new[] { journal.Directory, result.OutputPath }.Order(StringComparer.Ordinal)],
                scratch.Leftovers().Order(StringComparer.Ordinal));
            Assert.Empty(journal.Entries());
        }
        finally
        {
            // Si una aserción falló con el kill parado, el falso colgado seguiría vivo y Abort sin volver.
            release.Set();
            KillIfAlive(fake);
            if ((abortThread.ThreadState & System.Threading.ThreadState.Unstarted) == 0)
            {
                abortThread.Join(TimeSpan.FromSeconds(30));
            }
        }
    }

    [Fact]
    public void Start_to_the_same_output_after_abort_killed_in_time_is_not_refused()
    {
        using var scratch = new ExportScratch();
        ExportSinkRequest request = Request(scratch, "abortada a tiempo.mkv", FakeFfmpeg.Hang);
        using FfmpegPipeSink aborted = FakeFfmpeg.Sink();
        aborted.Start(request, CancellationToken.None);
        int? processId = aborted.ProcessId;
        WriteSome(aborted, request, 6);

        aborted.Abort();

        // El Abort que mata a tiempo suelta sus rutas al volver: no deja nada que haga esperar al siguiente Start.
        Assert.Null(aborted.PendingCleanup);
        Assert.False(SinkRuns.IsAlive(processId), $"El ffmpeg falso ({processId}) sigue vivo.");
        ExportSinkRequest again = Request(scratch, "abortada a tiempo.mkv");
        aborted.Start(again, CancellationToken.None);
        WriteSome(aborted, again, again.FrameCount);
        ExportSinkResult result = aborted.Complete(CancellationToken.None);
        Assert.Equal(again.FrameCount * FrameBytes, new FileInfo(result.OutputPath).Length);
        Assert.Equal([result.OutputPath], scratch.Leftovers());
    }

    // Solo el proceso, no sus tuberías: WaitForExit con tope espera además el fin de la salida redirigida, y en Windows
    // otro falso colgado de una prueba en paralelo puede haber heredado esas tuberías y retrasarlo hasta agotar el tope.
    // En Windows tampoco vale HasExited: mira el código de salida (GetExitCodeProcess), que TerminateProcess fija antes de
    // que el sistema cierre los handles del proceso, así que daba «salió» con el temporal todavía abierto sin compartir el
    // borrado; los cinco reintentos de DeleteTemporary (300 ms) no alcanzaban con el runner cargado y el temporal quedaba
    // (CI de Windows, 13-oct, tres veces: «Expected: [registro]; Actual: [temporal, registro]»). El handle del proceso se
    // señala recién cuando ya no tiene nada abierto, que es lo que espera Kill en el sink de verdad.
    private static bool HasExitedWithin(Process process, TimeSpan limit)
    {
        if (OperatingSystem.IsWindows())
        {
            using var exited = new ProcessExitWait(process);
            return exited.WaitOne(limit);
        }

        long start = Stopwatch.GetTimestamp();
        while (!process.HasExited)
        {
            if (Stopwatch.GetElapsedTime(start) > limit)
            {
                return false;
            }

            Thread.Sleep(10);
        }

        return true;
    }

    /// <summary>El handle del proceso como <see cref="WaitHandle"/>, sin adueñarse de él (lo cierra el <see cref="Process"/>).</summary>
    private sealed class ProcessExitWait : WaitHandle
    {
        public ProcessExitWait(Process process) =>
            SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(process.SafeHandle.DangerousGetHandle(), ownsHandle: false);
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

    private static void WriteSome(FfmpegPipeSink sink, ExportSinkRequest request, long count)
    {
        var source = new SyntheticFrames(request.Video!);
        for (long i = 0; i < count; i++)
        {
            sink.WriteVideo(source.Frame(i), CancellationToken.None);
        }
    }
}
