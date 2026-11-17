using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Media.Probe;
using Rodaje.Export;
using Rodaje.Export.Encoders;
using Rodaje.Export.Running;
using Rodaje.Media.Export;
using Rodaje.Media.Gpu;
using Rodaje.Media.Proxies;
using Rodaje.Render.Gpu;
using Rodaje.Render.Gpu.Vulkan;
using Rodaje.Render.Tests.Gpu;
using Xunit.Abstractions;

namespace Rodaje.Render.Tests.Export;

/// <summary>
/// La exportación por CPU (FFV1 y ProRes) de un proyecto con efecto de color, fundido e imagen en imagen (medios FFV1 de lavfi, que el
/// FFmpeg LGPL de Linux genera sin H.264), con el motor
/// Vulkan (cuadros decodificados en la CPU y subidos por el compositor) frente a la de Windows con D3D11 WARP. En Windows se comparan los dos
/// motores en el mismo proceso; en Linux, con el archivo que deja el trabajo de Windows de la CI (<c>RODAJE_REFERENCIAS_WARP</c>).
/// </summary>
/// <remarks>
/// El mismo hash cuadro a cuadro no se puede pedir entre rasterizadores: el filtro bilineal y el redondeo a 10 bits difieren en las fronteras
/// (con la RTX 3050, el 0,22 % de las muestras sale con uno o dos códigos de diferencia: el color con contraste lo amplifica). Se pide lo que
/// sí es reproducible: los mismos cuadros, en FFV1 (sin pérdidas, lo que entró por la tubería) como mucho 2 códigos de 10 bits en el 1 % de
/// las muestras, y en ProRes, que recomprime, una relación señal/ruido de 60 dB o más. Cuántos cuadros salen idénticos se escribe en la salida.
/// </remarks>
public sealed class ExportHashTests(ITestOutputHelper output)
{
    private const string ReferenceVariable = "RODAJE_REFERENCIAS_WARP";

