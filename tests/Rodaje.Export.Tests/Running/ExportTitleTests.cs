using System.Diagnostics;
using System.Globalization;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Timeline;
using Rodaje.Export.Encoders;
using Rodaje.Export.Running;
using Rodaje.Media.Export;
using Rodaje.Media.Gpu;
using Rodaje.Media.Proxies;
using Rodaje.Playback.Composition;
using Rodaje.Render;
using Rodaje.Render.Text;
using Rodaje.TestMedia;
using Xunit.Abstractions;
using static Rodaje.Export.Tests.Running.ExportProjects;

namespace Rodaje.Export.Tests.Running;

/// <summary>
/// Títulos en la exportación: el rasterizador sobre el dispositivo de la exportación da el mismo cuadro que la vista
/// previa asentada (sesión, rasterizador y compositor como en el reproductor), y la fuente que falta sigue la política de
/// contenido que falta.
/// </summary>
[Collection(ExportGpuGroup.Name)]
public sealed class ExportTitleTests(ITestOutputHelper output) : IDisposable
{
    private const string MissingFamily = "Rodaje Fuente Que No Existe";

    private static readonly Lazy<Task<IReadOnlyList<EncoderSpec>>> Encoders = new(() =>
        new ProbedEncoderSource(Skip.ProxyExecutable!, ExportEncoderCandidates.All).GetAsync());

    public void Dispose() => ExportEndToEndTests.DeleteFolders();

