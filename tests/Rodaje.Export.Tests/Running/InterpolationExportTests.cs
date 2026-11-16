using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Offline;
using Rodaje.Engine.Timeline;
using Rodaje.Export.Encoders;
using Rodaje.Export.Running;
using Rodaje.Media.Export;
using Rodaje.Media.Gpu;
using Rodaje.Media.Probe;
using Rodaje.Playback.Composition;
using Rodaje.Render;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Export.Tests.Running;

/// <summary>
/// Verificación de punta a punta de la interpolación de cuadros en exportación y coherencia con la vista previa.
/// </summary>
[Collection(ExportGpuGroup.Name)]
public sealed class InterpolationExportTests(ITestOutputHelper output) : IDisposable
{
    private static readonly ConcurrentBag<string> CreatedFolders = [];
    private static readonly Lazy<Task<IReadOnlyList<EncoderSpec>>> Encoders = new(() =>
        new CatalogEncoderSource(Skip.ProxyExecutable!).GetAsync());

    private static readonly object VideoLock = new();
    private static string? CachedPatternVideo;

    [ExportLongFact]
    public async Task Export_slow_motion_optical_flow_achieves_required_psnr()
    {
        string videoPath = GetOrCreatePatternVideo();
        Project project = CreateProject(videoPath, FrameSampling.OpticalFlow);

        // Exportar con OpticalFlow, con Blend y con Nearest
        (ExportJob jobFlow, string pathFlow) = await RunExportAsync(project, FrameSampling.OpticalFlow, "flow.mp4");
        (ExportJob jobBlend, string pathBlend) = await RunExportAsync(project, FrameSampling.Blend, "blend.mp4");
        (ExportJob jobNearest, string pathNearest) = await RunExportAsync(project, FrameSampling.Nearest, "nearest.mp4");

        const int width = TranslatingPatternMedia.DefaultWidth;
        const int height = TranslatingPatternMedia.DefaultHeight;
        const int shift = TranslatingPatternMedia.DefaultShiftPerFrame;

        // Decodificar los cuadros intermedios (t = 0.25, 0.50, 0.75 corresponden a los cuadros 1, 2, 3 de la secuencia a 0.25x)
        Dictionary<int, byte[]> flowFrames = DecodeFrames(pathFlow, width, height, [1, 2, 3]);
        Dictionary<int, byte[]> blendFrames = DecodeFrames(pathBlend, width, height, [1, 2, 3]);
        Dictionary<int, byte[]> nearestFrames = DecodeFrames(pathNearest, width, height, [1, 2, 3]);

        byte[] truth25 = TranslatingPatternMedia.CreateTruthLuma(0.25f, width, height, shift);
        byte[] truth50 = TranslatingPatternMedia.CreateTruthLuma(0.50f, width, height, shift);
        byte[] truth75 = TranslatingPatternMedia.CreateTruthLuma(0.75f, width, height, shift);

        double psnrFlow25 = TranslatingPatternMedia.ComputePsnr(flowFrames[1], truth25, width, height);
        double psnrFlow50 = TranslatingPatternMedia.ComputePsnr(flowFrames[2], truth50, width, height);
        double psnrFlow75 = TranslatingPatternMedia.ComputePsnr(flowFrames[3], truth75, width, height);

        double psnrBlend25 = TranslatingPatternMedia.ComputePsnr(blendFrames[1], truth25, width, height);
        double psnrBlend50 = TranslatingPatternMedia.ComputePsnr(blendFrames[2], truth50, width, height);
        double psnrBlend75 = TranslatingPatternMedia.ComputePsnr(blendFrames[3], truth75, width, height);

        double psnrNearest25 = TranslatingPatternMedia.ComputePsnr(nearestFrames[1], truth25, width, height);
        double psnrNearest50 = TranslatingPatternMedia.ComputePsnr(nearestFrames[2], truth50, width, height);
        double psnrNearest75 = TranslatingPatternMedia.ComputePsnr(nearestFrames[3], truth75, width, height);

        // Anotar los tres PSNR en la salida de la prueba para cada t
        output.WriteLine("================ PSNR MEDIDOS CONTRA LA VERDAD SINTÉTICA ================");
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[t = 0.25] Flujo óptico: {psnrFlow25:F2} dB | Mezcla: {psnrBlend25:F2} dB | Más cercano: {psnrNearest25:F2} dB"));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[t = 0.50] Flujo óptico: {psnrFlow50:F2} dB | Mezcla: {psnrBlend50:F2} dB | Más cercano: {psnrNearest50:F2} dB"));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[t = 0.75] Flujo óptico: {psnrFlow75:F2} dB | Mezcla: {psnrBlend75:F2} dB | Más cercano: {psnrNearest75:F2} dB"));
        output.WriteLine("=========================================================================");

        // Varas exigidas por F6: flujo >= 30 dB y al menos 6 dB por encima de Blend en los cuadros intermedios.
        // Nota: F4 (compute shader en GPU) se desarrolla en paralelo. Mientras tanto OpticalFlow hace Blend,
        // por lo que las aserciones de +6 dB fallarán hasta que F4 esté unido a main.
        Assert.True(psnrFlow25 >= 30.0, string.Create(CultureInfo.InvariantCulture,
            $"El flujo óptico a t=0.25 ({psnrFlow25:F2} dB) debe ser >= 30 dB."));
        Assert.True(psnrFlow50 >= 30.0, string.Create(CultureInfo.InvariantCulture,
            $"El flujo óptico a t=0.50 ({psnrFlow50:F2} dB) debe ser >= 30 dB."));
        Assert.True(psnrFlow75 >= 30.0, string.Create(CultureInfo.InvariantCulture,
            $"El flujo óptico a t=0.75 ({psnrFlow75:F2} dB) debe ser >= 30 dB."));

        Assert.True(psnrFlow25 >= psnrBlend25 + 6.0, string.Create(CultureInfo.InvariantCulture,
            $"El flujo óptico a t=0.25 ({psnrFlow25:F2} dB) debe superar a Mezcla ({psnrBlend25:F2} dB) por al menos 6 dB (diferencia: {psnrFlow25 - psnrBlend25:F2} dB)."));
        Assert.True(psnrFlow50 >= psnrBlend50 + 6.0, string.Create(CultureInfo.InvariantCulture,
            $"El flujo óptico a t=0.50 ({psnrFlow50:F2} dB) debe superar a Mezcla ({psnrBlend50:F2} dB) por al menos 6 dB (diferencia: {psnrFlow50 - psnrBlend50:F2} dB)."));
        Assert.True(psnrFlow75 >= psnrBlend75 + 6.0, string.Create(CultureInfo.InvariantCulture,
            $"El flujo óptico a t=0.75 ({psnrFlow75:F2} dB) debe superar a Mezcla ({psnrBlend75:F2} dB) por al menos 6 dB (diferencia: {psnrFlow75 - psnrBlend75:F2} dB)."));
    }

    [ExportLongTheory]
    [InlineData(FrameSampling.Blend)]
    [InlineData(FrameSampling.OpticalFlow)]
    public void Paused_preview_matches_export_at_half_fraction(FrameSampling sampling)
    {
        string videoPath = GetOrCreatePatternVideo();
        Project project = CreateProject(videoPath, sampling);

        using GpuDevice previewGpu = GpuDevice.Create();
        using GpuDevice exportGpu = GpuDevice.Create();

        using var preview = TimelineSession.Open(project, "sec", previewGpu,
            new TimelineSessionOptions { EnableAudio = false, UseProxies = ProxyUsage.Never });
        using var previewCompositor = new Compositor(previewGpu, preview.Width, preview.Height);

        using OfflineTimeline offline = OfflineTimeline.Open(project, "sec", exportGpu);
        using var exportCompositor = new Compositor(exportGpu, offline.Width, offline.Height);

        // En el cuadro 2 de la secuencia a 0.25x, el instante cae exactamente en t = 0.5
        preview.SeekToFrame(2);
        using CompositionFrame previewFrame = Settle(preview);
        previewCompositor.RenderStrict(previewFrame);

        using CompositionFrame exportFrame = offline.Render(2, CancellationToken.None);
        exportCompositor.RenderStrict(exportFrame);

        byte[] previewBytes = previewCompositor.ReadbackRgba();
        byte[] exportBytes = exportCompositor.ReadbackRgba();

        Assert.Equal(previewBytes.Length, exportBytes.Length);
        int maxDiff = 0;
        long diffCount = 0;
        for (int i = 0; i < previewBytes.Length; i++)
        {
            int diff = Math.Abs(previewBytes[i] - exportBytes[i]);
            if (diff > maxDiff) maxDiff = diff;
            if (diff > 0) diffCount++;
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[{sampling} t=0.5] Diferencia máxima entre vista previa y exportación: {maxDiff} (bytes distintos: {diffCount} de {previewBytes.Length})."));
        Assert.True(maxDiff <= 1, string.Create(CultureInfo.InvariantCulture,
            $"Diferencia máxima entre vista previa y exportación fue {maxDiff}, esperado <= 1."));
    }

    private static string GetOrCreatePatternVideo()
    {
        lock (VideoLock)
        {
            if (CachedPatternVideo is not null && File.Exists(CachedPatternVideo))
            {
                return CachedPatternVideo;
            }

            string folder = Path.Combine(Path.GetTempPath(), "rodaje-patron-interp");
            string path = Path.Combine(folder, "traslacion-320x180.mp4");
            TranslatingPatternMedia.CreateVideo(path);
            CachedPatternVideo = path;
            return path;
        }
    }

    private static Project CreateProject(string videoPath, FrameSampling sampling)
    {
        const int width = TranslatingPatternMedia.DefaultWidth;
        const int height = TranslatingPatternMedia.DefaultHeight;

        var sequence = new Sequence
        {
            Id = "sec",
            Name = "Interpolación",
            FrameRate = Rational.Fps30,
            Width = width,
            Height = height,
        };
        var project = new Project { Id = "p", Name = "Interpolación", Sequences = [sequence] };
        var mediaRef = new MediaRef { Id = "m", Path = videoPath };
        MediaProbe.Probe(videoPath).ApplyTo(mediaRef);
        project.Media.Add(mediaRef);

        var track = new Track { Id = "v0", Kind = TrackKind.Video, Name = "Video 1" };
        sequence.VideoTracks.Add(track);

        // Clip de 1 segundo (30 cuadros) del medio puesto a 0.25x en la secuencia: dura 4 segundos (120 cuadros)
        var clip = new Clip
        {
            Id = "c0",
            MediaId = mediaRef.Id,
            Start = Time.Zero,
            SourceIn = Time.Zero,
            SourceOut = Time.FromSeconds(1),
            Speed = new ClipSpeed
            {
                Rate = new Rational(1, 4),
                FrameSampling = sampling,
            },
        };
        ClipTimeMap.Fit(clip.Start, clip.SourceIn, clip.SourceOut, clip.Speed).ApplyTo(clip);
        track.Clips.Add(clip);
        track.SortClips();

        var issues = ModelValidator.Validate(project);
        Assert.True(issues.Count == 0, string.Join(Environment.NewLine, issues));
        return project;
    }

    private static async Task<(ExportJob Job, string Path)> RunExportAsync(Project project, FrameSampling sampling, string fileName)
    {
        Project clone = project.Clone();
        clone.Sequences[0].VideoTracks[0].Clips[0].Speed!.FrameSampling = sampling;

        IReadOnlyList<EncoderSpec> encoders = await Encoders.Value;
        string folder = CreateFolder();
        string path = Path.Combine(folder, fileName);

        var runner = new ExportRunner(new ExportRunnerOptions { Ffmpeg = Skip.ProxyExecutable! });
        var settings = new ExportSettings
        {
            Video = new VideoExportSettings
            {
                Codec = ExportCodec.H264,
                RateControl = RateControl.ConstantQuality(12),
            },
        };

        ExportJob job = runner.Start(new ExportRequest(clone, "sec", path, settings), encoders);
        await job.Completion.WaitAsync(TimeSpan.FromMinutes(2));
        Assert.True(job.Progress.State == ExportJobState.Completed, job.Failure?.ToString());
        Assert.True(File.Exists(path));
        return (job, path);
    }

    private static Dictionary<int, byte[]> DecodeFrames(string path, int width, int height, HashSet<int> frameIndices)
    {
        var frames = new Dictionary<int, byte[]>();
        FfmpegTool.Default!.DecodeGrayFrames(path, width, height, (index, gray) =>
        {
            if (frameIndices.Contains(index))
            {
                frames[index] = gray.ToArray();
            }
        });

        foreach (int needed in frameIndices)
        {
            Assert.True(frames.ContainsKey(needed), $"No se pudo decodificar el cuadro {needed} de {path}.");
        }

        return frames;
    }

    private static CompositionFrame Settle(TimelineSession session)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            CompositionFrame frame = session.Tick();
            if (!session.IsSeekPending)
            {
                return frame;
            }

            frame.Dispose();
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), "La vista previa no llegó a su cuadro en 15 s.");
            Thread.Sleep(2);
        }
    }

    private static string CreateFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "rodaje-pruebas-interp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        CreatedFolders.Add(folder);
        return folder;
    }

    public void Dispose()
    {
        while (CreatedFolders.TryTake(out string? folder))
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

/// <summary>Atributo para pruebas largas de exportación que requieren GPU y FFmpeg.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ExportLongFactAttribute : FactAttribute
{
    public ExportLongFactAttribute() => Skip = Rodaje.TestMedia.Skip.First(
        Rodaje.TestMedia.Skip.LongRun,
        Rodaje.TestMedia.Skip.Proxies,
        Rodaje.TestMedia.Skip.GpuTextures,
        Rodaje.TestMedia.Skip.HardwareDecoding);
}

/// <summary>Atributo para teorías largas de exportación que requieren GPU y FFmpeg.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ExportLongTheoryAttribute : TheoryAttribute
{
    public ExportLongTheoryAttribute() => Skip = Rodaje.TestMedia.Skip.First(
        Rodaje.TestMedia.Skip.LongRun,
        Rodaje.TestMedia.Skip.Proxies,
        Rodaje.TestMedia.Skip.GpuTextures,
        Rodaje.TestMedia.Skip.HardwareDecoding);
}
