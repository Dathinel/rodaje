using System.Diagnostics;
using Rodaje.Audio;
using Rodaje.Core.Model;
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
/// Secuencia real de tres pistas con FFmpeg y D3D11VA: tres archivos de 24, 25 y 29,97 fps con el número de cuadro
/// dibujado, cortes cada 2 s en una secuencia de 30 fps. Se omite con el motivo si falta la GPU o FFmpeg.
/// </summary>
public sealed class TimelineHardwareTests(ITestOutputHelper output)
{
    private const int Tracks = 3;
    private const int Segments = 6;
    private static readonly Rational SequenceRate = Rational.Fps30;
    private static readonly Time SegmentLength = Time.FromSeconds(2);

    private static readonly MediaSpec[] Specs =
    [
        new("capas-640-24", 640, 360, Rational.Fps24, TimeSpan.FromSeconds(14)) { GopLength = 48, BFrames = 2 },
        new("capas-640-25", 640, 360, Rational.Fps25, TimeSpan.FromSeconds(14)) { GopLength = 50, BFrames = 2 },
        new("capas-640-2997", 640, 360, Rational.Ntsc30, TimeSpan.FromSeconds(14)) { GopLength = 60, BFrames = 2 },
    ];

    [HardwareFact(VideoCodec.H264, Audio = AudioCodec.Aac)]
    public void Every_layer_shows_the_expected_source_frame_at_many_timeline_instants()
    {
        (Project project, TimelinePlan plan) = BuildProject();
        using GpuDevice gpu = GpuDevice.Create();
        using var backend = new DeviceClockBackend();
        using TimelineSession session = new(project, "sec", gpu, new TimelineSessionOptions(), backend);

        var rng = new Random(20261009);
        var instants = new List<long>();
        for (int j = 1; j < Segments; j++)
        {
            instants.AddRange([(60 * j) - 1, 60 * j, (60 * j) + 1]);
        }

        instants.AddRange(Enumerable.Range(0, 45).Select(_ => (long)rng.Next(0, Segments * 60)));
        rng.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(instants));

        int checkedLayers = 0;
        var failures = new List<string>();
        foreach (long frame in instants)
        {
            session.SeekToFrame(frame);
            using CompositionFrame shown = TickUntilSettled(session);
            Assert.Equal(Tracks, shown.Layers.Count);
            foreach (LayerFrame layer in shown.Layers)
            {
                long expected = Expected(plan, layer.TrackIndex, shown.TimelineTime, out string clipId);
                LumaImage luma = FrameReadback.ReadLuma(layer.Frame);
                int? code = FrameCode.Read(luma.Pixels, luma.Width, luma.Height, luma.Width);
                if (layer.ClipId != clipId || layer.Frame.Index != expected || code != expected)
                {
                    failures.Add($"cuadro {frame}, pista {layer.TrackIndex}: clip {layer.ClipId} (esperado {clipId}), índice {layer.Frame.Index}, código {(code is { } c ? c.ToString(System.Globalization.CultureInfo.InvariantCulture) : "ilegible")}, esperado {expected}");
                }

                Assert.IsType<D3D11TexturePayload>(layer.Frame.Payload);
                checkedLayers++;
            }
        }

