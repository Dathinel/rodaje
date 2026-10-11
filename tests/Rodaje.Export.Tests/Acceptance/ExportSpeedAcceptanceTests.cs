using System.Diagnostics;
using System.Globalization;
using Rodaje.Core.Timing;
using Rodaje.Engine.Offline;
using Rodaje.Export.Running;
using Rodaje.Media.Export;
using Rodaje.Media.Gpu;
using Rodaje.Playback.Composition;
using Rodaje.Render;
using Rodaje.Render.Export;
using Rodaje.TestMedia;
using Xunit.Abstractions;
using static Rodaje.Export.Tests.Acceptance.AcceptanceSupport;

namespace Rodaje.Export.Tests.Acceptance;

/// <summary>
/// Velocidad, exactitud y A/V de la fase 6 sobre 10 min de la escena de aceptación (1080p30, 3 capas con efectos,
/// velocidades y transiciones): NVENC H.264 ≤3 min con el 100 % de los números y A/V a los 10 min; SVT-AV1 ≤10 min;
/// el canal sin codificar ≥150 fps.
/// </summary>
[Collection(ExportAcceptanceGroup.Name)]
public sealed class ExportSpeedAcceptanceTests(ITestOutputHelper output)
{
    private const int Minutes = 10;

    [ExportAcceptanceFact]
    public async Task Nvenc_exports_10_minutes_in_3_with_every_frame_exact_and_av_in_sync()
    {
        AcceptanceScene scene = Scene(Minutes);
        string folder = Folder("nvenc-10min");
        string path = Path.Combine(folder, "diez minutos.mp4");
        var settings = new ExportSettings
        {
            Video = new VideoExportSettings { Codec = ExportCodec.H264, EncoderId = "h264_nvenc", RateControl = RateControl.Vbr(12_000, 18_000), Profile = "high" },
            Audio = new AudioExportSettings { Codec = ExportCodec.Aac },
            Performance = ExportPerformance.Fastest,
        };
        output.WriteLine($"Escena: {scene.FrameCount} cuadros, {scene.Transitions} transiciones, {scene.Layers.Sum(l => l.Clips.Count)} clips.");

        ExportJob job = await ExportAsync(output, scene.Project, path, settings);
        double seconds = job.Progress.Elapsed.TotalSeconds;
        Assert.Equal(scene.FrameCount, job.Report!.Video!.FrameCount);
        Assert.True(job.Report.MoovAtStart);

        (long checkedCount, long covered, List<string> failures, long frames) = CheckCodes(scene, path);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Números: {checkedCount - failures.Count}/{checkedCount} exactos en {frames} cuadros ({covered} lecturas tapadas por transiciones). {string.Join("; ", failures.Take(10))}"));

        (double mean, double max, int marks, int missing, double firstMs) = AvSync(path, scene.Duration);
        (double videoStart, double audioStart) = StartTimes(path);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"A/V: {marks} marcas (faltan {missing}), error medio {mean:0.000} ms, máximo {max:0.000} ms, primera marca {firstMs:0.000} ms; start_time video {videoStart:0.000000} s, audio {audioStart:0.000000} s."));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Tiempo NVENC 10 min: {seconds:0.0} s ({scene.FrameCount / seconds:0} fps)."));
        Delete(folder);

        Assert.Equal(scene.FrameCount, frames);
        Assert.True(failures.Count == 0, $"{failures.Count} números mal: {string.Join("; ", failures.Take(20))}");
        Assert.True(seconds <= 180, $"{seconds:0.0} s");
        Assert.True(Math.Abs(mean) <= 1 && max <= 1000.0 / 60, $"A/V medio {mean:0.000} ms, máximo {max:0.000} ms");
        Assert.True(missing == 0, $"Faltan {missing} marcas de audio.");
        Assert.InRange(videoStart, -1e-6, 1e-6);
        Assert.InRange(audioStart, -1.0 / 48_000, 1.0 / 48_000);
    }

    [ExportAcceptanceFact]
    public async Task Svt_av1_exports_10_minutes_in_10()
    {
        AcceptanceScene scene = Scene(Minutes);
        string folder = Folder("av1-10min");
        string path = Path.Combine(folder, "diez minutos.mkv");
        ExportSettings settings = ExportPresets.SmallFile.Settings with { Container = ExportContainer.Mkv, Performance = ExportPerformance.Fastest };
        ExportJob job = await ExportAsync(output, scene.Project, path, settings, timeout: TimeSpan.FromMinutes(30));
        double seconds = job.Progress.Elapsed.TotalSeconds;
        long bytes = new FileInfo(path).Length;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Tiempo SVT-AV1 (preset {job.Plan!.VideoEncoder!.DefaultSpeedPreset}) 10 min: {seconds:0.0} s ({scene.FrameCount / seconds:0.0} fps), {bytes * 8 / scene.Duration.ToSeconds() / 1e6:0.00} Mbps; {Environment.ProcessorCount} hilos lógicos."));
        Delete(folder);
        Assert.True(seconds <= 600, $"{seconds:0.0} s");
    }

    /// <summary>El camino de un cuadro sin codificador: motor fuera de línea, composición, conversión a NV12 y bajada a la CPU.</summary>
    [ExportAcceptanceFact]
    public void Without_encoding_the_pipeline_runs_at_150_fps_or_more()
    {
        AcceptanceScene scene = Scene(Minutes);
        using GpuDevice gpu = GpuDevice.Create();
        ExportPerformanceProfile profile = ExportPerformanceProfile.For(ExportPerformance.Fastest, gpu.DedicatedVideoMemory);
        var options = new OfflineTimelineOptions { LookaheadFrames = profile.LookaheadFrames, MaxDecodersPerMedia = profile.MaxDecodersPerMedia };
        using OfflineTimeline timeline = OfflineTimeline.Open(scene.Project, AcceptanceScene.SequenceId, gpu, options);
        timeline.Prepare(Time.Zero, timeline.Duration, CancellationToken.None);
        using var compositor = new Compositor(gpu, timeline.Width, timeline.Height);
        var format = new ExportVideoFormat(timeline.Width, timeline.Height, timeline.FrameRate, ExportPixelFormat.Nv12, ExportColor.Bt709Limited);
        using var converter = new ExportFrameConverter(gpu, timeline.Width, timeline.Height, format);
        long checksum = 0;
        void Consume(in ExportVideoFrame frame) => checksum += frame.Plane0[frame.Plane0.Length / 2];
        ExportFrameHandler consume = Consume;
        Func<LayerFrame, SourceGeometry> geometry = l => new SourceGeometry(l.SampleAspectRatio, l.Rotation);

        var watch = Stopwatch.StartNew();
        double worstMs = 0;
        for (long i = 0; i < timeline.FrameCount; i++)
        {
            long before = Stopwatch.GetTimestamp();
            if (converter.Pending == converter.Capacity)
            {
                converter.TryRead(consume);
            }

            using (CompositionFrame frame = timeline.Render(i, CancellationToken.None))
            {
                compositor.RenderStrict(frame, geometry);
            }

            converter.Submit(compositor.Texture, i);
            worstMs = Math.Max(worstMs, Stopwatch.GetElapsedTime(before).TotalMilliseconds);
        }

        while (converter.TryRead(consume))
        {
        }

        watch.Stop();
        double fps = timeline.FrameCount / watch.Elapsed.TotalSeconds;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Sin codificar: {timeline.FrameCount} cuadros en {watch.Elapsed.TotalSeconds:0.0} s = {fps:0} fps; peor cuadro {worstMs:0.0} ms; espera de la bajada {converter.ReadWait.TotalSeconds:0.0} s ({checksum})."));
        Assert.True(fps >= 150, $"{fps:0} fps");
    }

    /// <summary>
    /// Error A/V a lo largo de los 10 min: la marca de cada segundo del medio debe caer donde la pone el clip de audio
    /// (segundo 1 del medio en 0), contra el video, que es exacto por sus números. En ms.
    /// </summary>
    private static (double Mean, double Max, int Marks, int Missing, double FirstMs) AvSync(string path, Time duration)
    {
        const int rate = 48_000;
        float[] audio = DecodeAudioMono(path);
        int seconds = (int)Math.Floor(duration.ToSeconds() - 0.8);
        var errors = new List<double>();
        int missing = 0;
        for (int j = 0; j <= seconds; j++)
        {
            long center = (long)j * rate;
            long start = Math.Max(0, center - (rate / 4));
            long end = Math.Min(audio.Length, center + (rate * 3 / 4));
            int expected = (j + (int)AcceptanceScene.AudioSourceIn.ToSeconds()) % AudioCode.SecondModulus;
            AudioMark? mark = AudioCode.Detect(audio.AsSpan((int)start, (int)(end - start)), rate, 1)
                .Where(m => m.Second == expected).Cast<AudioMark?>().FirstOrDefault();
            if (mark is null)
            {
                missing++;
                continue;
            }

            errors.Add((start + mark.Value.Sample - center) * 1000.0 / rate);
        }

        return (errors.Count == 0 ? double.NaN : errors.Average(), errors.Count == 0 ? double.NaN : errors.Max(Math.Abs), errors.Count, missing,
            errors.Count == 0 ? double.NaN : errors[0]);
    }
}