    [ExportFact]
    public async Task Un_titulo_con_borde_y_sombra_sale_igual_que_en_la_vista_previa()
    {
        Project project = TwoTracks();
        AddTitle(project, Style("Segoe UI"));
        var settings = new ExportSettings
        {
            Video = new VideoExportSettings { Codec = ExportCodec.H264, EncoderId = "h264_nvenc", RateControl = RateControl.ConstantQuality(16), Profile = "high" },
        };

        (ExportJob job, string path) = await RunAsync(project, settings, "titulo.mp4", continueWithout: false);
        Assert.True(job.Progress.State == ExportJobState.Completed, job.Failure?.ToString());

        foreach (long frame in (long[])[15, 100, 200])
        {
            byte[] preview = Preview(project, frame);
            byte[] exported = await DecodeRgbAsync(path, frame, 1920, 1080);
            double psnr = LumaPsnr(preview, exported);
            (int fill, int stroke) = TitlePixels(exported);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"cuadro {frame}: PSNR de luma {psnr:0.0} dB; {fill} px de relleno, {stroke} px de borde"));
            Assert.True(psnr >= 35, $"Cuadro {frame}: PSNR {psnr:0.0} dB contra la vista previa.");
            Assert.True(fill > 5000, $"Cuadro {frame}: el relleno del título no se ve ({fill} px).");
            Assert.True(stroke > 2000, $"Cuadro {frame}: el borde del título no se ve ({stroke} px).");
        }

        string capture = Path.Combine(AppContext.BaseDirectory, "titulo-exportado.png");
        await SaveFrameAsync(path, 100, capture);
        output.WriteLine("Captura: " + capture);
    }

    [ExportFact]
    public async Task La_fuente_que_falta_detiene_la_exportacion_salvo_que_se_acepte_el_respaldo()
    {
        Project project = TitleOnly(Style(MissingFamily));
        var settings = new ExportSettings
        {
            Video = new VideoExportSettings { Codec = ExportCodec.H264, EncoderId = "h264_nvenc", RateControl = RateControl.ConstantQuality(20) },
        };

        (ExportJob failed, string path) = await RunAsync(project, settings, "sin-fuente.mp4", continueWithout: false);
        Assert.Equal(ExportJobState.Failed, failed.Progress.State);
        Assert.Equal(ExportErrorCode.ContentMissing, failed.Failure!.Code);
        Assert.True(failed.Failure.CanContinueWithout);
        Assert.Contains(MissingFamily, failed.Failure.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(path));

        (ExportJob done, path) = await RunAsync(project, settings, "con-respaldo.mp4", continueWithout: true);
        Assert.True(done.Progress.State == ExportJobState.Completed, done.Failure?.ToString());
        (int fill, _) = TitlePixels(await DecodeRgbAsync(path, 10, 1920, 1080));
        Assert.True(fill > 5000, $"Con el respaldo el título tiene que verse ({fill} px).");
    }

    private static TextStyle Style(string family) => new()
    {
        Name = "Prueba",
        Font = new StyleFont { Family = family, Weight = 800, Fallback = ["Segoe UI"] },
        Size = 160,
        Fill = new StyleFill { Color = "#FFE000" },
        Stroke = new StyleStroke { Color = "#0000FF", Width = 8 },
        Shadow = new StyleShadow { Color = "#000000B0", Dx = 8, Dy = 8, Blur = 10 },
    };

    private static void AddTitle(Project project, TextStyle style)
    {
        var track = new Track { Id = "v2", Kind = TrackKind.Video, Name = "Texto" };
        track.Clips.Add(new Clip
        {
            Id = "titulo",
            Start = Time.Zero,
            SourceIn = Time.Zero,
            SourceOut = S(7),
            Generator = new ClipGenerator { Text = "RODAJE 5b", Style = style, Edges = new GeneratorEdges { In = false, Out = false } },
        });
        project.Sequences[0].VideoTracks.Add(track);
    }

    private static Project TitleOnly(TextStyle style)
    {
        var sequence = new Sequence { Id = "sec", Name = "Título", FrameRate = Rational.Fps30, Width = 1920, Height = 1080 };
        var project = new Project { Id = "p", Name = "Título", Sequences = [sequence] };
        AddTitle(project, style);
        sequence.VideoTracks[0].Clips[0].SourceOut = S(1);
        return project;
    }

    /// <summary>El cuadro como lo muestra el reproductor en pausa: sesión, rasterizador y compositor, ya asentado.</summary>
    private static byte[] Preview(Project project, long frame)
    {
        using GpuDevice gpu = GpuDevice.Create();
        using var titles = new TitleRasterizer(gpu);
        using TimelineSession session = TimelineSession.Open(project, "sec", gpu,
            new TimelineSessionOptions { EnableAudio = false, UseProxies = ProxyUsage.Never, Titles = titles });
        using var compositor = new Compositor(gpu, session.Width, session.Height) { Titles = titles };
        Assert.True(compositor.ShadersReady.Wait(TimeSpan.FromSeconds(60)), "Los shaders no compilaron en 60 s.");
        session.Seek(Time.FromFrames(frame, Rational.Fps30));
        var watch = Stopwatch.StartNew();
        while (true)
        {
            using CompositionFrame composed = session.Tick();
            if (!session.IsSeekPending)
            {
                CompositeResult result = compositor.Render(composed, l => new SourceGeometry(l.SampleAspectRatio, l.Rotation));
                if (result.TitlesPending == 0)
                {
                    return compositor.ReadbackRgba();
                }
            }

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), "La vista previa no se asentó en 15 s.");
            Thread.Sleep(5);
        }
    }

    private async Task<(ExportJob Job, string Path)> RunAsync(Project project, ExportSettings settings, string name, bool continueWithout)
    {
        IReadOnlyList<EncoderSpec> encoders = await Encoders.Value;
        string path = Path.Combine(ExportEndToEndTests.Folder(), name);
        var runner = new ExportRunner(new ExportRunnerOptions { Ffmpeg = Skip.ProxyExecutable! });
        ExportJob job = runner.Start(new ExportRequest(project, "sec", path, settings) { ContinueWithoutMissing = continueWithout }, encoders);
        await job.Completion.WaitAsync(TimeSpan.FromMinutes(5));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{name}: {job.Progress.State} en {job.Progress.Elapsed.TotalSeconds:0.00} s; {job.Failure?.Message}"));
        return (job, path);
    }

    /// <summary>
    /// PSNR de la luma (BT.709): el 4:2:0 del H.264 emborrona por fuerza el color de un borde azul junto a un relleno
    /// amarillo, así que se compara la forma del título y no el submuestreo del croma.
    /// </summary>
    private static double LumaPsnr(byte[] rgba, byte[] rgb)
    {
        double sum = 0;
        for (int i = 0, j = 0; j < rgb.Length; i += 4, j += 3)
        {
            double d = Luma(rgba[i], rgba[i + 1], rgba[i + 2]) - Luma(rgb[j], rgb[j + 1], rgb[j + 2]);
            sum += d * d;
        }

        double mse = sum / (rgb.Length / 3);
        return mse == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    private static double Luma(byte r, byte g, byte b) => (0.2126 * r) + (0.7152 * g) + (0.0722 * b);

    /// <summary>Píxeles amarillos (relleno) y azules (borde) en la franja central.</summary>
    private static (int Fill, int Stroke) TitlePixels(byte[] rgb)
    {
        int fill = 0;
        int stroke = 0;
        for (int y = 400; y < 680; y++)
        {
            for (int x = 0; x < 1920; x++)
            {
                int i = ((y * 1920) + x) * 3;
                fill += rgb[i] > 200 && rgb[i + 1] > 180 && rgb[i + 2] < 90 ? 1 : 0;
                stroke += rgb[i] < 80 && rgb[i + 1] < 80 && rgb[i + 2] > 170 ? 1 : 0;
            }
        }

        return (fill, stroke);
    }

    private static async Task<byte[]> DecodeRgbAsync(string path, long frame, int width, int height)
    {
        byte[] data = await RunFfmpegAsync(["-i", path, "-vf",
            $"select=eq(n\\,{frame}),scale=in_color_matrix=bt709:in_range=tv:out_range=pc:flags=bicubic+accurate_rnd+full_chroma_int,format=rgb24",
            "-frames:v", "1", "-f", "rawvideo", "pipe:1"]);
        Assert.True(data.Length == width * height * 3, $"Cuadro {frame}: {data.Length} bytes.");
        return data;
    }

    private static Task<byte[]> SaveFrameAsync(string path, long frame, string png) =>
        RunFfmpegAsync(["-y", "-i", path, "-vf", $"select=eq(n\\,{frame})", "-frames:v", "1", png]);

    private static async Task<byte[]> RunFfmpegAsync(string[] arguments)
    {
        var info = new ProcessStartInfo(Skip.ProxyExecutable!.Path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in (string[])["-hide_banner", "-loglevel", "error", .. arguments])
        {
            info.ArgumentList.Add(a);
        }

        using Process process = Process.Start(info)!;
        Task<string> error = process.StandardError.ReadToEndAsync();
        using var memory = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(memory);
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
        return memory.ToArray();
    }
}
