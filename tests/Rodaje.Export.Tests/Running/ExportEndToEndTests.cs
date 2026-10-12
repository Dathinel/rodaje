using System.Diagnostics;
using System.Globalization;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Offline;
using Rodaje.Export.Encoders;
using Rodaje.Export.Running;
using Rodaje.Media.Export;
using Rodaje.Media.Gpu;
using Rodaje.Media.Proxies;
using Rodaje.Playback.Composition;
using Rodaje.Render;
using Rodaje.TestMedia;
using Xunit.Abstractions;
using static Rodaje.Export.Tests.Running.ExportProjects;

namespace Rodaje.Export.Tests.Running;

/// <summary>
/// La exportación entera en la GPU de verdad: proyecto de dos pistas con efecto, transición, velocidad y reversa a varios
/// formatos, comprobados con ffprobe (cuadros, duración de video y audio, color, <c>moov</c>), cuadros contra la
/// composición de la vista previa (PSNR), tiempos de 1 min 1080p30 en los dos modos y cancelación limpia.
/// </summary>
[Collection(ExportGpuGroup.Name)]
public sealed class ExportEndToEndTests(ITestOutputHelper output) : IDisposable
{
    private static readonly System.Collections.Concurrent.ConcurrentBag<string> Created = [];

    private static readonly Lazy<Task<IReadOnlyList<EncoderSpec>>> Encoders = new(() =>
        new CatalogEncoderSource(Skip.ProxyExecutable!).GetAsync());

