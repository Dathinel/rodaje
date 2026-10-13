using Rodaje.Media.Export;

namespace Rodaje.Media.Tests.Export;

/// <summary>
/// Tarea 234: el registro de las dos pasadas lo abre ffmpeg.exe con <c>fopen</c>, sin <c>\\?\</c> ni
/// <c>longPathAware</c>, así que <c>-passlogfile</c> nunca lleva una ruta que con los sufijos del codificador pase de
/// MAX_PATH; si la pedida es larga, va a un nombre corto en la carpeta temporal. Ese archivo vive fuera de la carpeta de
/// trabajo de la exportación, así que el sink lo borra en todos los caminos: al terminar, al fallar, al cancelar y,
/// si la segunda pasada no llega a arrancar, al liberarse.
/// </summary>
/// <remarks>
/// Con el ffmpeg falso, que no escribe el registro: la prueba crea los archivos que dejaría ffmpeg.exe («-0.log» y
/// «-0.log.mbtree») donde dice <c>-passlogfile</c>. La ruta pedida no existe ni se crea: solo cuenta su largo.
/// </remarks>
public sealed class FfmpegPipeSinkPassLogTests
{
    private const int MaxPath = 260;

    [Fact]
    public void A_long_pass_log_path_becomes_a_short_one_in_the_temporary_folder()
    {
        string requested = LongPassLogPath(Path.GetTempPath());
        string temporary = Path.Combine(Path.GetTempPath(), "rodaje-pruebas-export");

        string file = FfmpegPipeArguments.PassLogFile(requested, temporary);

        Assert.True(requested.Length > MaxPath, $"La prueba necesita una ruta pedida larga; tiene {requested.Length}.");
        Assert.True(file.Length <= FfmpegPipeArguments.PassLogPathLimit, $"{file.Length} caracteres: {file}");
        Assert.True(file.Length + FfmpegPipeArguments.PassLogSuffixReserve < MaxPath);
        Assert.Equal(Path.GetFullPath(temporary), Path.GetDirectoryName(file));
        Assert.Contains(ExportOutputGuard.TemporaryMarker, file, StringComparison.Ordinal);
        Assert.Equal(file, FfmpegPipeArguments.PassLogFile(requested, temporary));
        Assert.NotEqual(file, FfmpegPipeArguments.PassLogFile(requested + "2", temporary));
    }

    [Fact]
    public void A_short_pass_log_path_is_kept()
    {
        string requested = Path.Combine(Path.GetTempPath(), "rodaje-exportar-1.rodaje-tmp", "pasadas");

        Assert.Equal(Path.GetFullPath(requested), FfmpegPipeArguments.PassLogFile(requested));
    }

    [Fact]
    public void The_passlogfile_argument_never_passes_the_limit()
    {
        ExportVideoFormat format = SinkRuns.Format(320, 180, ExportPixelFormat.Yuv420P);
        foreach (int depth in new[] { 1, 4, 12 })
        {
            string requested = LongPassLogPath(Path.GetTempPath(), depth);
            var request = new ExportSinkRequest(Path.Combine(Path.GetTempPath(), "salida.webm"), ExportContainer.WebM, format,
                SinkEncoders.For("libvpx-vp9"), ["-b:v", "300k"], null, null, null, [], 30)
            { Pass = 1, PassCount = 2, PassLogPath = requested };

            IReadOnlyList<string> args = FfmpegPipeArguments.Build(request, null, null);

            int index = args.ToList().IndexOf("-passlogfile");
            Assert.True(index >= 0, "Falta -passlogfile.");
            string passLog = args[index + 1];
            Assert.True(passLog.Length <= FfmpegPipeArguments.PassLogPathLimit, $"{passLog.Length} caracteres: {passLog}");
            Assert.True(passLog.Length + FfmpegPipeArguments.PassLogSuffixReserve < MaxPath);
            Assert.Equal(FfmpegPipeArguments.PassLogFile(requested), passLog);
        }
    }

