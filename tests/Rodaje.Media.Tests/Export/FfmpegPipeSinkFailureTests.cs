using System.Diagnostics;
using Rodaje.Media.Export;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Media.Tests.Export;

/// <summary>Cancelar, disco lleno, codificadores que fallan y salidas imposibles: sin procesos vivos ni archivos.</summary>
public sealed class FfmpegPipeSinkFailureTests(ITestOutputHelper output)
{
    /// <summary>
    /// Cota de la CI para cada cancelación: la misma parada de ffmpeg.exe tardó 1 771 ms en CpuExportCancellationTests con
    /// todos los proyectos de prueba a la vez (CI de Windows de la tarea 107). Lo que se exige aquí es que pare y no deje
    /// nada; el segundo del criterio de la fase 6 queda en la gemela TiempoReal.
    /// </summary>
    private static readonly TimeSpan CiLimit = TimeSpan.FromSeconds(5);

    [ProxyFact]
    public void Ten_cancellations_leave_no_process_and_no_file() => TenCancellations(CiLimit);

    [ProxyFact]
    [Trait("Categoria", "TiempoReal")]
    public void Ten_cancellations_each_stop_within_a_second() => TenCancellations(TimeSpan.FromSeconds(1));

    private void TenCancellations(TimeSpan limit)
    {
        var random = new Random(6);
        ExportVideoFormat format = SinkRuns.Format(1280, 720, ExportPixelFormat.Yuv420P);
        var source = new SyntheticFrames(format);
        var latencies = new List<double>();
        for (int round = 0; round < 10; round++)
        {
            using var scratch = new ExportScratch();
            string wav = scratch.Tone("mezcla.rodaje-tmp.wav", 20);
            string outputPath = scratch.Combine($"cancelada {round}.mkv");
            var journal = new ExportJournal(scratch.Combine("registro"));
            var request = new ExportSinkRequest(outputPath, ExportContainer.Mkv, format, SinkEncoders.For("ffv1"), ["-level", "3"],
                ExportAudioFormat.Default, wav, SinkEncoders.For("flac"), [], 600);
            using var cancellation = new CancellationTokenSource();
            using var sink = new FfmpegPipeSink(SinkRuns.Ffmpeg, new FfmpegPipeSinkOptions { Journal = journal });
            sink.Start(request, cancellation.Token);
            int? processId = sink.ProcessId;
            long canceledAt = 0;
            var canceler = new Thread(() =>
            {
                Thread.Sleep(random.Next(20, 400));
                canceledAt = Stopwatch.GetTimestamp();
                cancellation.Cancel();
            });
            canceler.Start();

            Exception? caught = Record.Exception(() =>
            {
                for (long i = 0; i < request.FrameCount; i++)
                {
                    sink.WriteVideo(source.Frame(i), cancellation.Token);
                }

                sink.Complete(cancellation.Token);
            });
            long stoppedAt = Stopwatch.GetTimestamp();
            canceler.Join();
            double latency = Stopwatch.GetElapsedTime(canceledAt, Math.Max(stoppedAt, canceledAt)).TotalMilliseconds;
            latencies.Add(latency);

            Assert.IsAssignableFrom<OperationCanceledException>(caught);
            Assert.True(latency <= limit.TotalMilliseconds, $"La cancelación {round} tardó {latency:0} ms (cota {limit.TotalMilliseconds:0} ms).");
            Assert.False(SinkRuns.IsAlive(processId), $"ffmpeg.exe ({processId}) sigue vivo tras cancelar.");
            Assert.Equal([wav, journal.Directory], scratch.Leftovers());
            Assert.Empty(journal.Entries());
        }

        output.WriteLine($"Cancelación: mediana {latencies.Order().ElementAt(5):0} ms, máxima {latencies.Max():0} ms.");
    }

    [ProxyFact]
    public void Abort_from_another_thread_while_ffmpeg_pushes_back()
    {
        using var scratch = new ExportScratch();
        ExportVideoFormat format = SinkRuns.Format(1920, 1080, ExportPixelFormat.Yuv420P);
        var source = new SyntheticFrames(format);
        var request = new ExportSinkRequest(scratch.Combine("abortada.mkv"), ExportContainer.Mkv, format, SinkEncoders.For("ffv1"),
            ["-level", "3", "-threads", "1"], null, null, null, [], 2000);
        using var sink = new FfmpegPipeSink(SinkRuns.Ffmpeg);
        sink.Start(request, CancellationToken.None);
        int? processId = sink.ProcessId;
        var abort = new Thread(() =>
        {
            Thread.Sleep(300);
            sink.Abort();
        });
        abort.Start();

        Exception? caught = Record.Exception(() =>
        {
            for (long i = 0; i < request.FrameCount; i++)
            {
                sink.WriteVideo(source.Frame(i), CancellationToken.None);
            }
        });
        abort.Join();

        Assert.IsAssignableFrom<OperationCanceledException>(caught);
        Assert.False(SinkRuns.IsAlive(processId));
        Assert.Empty(scratch.Leftovers());
    }

