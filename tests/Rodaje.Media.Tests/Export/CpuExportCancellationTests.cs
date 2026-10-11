using System.Diagnostics;
using Rodaje.Media.Export;
using Xunit.Abstractions;

namespace Rodaje.Media.Tests.Export;

/// <summary>
/// Cancelar una exportación por CPU a mitad (con el token, desde otro hilo) no deja <c>ffmpeg.exe</c> vivo, ni archivo,
/// ni temporales, ni entradas en <c>exportando.json</c>. El límite de 1 s del criterio de la fase 6 lo mide
/// <see cref="FfmpegPipeSinkFailureTests"/> en el equipo de Daniel; en el runner compartido se da margen y se anota el tiempo.
/// </summary>
public sealed class CpuExportCancellationTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(3);

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
            var request = new ExportSinkRequest(outputPath, container, format, CpuExportArguments.Spec(video),
                [.. CpuExportArguments.Video(video, pixels), .. CpuExportArguments.Container(container)],
                ExportAudioFormat.Default, wav, CpuExportArguments.Spec(audio), CpuExportArguments.Audio(audio), 600);
            using var cancellation = new CancellationTokenSource();
            using var sink = new FfmpegPipeSink(CpuExportEnvironment.Ffmpeg, new FfmpegPipeSinkOptions { Journal = journal });
            sink.Start(request, cancellation.Token);
            int? processId = sink.ProcessId;
            Assert.True(SinkRuns.IsAlive(processId));
            Assert.Single(journal.Entries());
            long canceledAt = 0;
            var canceler = new Thread(() =>
            {
                Thread.Sleep(delay);
                canceledAt = Stopwatch.GetTimestamp();
                cancellation.Cancel();
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

            output.WriteLine($"{video} → {container}, cancelada a los {delay} ms tras {written} cuadros: {latency.TotalMilliseconds:0} ms hasta parar.");
            Assert.IsAssignableFrom<OperationCanceledException>(caught);
            Assert.True(written < request.FrameCount, "La exportación terminó antes de cancelarla; la prueba no midió nada.");
            Assert.True(latency <= Limit, $"La cancelación tardó {latency.TotalMilliseconds:0} ms.");
            Assert.False(SinkRuns.IsAlive(processId), $"ffmpeg.exe ({processId}) sigue vivo tras cancelar.");
            Assert.Null(sink.ProcessId);
            Assert.Equal([wav, journal.Directory], scratch.Leftovers());
            Assert.Empty(journal.Entries());
        }
    }
}
