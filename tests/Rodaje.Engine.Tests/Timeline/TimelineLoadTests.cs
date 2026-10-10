using System.Diagnostics;
using System.Globalization;
using Rodaje.Audio;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Rodaje.Media.Gpu;
using Rodaje.Media.Probe;
using Rodaje.Media.Video;
using Rodaje.Playback;
using Rodaje.Playback.Composition;
using Rodaje.TestMedia;
using Xunit.Abstractions;

namespace Rodaje.Engine.Tests.Timeline;

/// <summary>
/// Cuatro pistas reproducidas en tiempo real a 60 Hz con decodificadores reales: cortes cada 3 s en 1080p y 4K HEVC entre seis
/// archivos (en cada corte dos medios son nuevos y dos pasan a otra pista),
/// y un mismo archivo en las cuatro pistas con 2 s de desfase. Mide la VRAM del proceso, el pre-roll, los refrescos
/// tarde y la peor espera de una capa; deja un <c>.rodaje</c> de cada escenario en la caché de medios para el visor.
/// </summary>
public sealed class TimelineLoadTests(ITestOutputHelper output)
{
    private const int Tracks = 4;
    private const int CutMedia = 6;
    private static readonly Time Segment = Time.FromSeconds(3);

    [HardwareFact(VideoCodec.H264, VideoCodec.Hevc, Audio = AudioCodec.Aac, LongRunning = true)]
    public void Four_hevc_1080p_tracks_with_cuts_every_3_s_stay_within_the_vram_budget_with_pre_roll_on_time()
    {
        LoadResult result = Run(CutsScenario("1080p", 1920, 1080));
        Assert.True(result.PeakVramBytes < result.BudgetBytes, $"Pico de VRAM {Mb(result.PeakVramBytes)} MB, presupuesto {Mb(result.BudgetBytes)} MB.");
        Assert.True(result.Stats.PreRollHitRate >= 0.9, $"Pre-roll {result.Stats.PreRollHitRate:P0}.");
        Assert.True(result.WorstStallMs <= 50, $"Una capa esperó {result.WorstStallMs:0} ms.");
        Assert.Equal(0, result.Stats.DecodeErrors);
    }

    [HardwareFact(VideoCodec.H264, VideoCodec.Hevc, Audio = AudioCodec.Aac, LongRunning = true)]
    public void Four_hevc_4k_tracks_with_cuts_every_3_s_stay_within_the_vram_budget()
    {
        LoadResult result = Run(CutsScenario("4k", 3840, 2160));
        Assert.True(result.PeakVramBytes < result.BudgetBytes, $"Pico de VRAM {Mb(result.PeakVramBytes)} MB, presupuesto {Mb(result.BudgetBytes)} MB.");
        Assert.Equal(0, result.Stats.DecodeErrors);
        Assert.True(result.MaxTickMs < 50, $"Un Tick tardó {result.MaxTickMs:0.0} ms.");
    }

    [HardwareFact(VideoCodec.H264, VideoCodec.Hevc, Audio = AudioCodec.Aac, LongRunning = true)]
    public void The_same_1080p_file_on_four_tracks_two_seconds_apart_plays_without_late_refreshes()
    {
        LoadResult result = Run(SameMediaScenario());
        Assert.Equal(0, result.Stats.LateRefreshes);
        Assert.Equal(0, result.WrongFrames);
        Assert.Equal(0, result.Stats.DecodeErrors);
    }

    private sealed record Scenario(string Name, Project Project, TimelinePlan Plan);

    private sealed record LoadResult(TimelineStats Stats, long PeakVramBytes, long BudgetBytes, double MaxTickMs, double WorstStallMs, long WrongFrames);

    private static Scenario CutsScenario(string label, int width, int height)
    {
        TestMediaGenerator generator = TestMediaGenerator.CreateDefault()!;
        MediaSpec[] specs = [.. Enumerable.Range(0, CutMedia).Select(m =>
            new MediaSpec($"carga-hevc-{label}-{m}", width, height, Rational.Fps30, TimeSpan.FromSeconds(20))
            {
                Codec = VideoCodec.Hevc,
                GopLength = 30,
                BFrames = 2,
            })];
        (Project project, Sequence sequence) = NewProject($"cortes-{label}", width, height);
        for (int m = 0; m < specs.Length; m++)
        {
            AddMedia(project, $"m{m}", generator.GetOrCreate(specs[m]).Path);
        }

        const int segments = 8;
        for (int k = 0; k < Tracks; k++)
        {
            Track track = sequence.VideoTracks[k];
            for (int j = 0; j < segments; j++)
            {
                Time sourceIn = Time.FromSeconds(((j * 37) + (k * 53)) % 140 / 10.0);
                track.Clips.Add(new Clip
                {
                    Id = $"c{k}-{j}",
                    MediaId = $"m{((j * Tracks) + k) % CutMedia}",
                    Start = Time.FromTicks(Segment.Ticks * j),
                    SourceIn = sourceIn,
                    SourceOut = sourceIn + Segment,
                    Effects = k == 0 ? [] : [Corner($"t{k}-{j}", k, width, height)],
                });
            }
        }

        return Finish($"cortes-hevc-{label}", project);
    }

