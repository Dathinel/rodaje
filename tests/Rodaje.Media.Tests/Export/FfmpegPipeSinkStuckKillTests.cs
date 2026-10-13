using System.Diagnostics;
using Rodaje.Media.Export;
using Rodaje.TestMedia;

namespace Rodaje.Media.Tests.Export;

/// <summary>
/// Un ffmpeg.exe que no muere al cancelar: Abort vuelve sin esperar de más, no libera el proceso ni borra lo que
/// tiene abierto, lo anota en el diagnóstico, y la limpieza pendiente lo termina cuando por fin sale.
/// </summary>
/// <remarks>
/// Determinista: <see cref="FfmpegPipeSink.KillOverride"/> no mata en el primer intento (el de Abort) y el segundo (el de
/// la limpieza pendiente) espera a que la prueba lo suelte antes de matar de verdad.
/// </remarks>
public sealed class FfmpegPipeSinkStuckKillTests
{
    /// <summary>
    /// Cota de la CI para los dos Abort: la parada de ffmpeg.exe de la misma familia tardó 1 771 ms con todos los proyectos
    /// de prueba a la vez (CI de Windows de la tarea 107). Aquí se exige que Abort vuelva sin esperar al proceso que no
    /// muere; el segundo queda en la gemela TiempoReal.
    /// </summary>
    private static readonly TimeSpan CiLimit = TimeSpan.FromSeconds(5);

    [ProxyFact]
    public void Abort_leaves_cleanup_pending_while_ffmpeg_lives_and_finishes_it_when_it_exits() => AbortWithStuckFfmpeg(CiLimit);

    [ProxyFact]
    [Trait("Categoria", "TiempoReal")]
    public void Abort_with_stuck_ffmpeg_returns_within_a_second() => AbortWithStuckFfmpeg(TimeSpan.FromSeconds(1));

    private static void AbortWithStuckFfmpeg(TimeSpan limit)
    {
        using var scratch = new ExportScratch();
        var journal = new ExportJournal(scratch.Combine("registro"));
        ExportVideoFormat format = SinkRuns.Format(1280, 720, ExportPixelFormat.Yuv420P);
        var request = new ExportSinkRequest(scratch.Combine("no muere.mkv"), ExportContainer.Mkv, format, SinkEncoders.For("ffv1"),
            ["-level", "3"], null, null, null, [], 60);
        using var release = new ManualResetEventSlim();
        int attempts = 0;
        using var sink = new FfmpegPipeSink(SinkRuns.Ffmpeg, new FfmpegPipeSinkOptions { Journal = journal })
        {
            KillOverride = process =>
            {
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    return false;
                }

                release.Wait();
                process.Kill();
                return process.WaitForExit(TimeSpan.FromSeconds(10));
            },
        };
        sink.Start(request, CancellationToken.None);
        int? processId = sink.ProcessId;
        var source = new SyntheticFrames(format);
        for (long i = 0; i < 6; i++)
        {
            sink.WriteVideo(source.Frame(i), CancellationToken.None);
        }

        var watch = Stopwatch.StartNew();
        sink.Abort();
        sink.Abort();
        watch.Stop();

        Task? cleanup = sink.PendingCleanup;
        Assert.NotNull(cleanup);
        Assert.False(cleanup.IsCompleted);
        Assert.True(watch.Elapsed < limit, $"Abort tardó {watch.ElapsedMilliseconds} ms (cota {limit.TotalMilliseconds:0} ms).");
        Assert.True(SinkRuns.IsAlive(processId), "La prueba necesita a ffmpeg.exe vivo tras el primer intento.");
        Assert.Null(sink.ProcessId);
        Assert.Single(journal.Entries());
        Assert.Contains(sink.Diagnostics, d => d.Contains("no salió", StringComparison.Ordinal));
        Assert.IsAssignableFrom<OperationCanceledException>(
            Record.Exception(() => sink.WriteVideo(source.Frame(6), CancellationToken.None)));

        release.Set();

        Assert.True(cleanup.Wait(TimeSpan.FromSeconds(30)), "La limpieza pendiente no terminó.");
        Assert.False(SinkRuns.IsAlive(processId), $"ffmpeg.exe ({processId}) sigue vivo.");
        Assert.Equal([journal.Directory], scratch.Leftovers());
        Assert.Empty(journal.Entries());
        Assert.Contains(sink.Diagnostics, d => d.Contains("temporales borrados", StringComparison.Ordinal));
    }
}