    [ProxyFact]
    public void A_full_disk_before_starting_launches_nothing()
    {
        using var scratch = new ExportScratch();
        ExportVideoFormat format = SinkRuns.Format(320, 180, ExportPixelFormat.Yuv420P);
        var request = new ExportSinkRequest(scratch.Combine("sin espacio.mkv"), ExportContainer.Mkv, format, SinkEncoders.For("ffv1"), [],
            null, null, null, [], 10);
        using var sink = new FfmpegPipeSink(SinkRuns.Ffmpeg, new FfmpegPipeSinkOptions
        {
            EstimatedBytes = 500L << 20,
            FreeSpace = _ => 100L << 20,
        });

        ExportException error = Assert.Throws<ExportException>(() => sink.Start(request, CancellationToken.None));

        Assert.Equal(ExportErrorCode.DiskFull, error.Code);
        Assert.Contains("No cabe en el disco", error.Message, StringComparison.Ordinal);
        Assert.Null(sink.ProcessId);
        Assert.Empty(scratch.Leftovers());
    }

    [ProxyFact]
    public void A_disk_that_fills_while_writing_stops_the_export_cleanly()
    {
        using var scratch = new ExportScratch();
        ExportVideoFormat format = SinkRuns.Format(640, 360, ExportPixelFormat.Yuv420P);
        var source = new SyntheticFrames(format);
        int checks = 0;
        var request = new ExportSinkRequest(scratch.Combine("se llena.mkv"), ExportContainer.Mkv, format, SinkEncoders.For("ffv1"), [],
            null, null, null, [], 300);
        using var sink = new FfmpegPipeSink(SinkRuns.Ffmpeg, new FfmpegPipeSinkOptions
        {
            DiskCheckInterval = TimeSpan.Zero,
            FreeSpace = _ => Interlocked.Increment(ref checks) > 20 ? 1L << 20 : 10L << 30,
        });
        sink.Start(request, CancellationToken.None);
        int? processId = sink.ProcessId;

        ExportException error = Assert.Throws<ExportException>(() =>
        {
            for (long i = 0; i < request.FrameCount; i++)
            {
                sink.WriteVideo(source.Frame(i), CancellationToken.None);
            }
        });

        Assert.Equal(ExportErrorCode.DiskFull, error.Code);
        Assert.InRange(sink.FramesWritten, 15, 25);
        Assert.False(SinkRuns.IsAlive(processId));
        Assert.Empty(scratch.Leftovers());
    }

    [ProxyFact]
    public void An_encoder_this_ffmpeg_lacks_is_reported_in_spanish()
    {
        using var scratch = new ExportScratch();
        ExportVideoFormat format = SinkRuns.Format(320, 180, ExportPixelFormat.Yuv420P);
        var request = new ExportSinkRequest(scratch.Combine("sin x264.mp4"), ExportContainer.Mp4, format, SinkEncoders.For("libx264"), [],
            null, null, null, [], 10);
        using var sink = new FfmpegPipeSink(SinkRuns.Ffmpeg);

        ExportException error = Assert.Throws<ExportException>(() => SinkRuns.Run(sink, request, new SyntheticFrames(format)));

        Assert.Equal(ExportErrorCode.EncoderMissing, error.Code);
        Assert.StartsWith("Este ffmpeg.exe no trae el codificador elegido. Codificador: libx264.", error.Message, StringComparison.Ordinal);
        Assert.False(SinkRuns.IsAlive(sink.ProcessId));
        Assert.Empty(scratch.Leftovers());
    }

    [ProxyFact]
    public void A_setting_the_encoder_rejects_is_reported_in_spanish()
    {
        using var scratch = new ExportScratch();
        ExportVideoFormat format = SinkRuns.Format(320, 180, ExportPixelFormat.Yuv420P);
        var request = new ExportSinkRequest(scratch.Combine("perfil malo.mkv"), ExportContainer.Mkv, format, SinkEncoders.For("libsvtav1"),
            ["-profile:v", "inventado"], null, null, null, [], 10);
        using var sink = new FfmpegPipeSink(SinkRuns.Ffmpeg);

        ExportException error = Assert.Throws<ExportException>(() => SinkRuns.Run(sink, request, new SyntheticFrames(format)));

        output.WriteLine(error.Message);
        Assert.Equal(ExportErrorCode.EncoderRejected, error.Code);
        Assert.Contains("libsvtav1", error.Message, StringComparison.Ordinal);
        Assert.Empty(scratch.Leftovers());
    }