    private static Scenario SameMediaScenario()
    {
        TestMediaGenerator generator = TestMediaGenerator.CreateDefault()!;
        var spec = new MediaSpec("carga-h264-1080p-30s", 1920, 1080, Rational.Fps30, TimeSpan.FromSeconds(30)) { GopLength = 60, BFrames = 2 };
        (Project project, Sequence sequence) = NewProject("mismo-medio", 1920, 1080);
        const int width = 1920;
        const int height = 1080;
        AddMedia(project, "m", generator.GetOrCreate(spec).Path);
        for (int k = 0; k < Tracks; k++)
        {
            Time sourceIn = Time.FromSeconds(2 * k);
            sequence.VideoTracks[k].Clips.Add(new Clip
            {
                Id = $"c{k}",
                MediaId = "m",
                Start = Time.Zero,
                SourceIn = sourceIn,
                SourceOut = sourceIn + Time.FromSeconds(14),
                Effects = k == 0 ? [] : [Corner($"t{k}", k, width, height)],
            });
        }

        return Finish("mismo-medio-1080p-4-pistas", project);
    }

    private static (Project, Sequence) NewProject(string name, int width, int height)
    {
        var sequence = new Sequence { Id = "sec", Name = name, FrameRate = Rational.Fps30, Width = width, Height = height };
        for (int k = 0; k < Tracks; k++)
        {
            sequence.VideoTracks.Add(new Track { Id = $"v{k}", Kind = TrackKind.Video, Name = $"Video {k + 1}" });
        }

        return (new Project { Id = "p", Name = name, Sequences = [sequence] }, sequence);
    }

    private static void AddMedia(Project project, string id, string path)
    {
        var media = new MediaRef { Id = id, Path = path };
        MediaProbe.Probe(path).ApplyTo(media);
        project.Media.Add(media);
    }

    private static EffectInstance Corner(string id, int k, int width, int height) => new()
    {
        Id = id,
        Type = BuiltInEffects.Transform,
        Parameters = new()
        {
            [BuiltInEffects.TransformParameters.Scale] = new Parameter { Value = 0.4 },
            [BuiltInEffects.TransformParameters.X] = new Parameter { Value = (k % 2 == 0 ? -0.3 : 0.3) * width },
            [BuiltInEffects.TransformParameters.Y] = new Parameter { Value = (k < 3 ? -0.3 : 0.3) * height },
        },
    };

    private static Scenario Finish(string name, Project project)
    {
        Assert.Empty(ModelValidator.Validate(project));
        string directory = Path.Combine(TestMediaGenerator.CreateDefault()!.CacheDirectory, "escenarios");
        Directory.CreateDirectory(directory);
        ProjectSerializer.Save(project, Path.Combine(directory, name + ProjectSerializer.FileExtension));
        return new Scenario(name, project, TimelinePlan.Build(project, "sec", Environment.CurrentDirectory));
    }

    private LoadResult Run(Scenario scenario)
    {
        using GpuDevice gpu = GpuDevice.Create();
        long baseline = gpu.QueryLocalVideoMemoryUsage() ?? 0;
        using var backend = new RealTimeBackend();
        var options = new TimelineSessionOptions();
        using var session = new TimelineSession(scenario.Project, "sec", gpu, options, backend);
        long budget = session.Stats.VramBudgetBytes;
        long peak = 0;
        var settle = Stopwatch.StartNew();
        while (true)
        {
            session.Tick().Dispose();
            peak = Math.Max(peak, (gpu.QueryLocalVideoMemoryUsage() ?? 0) - baseline);
            if (!session.IsSeekPending)
            {
                break;
            }

            Assert.True(settle.Elapsed < TimeSpan.FromSeconds(20), "Las capas no llegaron a su primer cuadro en 20 s.");
            Thread.Sleep(1);
        }

        session.ResetStats();
        session.Play();
        Time refresh = Time.FromFrames(1, Rational.Fps60);
        int refreshes = (int)(scenario.Plan.Duration.Ticks / refresh.Ticks) - 2;
        var layerWaitStart = new long?[Tracks];
        var stallStart = new long?[Tracks];
        var ticks = new List<double>(refreshes);
        var stalls = new List<string>();
        double worstStallMs = 0;
        double maxTickMs = 0;
        long wrong = 0;
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < refreshes; i++)
        {
            backend.Clock.Advance(refresh);
            long before = Stopwatch.GetTimestamp();
            using (CompositionFrame shown = session.Tick())
            {
                double tickMs = Stopwatch.GetElapsedTime(before).TotalMilliseconds;
                maxTickMs = Math.Max(maxTickMs, tickMs);
                ticks.Add(tickMs);
                long now = Stopwatch.GetTimestamp();
                for (int k = 0; k < Tracks; k++)
                {
                    LayerFrame? layer = shown.Layers.FirstOrDefault(l => l.TrackIndex == k);
                    long expected = Expected(scenario.Plan, k, shown.TimelineTime, out string clipId);
                    bool exact = layer is not null && layer.ClipId == clipId && layer.Frame.Index == expected;
                    if (exact)
                    {
                        if (layerWaitStart[k] is { } start)
                        {
                            worstStallMs = Math.Max(worstStallMs, Stopwatch.GetElapsedTime(start, now).TotalMilliseconds);
                            layerWaitStart[k] = null;
                        }
                    }
                    else
                    {
                        wrong++;
                        layerWaitStart[k] ??= now;
                    }

                    if (exact && stallStart[k] is { } began)
                    {
                        double ms = Stopwatch.GetElapsedTime(began, now).TotalMilliseconds;
                        if (ms > 50)
                        {
                            stalls.Add(string.Create(CultureInfo.InvariantCulture, $"V{k + 1}@{shown.TimelineTime.ToSeconds():0.00}s:{ms:0}"));
                        }
                    }

                    stallStart[k] = exact ? null : stallStart[k] ?? now;
                }
            }

            peak = Math.Max(peak, (gpu.QueryLocalVideoMemoryUsage() ?? 0) - baseline);
            TimeSpan due = TimeSpan.FromTicks((long)((i + 1) * (TimeSpan.TicksPerSecond / 60.0)));
            while (watch.Elapsed < due)
            {
                Thread.Sleep(1);
            }
        }