    [Fact]
    public void The_kept_log_is_deleted_when_the_second_pass_never_starts()
    {
        using var scratch = new ExportScratch();
        var journal = new ExportJournal(scratch.Combine("registro"));
        ExportSinkRequest request = Request(scratch, "nunca.mkv");
        string passLog = FfmpegPipeArguments.PassLogFile(request.PassLogPath!);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            using (FfmpegPipeSink sink = FakeFfmpeg.Sink(new FfmpegPipeSinkOptions { Journal = journal }))
            {
                RunAnalysis(sink, request, passLog);
                Assert.NotEmpty(LogFiles(passLog));

                Assert.ThrowsAny<OperationCanceledException>(() => sink.Start(request with { Pass = 2 }, canceled.Token));
            }

            Assert.Empty(LogFiles(passLog));
            Assert.Empty(journal.Entries());
        }
        finally
        {
            Cleanup(passLog);
        }
    }

    [Fact]
    public void The_log_is_deleted_when_the_second_pass_finishes()
    {
        using var scratch = new ExportScratch();
        ExportSinkRequest request = Request(scratch, "terminada.mkv");
        string passLog = FfmpegPipeArguments.PassLogFile(request.PassLogPath!);
        try
        {
            using FfmpegPipeSink sink = FakeFfmpeg.Sink();
            RunAnalysis(sink, request, passLog);
            ExportSinkRequest second = request with { Pass = 2 };
            sink.Start(second, CancellationToken.None);
            WriteAll(sink, second);
            ExportSinkResult result = sink.Complete(CancellationToken.None);

            Assert.Equal(request.OutputPath, result.OutputPath);
            Assert.Empty(LogFiles(passLog));
        }
        finally
        {
            Cleanup(passLog);
        }
    }

    [Fact]
    public void The_log_is_deleted_when_the_second_pass_fails()
    {
        using var scratch = new ExportScratch();
        ExportSinkRequest request = Request(scratch, "fallida.mkv", FakeFfmpeg.ExitCode, "1");
        string passLog = FfmpegPipeArguments.PassLogFile(request.PassLogPath!);
        try
        {
            using FfmpegPipeSink sink = FakeFfmpeg.Sink();
            RunAnalysis(sink, request with { VideoArguments = [] }, passLog);
            ExportSinkRequest second = request with { Pass = 2 };
            sink.Start(second, CancellationToken.None);
            WriteAll(sink, second);

            Assert.Throws<ExportException>(() => sink.Complete(CancellationToken.None));
            Assert.Empty(LogFiles(passLog));
        }
        finally
        {
            Cleanup(passLog);
        }
    }

    [Fact]
    public void The_log_is_deleted_when_the_second_pass_is_canceled()
    {
        using var scratch = new ExportScratch();
        ExportSinkRequest request = Request(scratch, "cancelada.mkv");
        string passLog = FfmpegPipeArguments.PassLogFile(request.PassLogPath!);
        try
        {
            using FfmpegPipeSink sink = FakeFfmpeg.Sink();
            RunAnalysis(sink, request, passLog);
            ExportSinkRequest second = request with { Pass = 2 };
            sink.Start(second, CancellationToken.None);
            sink.Abort();

            Assert.Null(sink.PendingCleanup);
            Assert.Empty(LogFiles(passLog));
            Assert.Empty(scratch.Leftovers());
        }
        finally
        {
            Cleanup(passLog);
        }
    }

    // Una ruta pedida de más de MAX_PATH bajo <paramref name="baseDirectory"/>, como la carpeta de trabajo de una
    // exportación con una carpeta temporal larga.
    private static string LongPassLogPath(string baseDirectory, int depth = 4)
    {
        string path = baseDirectory;
        for (int i = 0; i < depth; i++)
        {
            path = Path.Combine(path, $"carpeta temporal muy larga con tildes y eñes número {i:00}");
        }

        return Path.Combine(path, $"rodaje-exportar-{Guid.NewGuid():N}{ExportOutputGuard.TemporaryMarker}", "pasadas");
    }

    private static ExportSinkRequest Request(ExportScratch scratch, string name, params string[] fakeOptions)
    {
        ExportVideoFormat format = SinkRuns.Format(320, 180, ExportPixelFormat.Yuv420P);
        return new ExportSinkRequest(scratch.Combine(name), ExportContainer.Mkv, format, SinkEncoders.For("ffv1"), fakeOptions,
            null, null, null, [], 10)
        { Pass = 1, PassCount = 2, PassLogPath = LongPassLogPath(scratch.Directory) };
    }

    // La pasada de análisis con el falso, que no escribe registro: se crean los archivos que dejaría ffmpeg.exe.
    private static void RunAnalysis(FfmpegPipeSink sink, ExportSinkRequest request, string passLog)
    {
        try
        {
            sink.Start(request, CancellationToken.None);
            Assert.Equal(passLog, sink.Arguments[sink.Arguments.ToList().IndexOf("-passlogfile") + 1]);
            File.WriteAllText(passLog + "-0.log", "registro falso");
            File.WriteAllText(passLog + "-0.log.mbtree", "registro falso");
            WriteAll(sink, request);
            sink.Complete(CancellationToken.None);
        }
        finally
        {
            // Fuera de Windows «NUL» no es un dispositivo: el falso lo crea en su carpeta.
            if (!OperatingSystem.IsWindows())
            {
                File.Delete(Path.Combine(Path.GetDirectoryName(FakeFfmpeg.Path)!, "NUL"));
            }
        }
    }

    private static void WriteAll(FfmpegPipeSink sink, ExportSinkRequest request)
    {
        var source = new SyntheticFrames(request.Video!);
        for (long i = 0; i < request.FrameCount; i++)
        {
            sink.WriteVideo(source.Frame(i), CancellationToken.None);
        }
    }

    private static string[] LogFiles(string passLog) =>
        [.. Directory.EnumerateFiles(Path.GetDirectoryName(passLog)!, Path.GetFileName(passLog) + "*")];

    private static void Cleanup(string passLog)
    {
        foreach (string file in LogFiles(passLog))
        {
            File.Delete(file);
        }
    }
}
