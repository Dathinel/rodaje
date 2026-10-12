using System.Diagnostics;
using System.Globalization;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Timeline;
using Rodaje.Export.Running;
using Rodaje.Export.Still;
using Rodaje.Export.Tests.Running;
using Rodaje.Media.Export;
using Rodaje.Media.Gpu;
using Rodaje.Media.Proxies;
using Rodaje.Playback.Composition;
using Rodaje.Render;
using Rodaje.Render.Text;
using Rodaje.TestMedia;
using Xunit.Abstractions;
using static Rodaje.Export.Tests.Running.ExportProjects;

namespace Rodaje.Export.Tests.Still;

/// <summary>
/// El fotograma exportado contra la vista previa asentada (sesión, rasterizador y compositor como en el reproductor
/// en pausa), con medios, color, PiP y un título encima. Necesita la GPU: la CI de Windows no la tiene y las omite.
/// </summary>
[Collection(ExportGpuGroup.Name)]
public sealed class StillExporterTests(ITestOutputHelper output) : IDisposable
{
    public void Dispose() => ExportEndToEndTests.DeleteFolders();

    [StillGpuFact]
    public async Task El_png_es_el_cuadro_de_la_vista_previa_con_el_titulo()
    {
        Project project = WithTitle(TwoTracks());
        foreach (long frame in (long[])[15, 100])
        {
            string path = Path.Combine(ExportEndToEndTests.Folder(), $"cuadro {frame}.png");
            StillResult result = await ExportAsync(project, frame, path, new StillSettings());
            Assert.Equal((frame, 1920, 1080), (result.Frame, result.Width, result.Height));

            byte[] preview = Preview(project, frame);
            byte[] still = StillEncoderTests.Decode(path, "rgb24");
            double psnr = Psnr(preview, still);
            (int fill, int stroke) = TitlePixels(still);
            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"cuadro {frame}: PSNR {psnr:0.0} dB; {fill} px de relleno, {stroke} de borde"));
            // Sin pérdidas: solo el tramado al bajar de 16 bits flotantes a 8 separa los dos.
            Assert.True(psnr >= 45, $"Cuadro {frame}: PSNR {psnr:0.0} dB contra la vista previa.");
            Assert.True(fill > 5000, $"Cuadro {frame}: el relleno del título no se ve ({fill} px).");
            Assert.True(stroke > 2000, $"Cuadro {frame}: el borde del título no se ve ({stroke} px).");
        }
    }

    [StillGpuFact]
    public async Task El_jpeg_se_parece_a_la_vista_previa()
    {
        Project project = WithTitle(TwoTracks());
        string path = Path.Combine(ExportEndToEndTests.Folder(), "cuadro.jpg");
        await ExportAsync(project, 100, path, new StillSettings { Format = StillFormat.Jpeg, JpegQuality = 95 });

        double psnr = Psnr(Preview(project, 100), StillEncoderTests.Decode(path, "rgb24"));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"JPEG al 95: PSNR {psnr:0.0} dB"));
        Assert.True(psnr >= 35, $"PSNR {psnr:0.0} dB contra la vista previa.");
        Assert.Contains("color_range=pc", StillEncoderTests.Probe(path), StringComparison.Ordinal);
    }

    [StillGpuFact]
    public async Task Con_alfa_el_fondo_sin_capas_es_transparente_y_el_titulo_opaco()
    {
        Project project = WithTitle(TitleOnly());
        string path = Path.Combine(ExportEndToEndTests.Folder(), "alfa.png");
        await ExportAsync(project, 10, path, new StillSettings { Alpha = true });

        byte[] rgba = StillEncoderTests.Decode(path, "rgba");
        Assert.Equal(0, rgba[3]);
        Assert.Equal(0, rgba[^1]);
        int opaque = 0;
        for (int i = 3; i < rgba.Length; i += 4)
        {
            opaque += rgba[i] == 255 ? 1 : 0;
        }

        Assert.True(opaque > 5000, $"El título debe quedar opaco ({opaque} px).");

        // Sin alfa, el mismo cuadro tiene fondo negro como el visor.
        string opaquePath = Path.Combine(ExportEndToEndTests.Folder(), "negro.png");
        await ExportAsync(project, 10, opaquePath, new StillSettings());
        byte[] rgb = StillEncoderTests.Decode(opaquePath, "rgb24");
        Assert.Equal((0, 0, 0), (rgb[0], rgb[1], rgb[2]));
        double psnr = Psnr(Preview(project, 10), rgb);
        Assert.True(psnr >= 45, $"Sin alfa: PSNR {psnr:0.0} dB contra la vista previa.");
    }

    [StillGpuFact]
    public async Task El_preajuste_vertical_da_1080x1920_con_barras()
    {
        Project project = TwoTracks();
        string path = Path.Combine(ExportEndToEndTests.Folder(), "vertical.png");
        StillResult result = await ExportAsync(project, 100, path, StillSettings.FromPreset(ExportPresets.Vertical));
        Assert.Equal((1080, 1920), (result.Width, result.Height));

        byte[] rgb = StillEncoderTests.Decode(path, "rgb24");
        Assert.Equal(1080 * 1920 * 3, rgb.Length);
        // 16:9 en 9:16: 607 filas de imagen en el centro y barras negras arriba y abajo.
        Assert.Equal((0, 0, 0), (rgb[0], rgb[1], rgb[2]));
        int center = ((960 * 1080) + 540) * 3;
        Assert.True(rgb[center] + rgb[center + 1] + rgb[center + 2] > 30, "El centro debe tener imagen.");
    }

    [StillGpuFact]
    public async Task Un_cursor_tras_el_final_exporta_el_ultimo_cuadro()
    {
        Project project = TwoTracks();
        string path = Path.Combine(ExportEndToEndTests.Folder(), "final.png");
        StillResult result = await ExportAsync(project, 100_000, path, new StillSettings());
        Assert.True(result.Frame < 100_000 && result.Frame > 0, $"Cuadro {result.Frame}.");
        Assert.True(File.Exists(path));
    }

    [StillGpuFact]
    public async Task La_fuente_que_falta_detiene_salvo_que_se_acepte_el_respaldo()
    {
        Project project = WithTitle(TitleOnly(), "Rodaje Fuente Que No Existe");
        string path = Path.Combine(ExportEndToEndTests.Folder(), "sin-fuente.png");
        ExportException error = await Assert.ThrowsAsync<ExportException>(() => ExportAsync(project, 10, path, new StillSettings()));
        Assert.Equal(ExportErrorCode.ContentMissing, error.Code);
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFileSystemEntries(Path.GetDirectoryName(path)!));

        await ExportAsync(project, 10, path, new StillSettings(), continueWithout: true);
        Assert.True(File.Exists(path));
    }

    private async Task<StillResult> ExportAsync(Project project, long frame, string path, StillSettings settings, bool continueWithout = false)
    {
        var exporter = new StillExporter(new ExportRunnerOptions { Ffmpeg = Skip.ProxyExecutable! });
        var watch = Stopwatch.StartNew();
        StillResult result = await exporter.ExportAsync(
            new StillRequest(project.Clone(), "sec", Time.FromFrames(frame, Rational.Fps30), path, settings) { ContinueWithoutMissing = continueWithout })
            .WaitAsync(TimeSpan.FromMinutes(2));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileName(path)}: {result.Bytes} B en {watch.Elapsed.TotalMilliseconds:0} ms"));
        return result;
    }

    private static Project TitleOnly()
    {
        var sequence = new Sequence { Id = "sec", Name = "Título", FrameRate = Rational.Fps30, Width = 1920, Height = 1080 };
        return new Project { Id = "p", Name = "Título", Sequences = [sequence] };
    }

    private static Project WithTitle(Project project, string family = "Segoe UI")
    {
        var track = new Track { Id = "texto", Kind = TrackKind.Video, Name = "Texto" };
        track.Clips.Add(new Clip
        {
            Id = "titulo",
            Start = Time.Zero,
            SourceIn = Time.Zero,
            SourceOut = S(6),
            Generator = new ClipGenerator
            {
                Text = "FOTOGRAMA",
                Style = new TextStyle
                {
                    Name = "Prueba",
                    Font = new StyleFont { Family = family, Weight = 800, Fallback = ["Segoe UI"] },
                    Size = 160,
                    Fill = new StyleFill { Color = "#FFE000" },
                    Stroke = new StyleStroke { Color = "#0000FF", Width = 8 },
                    Shadow = new StyleShadow { Color = "#000000B0", Dx = 8, Dy = 8, Blur = 10 },
                },
                Edges = new GeneratorEdges { In = false, Out = false },
            },
        });
        project.Sequences[0].VideoTracks.Add(track);
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

    /// <summary>PSNR de RGB entre la vista previa (RGBA) y la imagen (RGB).</summary>
    private static double Psnr(byte[] rgba, byte[] rgb)
    {
        Assert.Equal(rgba.Length / 4, rgb.Length / 3);
        double sum = 0;
        for (int i = 0, j = 0; j < rgb.Length; i += 4, j += 3)
        {
            for (int c = 0; c < 3; c++)
            {
                double d = rgba[i + c] - rgb[j + c];
                sum += d * d;
            }
        }

        double mse = sum / rgb.Length;
        return mse == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 / mse);
    }

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
}

/// <summary>
/// Necesita el <c>ffmpeg.exe</c> LGPL, la GPU con texturas y la decodificación D3D11VA de los medios de prueba; no NVENC
/// (la imagen no pasa por un codificador de video).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class StillGpuFactAttribute : FactAttribute
{
    // Sin ffmpeg.exe no se mira la GPU: en Linux no hay d3d11 y preguntar lanzaría al descubrir las pruebas.
    public StillGpuFactAttribute() => Skip = Rodaje.TestMedia.Skip.Proxies
        ?? Rodaje.TestMedia.Skip.First(Rodaje.TestMedia.Skip.GpuTextures, Rodaje.TestMedia.Skip.HardwareDecoding);
}