    [ProxyFact]
    public void The_output_cannot_overwrite_a_source_or_a_file_in_use()
    {
        using var scratch = new ExportScratch();
        string source = scratch.Combine("original.mp4");
        File.WriteAllBytes(source, [1, 2, 3]);
        ExportVideoFormat format = SinkRuns.Format(320, 180, ExportPixelFormat.Yuv420P);
        var request = new ExportSinkRequest(source, ExportContainer.Mp4, format, SinkEncoders.For("libsvtav1"), [], null, null, null, [], 10);
        using var sink = new FfmpegPipeSink(SinkRuns.Ffmpeg, new FfmpegPipeSinkOptions { SourcePaths = [source.ToUpperInvariant()] });

        ExportException overwrite = Assert.Throws<ExportException>(() => sink.Start(request, CancellationToken.None));
        string busy = scratch.Combine("abierto.mp4");
        using (new FileStream(busy, FileMode.Create, FileAccess.ReadWrite, FileShare.Read))
        {
            ExportException inUse = Assert.Throws<ExportException>(() => sink.Start(request with { OutputPath = busy }, CancellationToken.None));
            Assert.Equal(ExportErrorCode.OutputNotWritable, inUse.Code);
        }

        Assert.Equal(ExportErrorCode.InvalidSettings, overwrite.Code);
        Assert.Contains("medio del proyecto", overwrite.Message, StringComparison.Ordinal);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(source));
        Assert.Null(sink.ProcessId);
    }

    [ProxyFact]
    public void Missing_frames_fail_instead_of_writing_a_short_file()
    {
        using var scratch = new ExportScratch();
        ExportVideoFormat format = SinkRuns.Format(320, 180, ExportPixelFormat.Yuv420P);
        var source = new SyntheticFrames(format);
        var request = new ExportSinkRequest(scratch.Combine("corta.mkv"), ExportContainer.Mkv, format, SinkEncoders.For("ffv1"), [], null, null,
            null, [], 10);
        using var sink = new FfmpegPipeSink(SinkRuns.Ffmpeg);
        sink.Start(request, CancellationToken.None);
        for (int i = 0; i < 7; i++)
        {
            sink.WriteVideo(source.Frame(i), CancellationToken.None);
        }

        Assert.Throws<ArgumentException>(() => sink.WriteVideo(source.Frame(8), CancellationToken.None));
        ExportException error = Assert.Throws<ExportException>(() => sink.Complete(CancellationToken.None));

        Assert.Contains("7 de 10", error.Message, StringComparison.Ordinal);
        Assert.Empty(scratch.Leftovers());
    }

    [Theory]
    [InlineData("[out#0/mp4 @ 0x1] Error writing trailer: No space left on device", ExportErrorCode.DiskFull, "El disco de destino se llenó.")]
    [InlineData("[out#0/mp4] Error opening output D:\\x.mp4: Permission denied", ExportErrorCode.OutputNotWritable, "No hay permiso")]
    [InlineData("[h264_nvenc @ 0x2] OpenEncodeSessionEx failed: out of memory (10)", ExportErrorCode.EncoderRejected, "La GPU no abrió otra sesión")]
    [InlineData("[h264_nvenc @ 0x2] No capable devices found", ExportErrorCode.EncoderRejected, "La GPU no ofrece este codificador")]
    [InlineData("Unrecognized option 'cq'.\nError splitting the argument list: Option not found", ExportErrorCode.EncoderRejected, "Este ffmpeg.exe no conoce una de las opciones")]
    [InlineData("[mp4 @ 0x3] Could not find tag for codec ffv1 in stream #0", ExportErrorCode.EncoderRejected, "El contenedor no admite")]
    [InlineData("av_interleaved_write_frame(): Broken pipe", ExportErrorCode.EncoderFailed, "ffmpeg.exe cerró la tubería")]
    [InlineData("algo que nadie esperaba", ExportErrorCode.EncoderFailed, "ffmpeg.exe terminó con el código 1.")]
    public void Ffmpeg_errors_become_spanish_messages(string stderr, ExportErrorCode code, string start)
    {
        (ExportErrorCode actual, string message) = FfmpegMessages.Translate(stderr, 1, "h264_nvenc");

        Assert.Equal(code, actual);
        Assert.StartsWith(start, message, StringComparison.Ordinal);
        Assert.Contains("(ffmpeg: ", message, StringComparison.Ordinal);
    }
}