        output.WriteLine($"{instants.Count} instantes, {checkedLayers} capas comprobadas por su código, {failures.Count} fallos.");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        Assert.Equal(0, session.Stats.DecodeErrors);
    }

    [HardwareFact(VideoCodec.H264, Audio = AudioCodec.Aac)]
    [Trait("Categoria", "TiempoReal")]
    public void Cuts_with_pre_roll_arrive_on_time_when_playing_at_60_hz()
    {
        (Project project, TimelinePlan plan) = BuildProject();
        using GpuDevice gpu = GpuDevice.Create();
        using var backend = new DeviceClockBackend();
        using TimelineSession session = new(project, "sec", gpu, new TimelineSessionOptions(), backend);
        TickUntilSettled(session).Dispose();
        session.ResetStats();

        session.Play();
        Time start = backend.Clock.Position;
        long lastRefresh = (plan.Duration.Ticks / Time.FromFrames(1, Rational.Fps60).Ticks) - 2;
        var pacer = new WallClockPacer(60);
        long refreshes = 0;
        long wrong = 0;
        for (long slot = pacer.Next(); slot <= lastRefresh; slot = pacer.Next())
        {
            backend.Clock.Advance(start + Time.FromFrames(slot, Rational.Fps60) - backend.Clock.Position);
            refreshes++;
            using (CompositionFrame shown = session.Tick())
            {
                foreach (LayerFrame layer in shown.Layers)
                {
                    long expected = Expected(plan, layer.TrackIndex, shown.TimelineTime, out _);
                    if (layer.Frame.Index != expected)
                    {
                        wrong++;
                    }
                }
            }
        }

        TimelineStats stats = session.Stats;
        output.WriteLine($"{refreshes} refrescos a 60 Hz ({pacer.Skipped} saltados por llegar tarde el bucle); cortes {stats.Cuts}, pre-roll {stats.PreRollHits}/{stats.PreRollHits + stats.PreRollMisses} " +
            $"({stats.PreRollHitRate:P0}), tarde en cortes {stats.LateAtCuts}, tarde en total {stats.LateRefreshes}, cuadros saltados {stats.FramesDropped}, " +
            $"mostrados {stats.FramesShown}, refrescos con cuadro distinto del debido {wrong}, decodificadores {stats.DecodersOpen}, " +
            $"seeks de keyframe {stats.DecoderSeeks}, caché {stats.CacheBytes / (1024 * 1024)} MB de {stats.CacheBudgetBytes / (1024 * 1024)} MB.");
        foreach (LayerStats layer in stats.Layers)
        {
            output.WriteLine($"  pista {layer.TrackIndex}: mostrados {layer.FramesShown}, saltados {layer.FramesDropped}, tarde {layer.LateRefreshes}, " +
                $"cortes {layer.Cuts}, pre-roll {layer.PreRollHits}/{layer.PreRollHits + layer.PreRollMisses}, tarde en cortes {layer.LateAtCuts}");
        }

        Assert.Equal(Tracks * (Segments - 1), stats.Cuts);
        Assert.Equal(0, stats.PreRollMisses);
        Assert.Equal(0, stats.LateAtCuts);
        Assert.Equal(0, stats.DecodeErrors);
    }

    /// <summary>Pista k, tramo j: medio <c>(j + k) % 3</c>, entrada distinta en cada tramo.</summary>
    private static (Project Project, TimelinePlan Plan) BuildProject()
    {
        TestMediaGenerator generator = TestMediaGenerator.CreateDefault()!;
        var sequence = new Sequence { Id = "sec", Name = "Capas", FrameRate = SequenceRate, Width = 1920, Height = 1080 };
        var project = new Project { Id = "p", Name = "Capas", Sequences = [sequence] };
        for (int m = 0; m < Specs.Length; m++)
        {
            GeneratedMedia media = generator.GetOrCreate(Specs[m]);
            var reference = new MediaRef { Id = $"m{m}", Path = media.Path };
            MediaProbe.Probe(media.Path).ApplyTo(reference);
            project.Media.Add(reference);
        }

        for (int k = 0; k < Tracks; k++)
        {
            var track = new Track { Id = $"v{k}", Kind = TrackKind.Video, Name = $"Video {k + 1}" };
            for (int j = 0; j < Segments; j++)
            {
                Time sourceIn = Time.FromSeconds(((j * 37) + (k * 53)) % 100 / 10.0);
                track.Clips.Add(new Clip
                {
                    Id = $"v{k}-{j}",
                    MediaId = $"m{(j + k) % 3}",
                    Start = Time.FromTicks(SegmentLength.Ticks * j),
                    SourceIn = sourceIn,
                    SourceOut = sourceIn + SegmentLength,
                });
            }

            sequence.VideoTracks.Add(track);
        }

        Assert.Empty(ModelValidator.Validate(project));
        return (project, TimelinePlan.Build(project, "sec", Environment.CurrentDirectory));
    }

    /// <summary>Cuadro del origen que contiene el instante, calculado aparte con aritmética entera.</summary>
    private static long Expected(TimelinePlan plan, int trackIndex, Time timeline, out string clipId)
    {
        VideoClipPlan clip = plan.Layers.Single(l => l.TrackIndex == trackIndex).Clips.Single(c => c.Start <= timeline && timeline < c.End);
        clipId = clip.ClipId;
        Int128 sourceTicks = clip.ToSource(timeline).Ticks;
        return (long)(sourceTicks * clip.FrameRate.Num / ((Int128)Time.TicksPerSecond * clip.FrameRate.Den));
    }

    private static CompositionFrame TickUntilSettled(TimelineSession session)
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
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "Las capas no llegaron a su cuadro en 10 s.");
            Thread.Sleep(1);
        }
    }

    /// <summary>FFmpeg y D3D11VA reales, con un reloj manual en lugar del dispositivo de audio.</summary>
    internal sealed class DeviceClockBackend : IClipBackend, IDisposable
    {
        private readonly DisposalLog _log = new();
        private FakeOutput? _output;

        public ManualClock Clock => _output!.ManualClock;

        public IClipVideo OpenVideo(string path, VideoDecoderOptions options) => MediaClipBackend.Instance.OpenVideo(path, options);

        public IPcmSource OpenAudio(string path) => MediaClipBackend.Instance.OpenAudio(path);

        public IAudioOutput OpenOutput(IAudioSampleSource source, AudioOutputOptions options) => _output = new FakeOutput(source, hasDevice: true, _log);

        public IDecodeExecutor CreateExecutor() => MediaClipBackend.Instance.CreateExecutor();

        public IKeyframeIndex? ReadKeyframes(string path) => MediaClipBackend.Instance.ReadKeyframes(path);

        public void Dispose() => _output?.Dispose();
    }
}