        TimelineStats stats = session.Stats;
        long final = (gpu.QueryLocalVideoMemoryUsage() ?? 0) - baseline;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{scenario.Name}: {refreshes} refrescos a 60 Hz en {watch.Elapsed.TotalSeconds:0.0} s, GPU {gpu.AdapterName} ({Mb(gpu.DedicatedVideoMemory)} MB)."));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  VRAM del proceso: pico {Mb(peak)} MB sobre la base, {Mb(final)} MB al final; presupuesto {Mb(budget)} MB; contabilizada {Mb(stats.VramCommittedBytes)} MB al final (pico {Mb(stats.VramPeakBytes)} MB)."));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  Cortes {stats.Cuts}, pre-roll {stats.PreRollHits}/{stats.PreRollHits + stats.PreRollMisses} ({stats.PreRollHitRate:P0}), tarde en cortes {stats.LateAtCuts}, tarde en total {stats.LateRefreshes}, saltados {stats.FramesDropped}, mostrados {stats.FramesShown}."));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  Capa sin su cuadro exacto: {wrong} refrescos; peor espera {worstStallMs:0} ms; Tick: mediana {Percentile(ticks, 0.5):0.00} ms, p99 {Percentile(ticks, 0.99):0.00} ms, máximo {maxTickMs:0.00} ms."));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  Decodificadores abiertos {stats.DecodersOpen}, cerrados {stats.DecodersClosed}, seeks de keyframe {stats.DecoderSeeks}, errores {stats.DecodeErrors}, caché {Mb(stats.CacheBytes)} MB de {Mb(stats.CacheBudgetBytes)} MB."));
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  Esperas por VRAM al crear un pool: {session.PoolWaits.Count} ({session.PoolWaits.Milliseconds:0} ms); esperas de capa de más de 50 ms: {string.Join(", ", stalls)}"));
        return new LoadResult(stats, peak, budget, maxTickMs, worstStallMs, wrong);
    }

    private static long Mb(long bytes) => bytes / (1024 * 1024);

    private static double Percentile(List<double> values, double fraction)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        List<double> sorted = [.. values.Order()];
        return sorted[(int)Math.Min(sorted.Count - 1, Math.Round(fraction * (sorted.Count - 1)))];
    }

    private static long Expected(TimelinePlan plan, int trackIndex, Time timeline, out string clipId)
    {
        VideoClipPlan clip = plan.Layers.Single(l => l.TrackIndex == trackIndex).Clips.Single(c => c.Start <= timeline && timeline < c.End);
        clipId = clip.ClipId;
        Int128 sourceTicks = timeline.Ticks - clip.Start.Ticks + clip.SourceIn.Ticks;
        return (long)(sourceTicks * clip.FrameRate.Num / ((Int128)Time.TicksPerSecond * clip.FrameRate.Den));
    }

    /// <summary>FFmpeg y D3D11VA reales con hilos reales, y un reloj manual en lugar del dispositivo de audio.</summary>
    private sealed class RealTimeBackend : IClipBackend, IDisposable
    {
        private readonly DisposalLog _log = new();
        private FakeOutput? _output;

        public ManualClock Clock => _output!.ManualClock;

        public IClipVideo OpenVideo(string path, VideoDecoderOptions options) => MediaClipBackend.Instance.OpenVideo(path, options);

        public IPcmSource OpenAudio(string path) => MediaClipBackend.Instance.OpenAudio(path);

        public IAudioOutput OpenOutput(IAudioSampleSource source, AudioOutputOptions options) => _output = new FakeOutput(source, hasDevice: true, _log);

        public IDecodeExecutor CreateExecutor() => MediaClipBackend.Instance.CreateExecutor();

        public void Dispose() => _output?.Dispose();
    }
}