    [ExportFact]
    public async Task H264_nvenc_mp4_with_aac_loudness_and_chapters_matches_the_preview()
    {
        Project project = TwoTracks();
        project.Sequences[0].Markers.AddRange([new Marker { Id = "m1", Time = S(2.5), Name = "Rápido" }, new Marker { Id = "m2", Time = S(5), Name = "Reversa" }]);
        var settings = new ExportSettings
        {
            Video = new VideoExportSettings { Codec = ExportCodec.H264, EncoderId = "h264_nvenc", RateControl = RateControl.ConstantQuality(16), Profile = "high" },
            Audio = new AudioExportSettings { Codec = ExportCodec.Aac, Loudness = LoudnessTarget.Platforms },
            ChaptersFromMarkers = true,
        };

        (ExportJob job, string path) = await RunAsync(project, settings, "h264.mp4");

        ExportReport report = job.Report!;
        output.WriteLine($"{report.Video} / {report.Audio}; {report.Bytes / 1024} KB; sonoridad {job.Loudness}");
        Assert.Equal((210L, 1920, 1080), (report.Video!.FrameCount, report.Video.Width, report.Video.Height));
        Assert.Equal(("bt709", "tv", "left"), (report.Video.ColorSpace, report.Video.ColorRange, report.Video.ChromaLocation));
        Assert.True(report.MoovAtStart);
        Assert.Equal(7.0, report.Video.DurationSeconds, 3);
        Assert.InRange(report.Audio!.DurationSeconds, 7.0 - 0.045, 7.0 + 0.045);
        Assert.Equal(3, report.Chapters);
        Assert.InRange(job.Loudness!.Value.Measured.GainTo(-14) - job.Loudness.Value.GainDb, -0.01, 30);

        double worst = await PsnrAgainstPreviewAsync(project, path, [0, 30, 70, 80, 120, 170, 209]);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"PSNR contra la composición de la vista previa: peor {worst:0.0} dB."));
        Assert.True(worst >= 35, $"PSNR {worst:0.0} dB");
    }

    [ExportFact]
    public async Task Hevc_nvenc_ten_bit_keeps_p010_and_main10()
    {
        var settings = new ExportSettings
        {
            Video = new VideoExportSettings { Codec = ExportCodec.Hevc, EncoderId = "hevc_nvenc", BitDepth = 10, Profile = "main10", RateControl = RateControl.ConstantQuality(20) },
            Audio = new AudioExportSettings { Codec = ExportCodec.Aac },
        };

        (ExportJob job, _) = await RunAsync(TwoTracks(), settings, "hevc10.mp4");

        output.WriteLine(job.Report!.Video!.ToString());
        Assert.Equal("hevc", job.Report.Video!.Codec);
        Assert.Contains("10", job.Report.Video.Profile, StringComparison.Ordinal);
        Assert.Contains("10", job.Report.Video.PixelFormat, StringComparison.Ordinal);
    }

    [ExportFact]
    public async Task Prores_422_hq_mov_with_pcm()
    {
        (ExportJob job, _) = await RunAsync(TwoTracks(), ExportPresets.ProResMaster.Settings, "maestro.mov");

        output.WriteLine($"{job.Report!.Video} / {job.Report.Audio}");
        Assert.Equal("prores", job.Report.Video!.Codec);
        Assert.Equal("yuv422p10le", job.Report.Video.PixelFormat);
        Assert.StartsWith("pcm_s24", job.Report.Audio!.Codec, StringComparison.Ordinal);
    }

    [ExportFact]
    public async Task Av1_on_the_cpu_in_mkv_with_opus()
    {
        var settings = new ExportSettings
        {
            Container = ExportContainer.Mkv,
            Video = new VideoExportSettings { Codec = ExportCodec.Av1, RateControl = RateControl.ConstantQuality(40), SpeedPreset = "12" },
            Audio = new AudioExportSettings { Codec = ExportCodec.Opus, RateControl = new RateControl { Mode = RateControlMode.Vbr, BitrateKbps = 96 } },
        };

        (ExportJob job, _) = await RunAsync(TwoTracks(), settings, "av1.mkv");

        output.WriteLine($"{job.Report!.Video} / {job.Report.Audio}; {job.Progress.Elapsed.TotalSeconds:0.0} s");
        Assert.Equal("av1", job.Report.Video!.Codec);
        Assert.Equal("opus", job.Report.Audio!.Codec);
    }

    [ExportFact]
    public async Task Vertical_preset_puts_bars_around_a_horizontal_sequence()
    {
        (ExportJob job, string path) = await RunAsync(TwoTracks(), ExportPresets.Vertical.Settings with { Audio = null }, "vertical.mp4");

        Assert.Equal((1080, 1920), (job.Report!.Video!.Width, job.Report.Video.Height));
        byte[] rgb = await DecodeRgbAsync(path, 100, 1080, 1920);
        // 1920×1080 cabe en 1080×608 centrado: arriba y abajo quedan 656 filas negras.
        Assert.True(Mean(rgb, 1080, 0, 640) < 2, "Barra de arriba");
        Assert.True(Mean(rgb, 1080, 1280, 1920) < 2, "Barra de abajo");
        Assert.True(Mean(rgb, 1080, 700, 1200) > 10, "Imagen");
    }

    [ExportFact]
    [Trait("Categoria", "TiempoReal")]
    public async Task One_minute_of_1080p30_in_both_performance_modes()
    {
        Project project = Long(60);
        var settings = new ExportSettings
        {
            Video = new VideoExportSettings { Codec = ExportCodec.H264, EncoderId = "h264_nvenc", RateControl = RateControl.Vbr(12_000, 18_000) },
        };

        var times = new List<string>();
        foreach (ExportPerformance mode in new[] { ExportPerformance.Fastest, ExportPerformance.Background })
        {
            (ExportJob job, _) = await RunAsync(project, settings with { Performance = mode }, $"minuto-{mode}.mp4");
            double seconds = job.Progress.Elapsed.TotalSeconds;
            times.Add(string.Create(CultureInfo.InvariantCulture, $"{mode}: {seconds:0.0} s ({1800 / seconds:0} fps)"));
            Assert.Equal(1800, job.Report!.Video!.FrameCount);
            Assert.True(seconds < 60, $"{mode}: {seconds:0.0} s para 1 min");
        }

        output.WriteLine("1 min 1080p30, 2 pistas con efectos, NVENC H.264: " + string.Join("; ", times));
    }

    [ExportFact]
    public async Task Canceling_mid_export_leaves_no_file_process_or_temporary()
    {
        IReadOnlyList<EncoderSpec> encoders = await Encoders.Value;
        string folder = Folder();
        string path = Path.Combine(folder, "cancelada.mp4");
        var runner = new ExportRunner(new ExportRunnerOptions { Ffmpeg = Skip.ProxyExecutable!, TemporaryDirectory = folder });
        var settings = new ExportSettings
        {
            Video = new VideoExportSettings { Codec = ExportCodec.H264, EncoderId = "h264_nvenc" },
            Audio = new AudioExportSettings { Codec = ExportCodec.Aac },
        };
        using ExportJob job = runner.Start(new ExportRequest(Long(30), "sec", path, settings), encoders);
        var watch = Stopwatch.StartNew();
        while (job.Progress.State != ExportJobState.Encoding || job.Progress.FramesDone < 60)
        {
            Assert.False(job.Completion.IsCompleted, job.Failure?.ToString());
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60));
            await Task.Delay(10);
        }

        string[] ffmpegBefore = FfmpegProcesses();
        watch.Restart();
        job.Cancel();
        await job.Completion;
        watch.Stop();

        output.WriteLine($"Cancelada en el cuadro {job.Progress.FramesDone} en {watch.Elapsed.TotalMilliseconds:0} ms.");
        Assert.Equal(ExportJobState.Canceled, job.Progress.State);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"{watch.Elapsed.TotalMilliseconds:0} ms");
        Assert.Empty(Directory.GetFileSystemEntries(folder));
        // El ffmpeg.exe de esta exportación estaba entre los de antes y ya no está (otros agentes pueden tener los suyos).
        Assert.True(FfmpegProcesses().Count(ffmpegBefore.Contains) < ffmpegBefore.Length);
    }

    /// <summary>
    /// Un medio cuyo audio dura más que su video (como «parte 6.mp4»: 430 cuadros y 14,35 s de contenedor) entero en la
    /// línea de tiempo: el clip pasa al último cuadro y la exportación lo congela en vez de fallar con «dañado».
    /// </summary>
    [ExportFact]
    public async Task A_clip_whose_audio_outlasts_its_video_exports_holding_the_last_frame()
    {
        Project project = PhoneClips(2);
        MediaRef phone = project.Media[0];
        Time videoEnd = Time.FromFrames(Phone.FrameCount, Rational.Fps30);
        Assert.True(phone.Duration > videoEnd, $"El medio dura {phone.Duration} y su video {videoEnd}: no reproduce el caso.");
        var settings = new ExportSettings
        {
            Video = new VideoExportSettings { Codec = ExportCodec.H264, EncoderId = "h264_nvenc" },
            Audio = new AudioExportSettings { Codec = ExportCodec.Aac },
        };

        (ExportJob job, string path) = await RunAsync(project, settings, "telefono.mp4");

        Sequence sequence = project.Sequences[0];
        long frames = (sequence.Duration - Time.FromTicks(1)).ToFrames(sequence.FrameRate) + 1;
        output.WriteLine($"Medio {phone.Duration.ToSeconds():0.000} s, video {videoEnd.ToSeconds():0.000} s; {job.Report!.Video} / {job.Report.Audio}");
        Assert.Equal(frames, job.Report.Video!.FrameCount);
        Assert.InRange(job.Report.Audio!.DurationSeconds, sequence.Duration.ToSeconds() - 0.045, sequence.Duration.ToSeconds() + 0.045);
        // El cuadro congelado es el último del medio, el mismo que el anterior salvo la pérdida del codificador.
        byte[] held = await DecodeRgbAsync(path, frames - 1, 1080, 1920);
        byte[] last = await DecodeRgbAsync(path, frames - 2, 1080, 1920);
        double difference = held.Zip(last, (x, y) => Math.Abs(x - y)).Average();
        Assert.True(difference < 1.5, $"Diferencia media {difference:0.00}");
    }

    [ExportFact]
    public async Task A_missing_medium_fails_with_its_code_before_writing()
    {
        Project project = TwoTracks();
        project.Media[1].Path = Path.Combine(Folder(), "no-esta.mp4");
        var settings = new ExportSettings { Video = new VideoExportSettings { Codec = ExportCodec.H264 } };

        (ExportJob job, string path) = await RunAsync(project, settings, "falta.mp4", expectSuccess: false);

        Assert.Equal(ExportErrorCode.MediaMissing, job.Failure!.Code);
        Assert.False(File.Exists(path));
    }

    public void Dispose() => DeleteFolders();

    /// <summary>Carpeta nueva con espacio y tilde; se borra al terminar la prueba (las de esta colección van en serie).</summary>
    internal static string Folder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "rodaje-pruebas-export", "extremo ñ", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        Created.Add(folder);
        return folder;
    }

    internal static void DeleteFolders()
    {
        while (Created.TryTake(out string? folder))
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private async Task<(ExportJob Job, string Path)> RunAsync(Project project, ExportSettings settings, string name, bool expectSuccess = true)
    {
        IReadOnlyList<EncoderSpec> encoders = await Encoders.Value;
        string path = Path.Combine(Folder(), name);
        var runner = new ExportRunner(new ExportRunnerOptions { Ffmpeg = Skip.ProxyExecutable! });
        ExportJob job = runner.Start(new ExportRequest(project, "sec", path, settings), encoders);
        await job.Completion.WaitAsync(TimeSpan.FromMinutes(5));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{name}: {job.Progress.State} en {job.Progress.Elapsed.TotalSeconds:0.00} s; {job.Plan?.VideoEncoder?.Id} {job.Plan?.Video?.PixelFormat} {job.Failure?.Message}"));
        if (expectSuccess)
        {
            Assert.True(job.Progress.State == ExportJobState.Completed, job.Failure?.ToString());
            Assert.True(File.Exists(path));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*" + ExportOutputGuard.TemporaryMarker + "*"));
        }

        return (job, path);
    }

    /// <summary>El peor PSNR (R'G'B' 8 bits) de los cuadros exportados contra la composición de la vista previa.</summary>
    private static async Task<double> PsnrAgainstPreviewAsync(Project project, string path, long[] frames)
    {
        using GpuDevice gpu = GpuDevice.Create();
        using OfflineTimeline timeline = OfflineTimeline.Open(project, "sec", gpu);
        using var compositor = new Compositor(gpu, timeline.Width, timeline.Height);
        double worst = double.PositiveInfinity;
        foreach (long f in frames)
        {
            using (CompositionFrame frame = timeline.Render(f, CancellationToken.None))
            {
                compositor.RenderStrict(frame, l => new SourceGeometry(l.SampleAspectRatio, l.Rotation));
            }

            byte[] reference = compositor.ReadbackRgba();
            byte[] decoded = await DecodeRgbAsync(path, f, timeline.Width, timeline.Height);
            double sum = 0;
            for (int i = 0, j = 0; j < decoded.Length; i += 4, j += 3)
            {
                for (int c = 0; c < 3; c++)
                {
                    double d = reference[i + c] - decoded[j + c];
                    sum += d * d;
                }
            }

            double mse = sum / decoded.Length;
            worst = Math.Min(worst, mse == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse));
        }

        return worst;
    }

    private static async Task<byte[]> DecodeRgbAsync(string path, long frame, int width, int height)
    {
        FfmpegExecutable ffmpeg = Skip.ProxyExecutable!;
        var info = new ProcessStartInfo(ffmpeg.Path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in (string[])["-hide_banner", "-loglevel", "error", "-i", path, "-vf",
            $"select=eq(n\\,{frame}),scale=in_color_matrix=bt709:in_range=tv:out_range=pc:flags=bicubic+accurate_rnd+full_chroma_int,format=rgb24",
            "-frames:v", "1", "-f", "rawvideo", "pipe:1"])
        {
            info.ArgumentList.Add(a);
        }

        using Process process = Process.Start(info)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        using var memory = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(memory);
        await process.WaitForExitAsync();
        Assert.True(memory.Length == (long)width * height * 3, $"Cuadro {frame}: {memory.Length} bytes. {await error}");
        return memory.ToArray();
    }

    private static double Mean(byte[] rgb, int width, int fromRow, int toRow)
    {
        long sum = 0;
        for (int i = fromRow * width * 3; i < toRow * width * 3; i++)
        {
            sum += rgb[i];
        }

        return (double)sum / ((toRow - fromRow) * width * 3);
    }

    private static string[] FfmpegProcesses() => [.. Process.GetProcessesByName("ffmpeg").Select(p =>
    {
        using (p)
        {
            return p.Id.ToString(CultureInfo.InvariantCulture);
        }
    })];
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ExportGpuGroup
{
    public const string Name = "Exportación en la GPU";
}

/// <summary>Necesita el <c>ffmpeg.exe</c> LGPL, la GPU con texturas y decodificación por hardware.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ExportFactAttribute : FactAttribute
{
    public ExportFactAttribute() => Skip = Rodaje.TestMedia.Skip.First(Rodaje.TestMedia.Skip.Proxies, Rodaje.TestMedia.Skip.GpuTextures,
        Rodaje.TestMedia.Skip.HardwareDecoding, Rodaje.TestMedia.Skip.Nvenc);
}
