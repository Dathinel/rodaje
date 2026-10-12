using System.Diagnostics;
using Rodaje.Media.Export;
using Xunit.Abstractions;

namespace Rodaje.Export.Tests.Cpu;

/// <summary>
/// Cancelar una exportación por CPU a mitad (con el token, desde otro hilo) no deja <c>ffmpeg.exe</c> vivo, ni archivo,
/// ni temporales, ni entradas en <c>exportando.json</c>, y para en 1 s como pide el criterio de la fase 6, también en el
/// runner compartido de la CI con la CPU saturada (el sink sube ffmpeg.exe a prioridad alta antes de matarlo).
/// </summary>
public sealed class CpuExportCancellationTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(1);

    [CpuExportTheory]
    [CpuExportCase("libsvtav1", ExportContainer.Mkv, ExportPixelFormat.Yuv420P, "libopus")]
    [CpuExportCase("libvpx-vp9", ExportContainer.WebM, ExportPixelFormat.Yuv420P, "libopus")]
    [CpuExportCase("h264_mf", ExportContainer.Mp4, ExportPixelFormat.Nv12, "aac_mf")]
    [CpuExportCase("prores_ks", ExportContainer.Mov, ExportPixelFormat.Yuv422P10, "pcm_s24le")]
    [CpuExportCase("dnxhd", ExportContainer.Mxf, ExportPixelFormat.Yuv422P10, "pcm_s24le")]
    public void Canceling_mid_export_leaves_no_process_and_no_file(string video, ExportContainer container, ExportPixelFormat pixels, string audio)
    {
        ExportVideoFormat format = CpuExportEnvironment.Format(1280, 720, pixels);
        var source = new LavfiFrames(format, distinct: 8);
        foreach (int delay in new[] { 60, 350 })
        {
            using var scratch = new ExportScratch();
            string wav = CpuExportEnvironment.SineWav(scratch.Combine("mezcla.rodaje-tmp.wav"), 20);
            string outputPath = scratch.Combine($"cancelada {video}.{container.Extension()}");
            var journal = new ExportJournal(scratch.Combine("registro"));
            EncoderArguments arguments = CpuExportArguments.Build(container, video, pixels, audio, 600);
            var request = new ExportSinkRequest(outputPath, container, format, CpuExportArguments.Spec(video), arguments.Video,
                ExportAudioFormat.Default, wav, CpuExportArguments.Spec(audio), arguments.Audio, 600);
            using var cancellation = new CancellationTokenSource();
            using var sink = new FfmpegPipeSink(CpuExportEnvironment.Ffmpeg, new FfmpegPipeSinkOptions { Journal = journal });
            sink.Start(request, cancellation.Token);
            int? processId = SinkRuns.ProcessId(sink);
            Assert.True(SinkRuns.IsAlive(processId));
            Assert.Single(journal.Entries());
            // Para saber dónde se va el tiempo: cuándo muere ffmpeg.exe (ExitTime de Windows) y cuándo vuelve Cancel, que
            // ejecuta Abort del sink en este mismo hilo (matar el árbol, esperar la salida y borrar temporales).
            using Process watched = Process.GetProcessById(processId!.Value);
            // Abrir el handle ya: si no, tras morir ffmpeg.exe y soltarlo el sink, ExitTime no se puede leer (salía «NaN»).
            _ = watched.Handle;
            long canceledAt = 0;
            long cancelReturnedAt = 0;
            DateTime canceledWall = default;
            var canceler = new Thread(() =>
            {
                Thread.Sleep(delay);
                canceledWall = DateTime.Now;
                canceledAt = Stopwatch.GetTimestamp();
                cancellation.Cancel();
                cancelReturnedAt = Stopwatch.GetTimestamp();
            });
            canceler.Start();

            long written = 0;
            Exception? caught = Record.Exception(() =>
            {
                for (long i = 0; i < request.FrameCount; i++)
                {
                    sink.WriteVideo(source.Frame(i), cancellation.Token);
                    written = i + 1;
                }

                sink.Complete(cancellation.Token);
            });
            long stoppedAt = Stopwatch.GetTimestamp();
            canceler.Join();
            TimeSpan latency = Stopwatch.GetElapsedTime(canceledAt, Math.Max(stoppedAt, canceledAt));

            double abortMs = Stopwatch.GetElapsedTime(canceledAt, Math.Max(cancelReturnedAt, canceledAt)).TotalMilliseconds;
            double exitMs = ExitDelay(watched, canceledWall);
            output.WriteLine($"{video} → {container}, cancelada a los {delay} ms tras {written} cuadros: {latency.TotalMilliseconds:0} ms hasta parar " +
                $"(ffmpeg.exe muerto a los {exitMs:0} ms, Abort volvió a los {abortMs:0} ms).");
            Assert.IsAssignableFrom<OperationCanceledException>(caught);
            Assert.True(written < request.FrameCount, "La exportación terminó antes de cancelarla; la prueba no midió nada.");
            Assert.True(latency <= Limit, $"La cancelación tardó {latency.TotalMilliseconds:0} ms.");
            Assert.False(SinkRuns.IsAlive(processId), $"ffmpeg.exe ({processId}) sigue vivo tras cancelar.");
            Assert.Null(SinkRuns.ProcessId(sink));
            Assert.Equal([wav, journal.Directory], scratch.Leftovers());
            Assert.Empty(journal.Entries());
        }
    }

    // Milisegundos entre la cancelación y la muerte de ffmpeg.exe según Windows; NaN si el sistema no da ExitTime.
    private static double ExitDelay(Process process, DateTime canceled)
    {
        try
        {
            return process.WaitForExit(TimeSpan.FromSeconds(10)) ? (process.ExitTime - canceled).TotalMilliseconds : double.NaN;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or System.ComponentModel.Win32Exception)
        {
            return double.NaN;
        }
    }
}