    [VulkanFact]
    public async Task Vulkan_exports_animated_titles_and_checks_missing_fonts()
    {
        if (Unavailable() is { } reason)
        {
            output.WriteLine(reason);
            return;
        }
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "Rodaje.slnx")))
        {
            root = Directory.GetParent(root)!.FullName;
        }
        string font = Path.Combine(root, "tests", "Rodaje.Text.Tests", "Fuentes", "Anton-Regular.ttf");
        var style = new TextStyle
        {
            Font = new StyleFont { Family = "Anton", Fallback = ["Anton"] }, Size = 180,
            Fill = new StyleFill { Color = "#FFD400" },
            Stroke = new StyleStroke { Color = "#304080", Width = 8 },
            Shadow = new StyleShadow { Color = "#FF0000", Dx = 15, Dy = 15, Blur = 12 },
            In = Rodaje.Core.Text.TextAnimationPresets.Find("subir")!.In(TextUnit.Letter, 0.5),
        };
        var clip = new Clip
        {
            Id = "title", Start = Time.Zero, SourceIn = Time.Zero, SourceOut = Time.FromSeconds(1),
            Generator = new ClipGenerator { Text = "TÍTULO", Style = style, Edges = new GeneratorEdges { In = true, Out = false } },
        };
        var sequence = new Sequence
        {
            Id = "sec", Name = "Títulos", Width = 320, Height = 180, FrameRate = Rational.Fps30,
            VideoTracks = [new Track { Id = "v", Kind = TrackKind.Video, Name = "Títulos", Clips = [clip] }],
        };
        var project = new Project { Id = "p", Name = "Títulos", Sequences = [sequence] };
        Assert.Empty(ModelValidator.Validate(project));
        using var scratch = new Scratch();
        IReadOnlyList<EncoderSpec> encoders = await new CatalogEncoderSource(Ffmpeg).GetAsync();
        var runner = new ExportRunner(new ExportRunnerOptions
        {
            Ffmpeg = Ffmpeg, CreateGpu = () => VulkanGpu.Create(preferSoftware: true), FontFiles = () => [font],
        });
        var settings = new ExportSettings
        {
            Container = ExportContainer.Mkv,
            Video = new VideoExportSettings { Codec = ExportCodec.Ffv1, EncoderId = "ffv1", BitDepth = 10,
                Chroma = ChromaSubsampling.Chroma420, RateControl = RateControl.Lossless },
        };
        async Task<ExportJobState> Export(string name, bool continueWithout)
        {
            string path = scratch.Combine(name + ".mkv");
            using ExportJob job = runner.Start(new ExportRequest(project, "sec", path, settings) { ContinueWithoutMissing = continueWithout }, encoders);
            await job.Completion.WaitAsync(TimeSpan.FromMinutes(2));
            if (name == "missing")
            {
                Assert.Equal(ExportErrorCode.ContentMissing, job.Failure?.Code);
                Assert.Contains("Fuente Que No Existe", job.Failure!.Message, StringComparison.Ordinal);
                Assert.False(File.Exists(path));
            }
            else
            {
                Assert.True(job.Progress.State == ExportJobState.Completed, job.Failure?.ToString());
                Assert.Equal(30, FrameCount(path));
                byte[] rgb = Run("-i", path, "-f", "rawvideo", "-pix_fmt", "rgb24", "-");
                int frameBytes = 320 * 180 * 3;
                int Yellow(int frame) => Enumerable.Range(0, 320 * 180).Count(p =>
                    rgb[frame * frameBytes + p * 3] > 170 && rgb[frame * frameBytes + p * 3 + 1] > 140
                    && rgb[frame * frameBytes + p * 3 + 2] < 90);
                Assert.True(Yellow(25) > 500, "El título debe verse en el vídeo decodificado.");
                Assert.True(Yellow(0) < Yellow(25) / 4, "La entrada por letras debe evaluarse durante la exportación.");
                output.WriteLine($"{name}: 30 cuadros, relleno amarillo al final {Yellow(25)} px.");
            }
            return job.Progress.State;
        }

        Assert.Equal(ExportJobState.Completed, await Export("title", false));

        // La imagen fija sale por el mismo motor: el cuadro 25 con el título entero.
        string png = scratch.Combine("cuadro.png");
        var still = new Rodaje.Export.Still.StillExporter(new ExportRunnerOptions
        {
            Ffmpeg = Ffmpeg, CreateGpu = () => VulkanGpu.Create(preferSoftware: true), FontFiles = () => [font],
        });
        await still.ExportAsync(new Rodaje.Export.Still.StillRequest(project, "sec", Time.FromFrames(25, Rational.Fps30), png,
            new Rodaje.Export.Still.StillSettings())).WaitAsync(TimeSpan.FromMinutes(2));
        byte[] image = Run("-i", png, "-f", "rawvideo", "-pix_fmt", "rgb24", "-");
        int yellow = Enumerable.Range(0, 320 * 180).Count(p => image[p * 3] > 170 && image[p * 3 + 1] > 140 && image[p * 3 + 2] < 90);
        Assert.True(yellow > 500, $"El título debe verse en la imagen fija ({yellow} px).");
        style.Font.Family = "Fuente Que No Existe";
        Assert.Equal(ExportJobState.Failed, await Export("missing", false));
        Assert.Equal(ExportJobState.Completed, await Export("fallback", true));
    }

    public static TheoryData<string> Codecs => ["ffv1", "prores_ks"];

    [VulkanTheory]
    [MemberData(nameof(Codecs))]
    public async Task Vulkan_exports_the_same_frames_as_d3d11_warp(string encoder)
    {
        if (Unavailable() is { } reason)
        {
            output.WriteLine(reason);
            return;
        }

        using var scratch = new Scratch();
        string vulkan = await ExportAsync(encoder, () => VulkanGpu.Create(preferSoftware: true), null, scratch.Combine("vulkan"));
        string? reference = await ReferenceAsync(encoder, scratch);
        if (reference is null)
        {
            output.WriteLine($"Sin la exportación de WARP para {encoder} ({ReferenceVariable}): solo se comprobó que exporta.");
            return;
        }

        string format = PixelFormat(encoder);
        ushort[] actual = Decode(vulkan, format);
        ushort[] expected = Decode(reference, format);
        Assert.Equal(expected.Length, actual.Length);
        int frames = FrameCount(reference);
        int frameSamples = expected.Length / frames;
        int identical = 0, differing = 0, worst = 0;
        double squared = 0;
        for (int f = 0; f < frames; f++)
        {
            ReadOnlySpan<ushort> a = expected.AsSpan(f * frameSamples, frameSamples);
            ReadOnlySpan<ushort> b = actual.AsSpan(f * frameSamples, frameSamples);
            if (a.SequenceEqual(b))
            {
                identical++;
                continue;
            }

            for (int i = 0; i < a.Length; i++)
            {
                int d = Math.Abs(a[i] - b[i]);
                if (d != 0)
                {
                    differing++;
                    worst = Math.Max(worst, d);
                    squared += (double)d * d;
                }
            }
        }

        double fraction = (double)differing / expected.Length;
        double psnr = squared == 0 ? 99 : 10 * Math.Log10(1023.0 * 1023.0 * expected.Length / squared);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{encoder}: {identical} de {frames} cuadros idénticos; {fraction:P3} de las muestras distintas, como mucho {worst} código(s); {psnr:0.0} dB"));
        if (encoder == "ffv1")
        {
            Assert.True(worst <= 2, $"Diferencia de {worst} códigos.");
            Assert.True(fraction <= 0.01, $"El {fraction:P3} de las muestras difiere.");
        }
        else
        {
            Assert.True(psnr >= 60, $"{psnr:0.0} dB");
        }
    }

    /// <summary>Con <c>RODAJE_REFERENCIAS_WARP</c> en Windows, deja la exportación con WARP para el trabajo de Linux.</summary>
    [Fact]
    public async Task Generate_warp_references()
    {
        string? folder = Environment.GetEnvironmentVariable(ReferenceVariable);
        if (folder is null || !OperatingSystem.IsWindowsVersionAtLeast(7) || Unavailable() is not null)
        {
            return;
        }

        Directory.CreateDirectory(folder);
        foreach (MediaRef media in s_project.Value.Media)
        {
            File.Copy(media.Path, Path.Combine(folder, Path.GetFileName(media.Path)), overwrite: true);
        }

        foreach (string encoder in Codecs)
        {
            await ExportAsync(encoder, null, GpuDevice.CreateWarp, Path.Combine(folder, "export-" + encoder));
        }
    }

    private static readonly Lazy<(FfmpegExecutable? Ffmpeg, string? Reason)> s_ffmpeg = new(() =>
        Rodaje.TestMedia.Skip.FfmpegLibraries is { } libraries ? (null, libraries)
        : FfmpegExecutable.TryLocate(out string? failure) is { } ffmpeg ? (ffmpeg, null) : (null, "Omitida: " + failure));

    private static string? Unavailable() => s_ffmpeg.Value.Reason;

    private static FfmpegExecutable Ffmpeg => s_ffmpeg.Value.Ffmpeg!;

    private static readonly Lazy<Project> s_project = new(CreateProject);

    /// <summary>320×180 a 30 cps, 5 s: dos medios de 640×360 (uno con color y un fundido de 0,5 s al otro) y el primero en imagen en imagen.</summary>
    private static Project CreateProject()
    {
        // En Linux, con los medios del trabajo de Windows: así entra lo mismo aunque lavfi o swscale no den los mismos bits en los dos.
        string folder = Environment.GetEnvironmentVariable(ReferenceVariable) is { } shared && !OperatingSystem.IsWindows()
            && File.Exists(Path.Combine(shared, "a.mkv"))
            ? shared
            : Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "rodaje-medios-exportacion")).FullName;
        var sequence = new Sequence { Id = "sec", Name = "Exportar", FrameRate = Rational.Fps30, Width = 320, Height = 180 };
        var project = new Project { Id = "p", Name = "Exportar", Sequences = [sequence] };
        MediaRef a = Media(project, folder, "a", "testsrc2=size=640x360:rate=30:duration=4");
        MediaRef b = Media(project, folder, "b", "smptehdbars=size=640x360:rate=30:duration=4");
        var main = new Track { Id = "v0", Kind = TrackKind.Video, Name = "Video 1" };
        var top = new Track { Id = "v1", Kind = TrackKind.Video, Name = "Video 2" };
        sequence.VideoTracks.AddRange([main, top]);
        Clip color = Add(main, a.Id, "color", Time.Zero, Time.Zero, Time.FromSeconds(2.5));
        color.Effects.Add(Effect(BuiltInEffects.Color, (BuiltInEffects.ColorParameters.Saturation, 0.5), (BuiltInEffects.ColorParameters.Contrast, 0.2)));
        Add(main, b.Id, "segundo", Time.FromSeconds(2.5), Time.FromSeconds(0.5), Time.FromSeconds(2.5));
        main.Transitions.Add(new TransitionInstance { Id = "t1", Type = BuiltInTransitions.Crossfade, From = "color", To = "segundo", Duration = Time.FromSeconds(0.5) });
        Clip pip = Add(top, a.Id, "pip", Time.FromSeconds(1), Time.FromSeconds(1), Time.FromSeconds(3));
        pip.Effects.Add(Effect(BuiltInEffects.Transform, (BuiltInEffects.TransformParameters.Scale, 0.35), (BuiltInEffects.TransformParameters.X, -60)));
        foreach (Clip clip in sequence.VideoTracks.SelectMany(t => t.Clips))
        {
            clip.Effects.ForEach(e => e.Id = $"{clip.Id}-{e.Type}");
        }

        Assert.Empty(ModelValidator.Validate(project));
        return project;
    }

    private static MediaRef Media(Project project, string folder, string id, string source)
    {
        string path = Path.Combine(folder, id + ".mkv");
        if (!File.Exists(path) || OperatingSystem.IsWindows())
        {
            Run("-f", "lavfi", "-i", source, "-c:v", "ffv1", "-pix_fmt", "yuv420p", "-y", path);
        }

        var reference = new MediaRef { Id = id, Path = path };
        MediaProbe.Probe(path).ApplyTo(reference);
        project.Media.Add(reference);
        return reference;
    }

    private static Clip Add(Track track, string mediaId, string id, Time start, Time sourceIn, Time duration)
    {
        var clip = new Clip { Id = id, MediaId = mediaId, Start = start, SourceIn = sourceIn, SourceOut = sourceIn + duration };
        track.Clips.Add(clip);
        track.SortClips();
        return clip;
    }

    private static EffectInstance Effect(string type, params (string Name, double Value)[] parameters)
    {
        var effect = new EffectInstance { Id = type, Type = type };
        foreach ((string name, double value) in parameters)
        {
            effect.Parameters[name] = new Parameter { Value = value };
        }

        return effect;
    }

    private static async Task<string?> ReferenceAsync(string encoder, Scratch scratch)
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(7))
        {
            return await ExportAsync(encoder, null, GpuDevice.CreateWarp, scratch.Combine("warp"));
        }

        string? folder = Environment.GetEnvironmentVariable(ReferenceVariable);
        string? file = folder is null ? null : Path.Combine(folder, "export-" + encoder + Extension(encoder));
        return file is not null && File.Exists(file) ? file : null;
    }

    private static string Extension(string encoder) => encoder == "prores_ks" ? ".mov" : ".mkv";

    private static string PixelFormat(string encoder) => encoder == "prores_ks" ? "yuv422p10le" : "yuv420p10le";

    /// <summary>Exporta a <paramref name="stem"/> (más la extensión del contenedor) y devuelve la ruta.</summary>
    private static async Task<string> ExportAsync(string encoder, Func<IGpu>? createGpu, Func<GpuDevice>? createDevice, string stem)
    {
        FfmpegExecutable ffmpeg = Ffmpeg;
        IReadOnlyList<EncoderSpec> encoders = await new CatalogEncoderSource(ffmpeg).GetAsync();
        EncoderSpec spec = encoders.First(e => e.Id == encoder);
        bool prores = spec.Codec == ExportCodec.ProRes;
        string path = stem + Extension(encoder);
        var runner = new ExportRunner(new ExportRunnerOptions { Ffmpeg = ffmpeg, CreateGpu = createGpu, CreateDevice = createDevice });
        using ExportJob job = runner.Start(new ExportRequest(s_project.Value, "sec", path, new ExportSettings
        {
            Container = prores ? ExportContainer.Mov : ExportContainer.Mkv,
            Video = new VideoExportSettings
            {
                Codec = spec.Codec,
                EncoderId = encoder,
                BitDepth = 10,
                Chroma = prores ? ChromaSubsampling.Chroma422 : ChromaSubsampling.Chroma420,
                Profile = prores ? "hq" : null,
                RateControl = prores ? RateControl.ProfileDefined : RateControl.Lossless,
            },
        }), encoders);
        await job.Completion.WaitAsync(TimeSpan.FromMinutes(10));
        Assert.True(job.Progress.State == ExportJobState.Completed, job.Failure?.ToString());
        return path;
    }

    private static int FrameCount(string path) => System.Text.Encoding.ASCII.GetString(Run("-i", path, "-map", "0:v:0", "-f", "framemd5", "-"))
        .Split('\n').Count(l => l.Length > 0 && l[0] != '#');

    private static ushort[] Decode(string path, string pixelFormat) =>
        MemoryMarshal.Cast<byte, ushort>(Run("-i", path, "-map", "0:v:0", "-f", "rawvideo", "-pix_fmt", pixelFormat, "-")).ToArray();

    private static byte[] Run(params string[] arguments)
    {
        var start = new ProcessStartInfo(Ffmpeg.Path) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (string argument in (string[])["-hide_banner", "-nostdin", "-loglevel", "error", .. arguments])
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        using var memory = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(memory);
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return memory.ToArray();
    }

    private sealed class Scratch : IDisposable
    {
        private readonly string _folder = Directory.CreateTempSubdirectory("rodaje-exportacion-").FullName;

        public string Combine(string name) => Path.Combine(_folder, name);

        public void Dispose() => Directory.Delete(_folder, recursive: true);
    }
}
