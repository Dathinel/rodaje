using System.Diagnostics;
using System.Globalization;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Rodaje.Media.Gpu;
using Rodaje.Media.Probe;
using Rodaje.Media.Video;
using Rodaje.Playback.Composition;
using Rodaje.TestMedia;
using Xunit.Abstractions;
using DeviceClockBackend = Rodaje.Engine.Tests.Timeline.TimelineHardwareTests.DeviceClockBackend;

namespace Rodaje.Engine.Tests.Timeline;

/// <summary>
/// Velocidad con FFmpeg y D3D11VA reales sobre archivos con el número de cuadro dibujado: seek exacto en cada caso de la
/// fase 5c y reproducción de 1080p a 2× y 4× sin pérdidas. Con <c>RODAJE_PRUEBAS_LARGAS=1</c> son 2000 seeks por caso.
/// </summary>
public sealed class TimelineSpeedHardwareTests(ITestOutputHelper output)
{
    private static readonly Rational SequenceRate = Rational.Fps30;

    private static readonly MediaSpec[] SeekSpecs =
    [
        new("velocidad-640-24", 640, 360, Rational.Fps24, TimeSpan.FromSeconds(210)) { GopLength = 48, BFrames = 2 },
        new("velocidad-640-25", 640, 360, Rational.Fps25, TimeSpan.FromSeconds(210)) { GopLength = 50, BFrames = 2 },
        new("velocidad-640-2997", 640, 360, Rational.Ntsc30, TimeSpan.FromSeconds(210)) { GopLength = 60, BFrames = 2 },
    ];

    public static TheoryData<string> Cases => [.. SpeedClips.Cases];

    [HardwareTheory(VideoCodec.H264)]
    [MemberData(nameof(Cases))]
    public void Seeks_show_the_source_frame_drawn_in_the_image_at_every_speed(string name)
    {
        TestMediaGenerator generator = TestMediaGenerator.CreateDefault()!;
        var sequence = new Sequence { Id = "sec", Name = "Velocidad", FrameRate = SequenceRate, Width = 1920, Height = 1080 };
        var project = new Project { Id = "p", Name = "Velocidad", Sequences = [sequence] };
        var track = new Track { Id = "v0", Kind = TrackKind.Video, Name = "Video 1" };
        sequence.VideoTracks.Add(track);
        var clips = new Clip[SeekSpecs.Length];
        var rates = new Rational[SeekSpecs.Length];
        Time start = Time.Zero;
        for (int k = 0; k < SeekSpecs.Length; k++)
        {
            GeneratedMedia media = generator.GetOrCreate(SeekSpecs[k]);
            var reference = new MediaRef { Id = $"m{k}", Path = media.Path };
            MediaProbe.Probe(media.Path).ApplyTo(reference);
            project.Media.Add(reference);
            rates[k] = SeekSpecs[k].FrameRate;

            Time sourceIn = Time.FromSeconds(1.3 + k);
            var clip = new Clip { Id = $"c{k}", MediaId = reference.Id, Start = start, SourceIn = sourceIn, SourceOut = sourceIn + Time.FromSeconds(1) };
            if (name == "congelado")
            {
                SpeedClips.Frozen(clip, sourceIn + Time.FromTicks(k * 7_000_003), Time.FromSeconds(2));
            }
            else
            {
                Rational rate = SpeedClips.Parse(name);
                SpeedClips.WithRate(clip, rate, sourceIn, sourceIn + Time.FromSeconds(2 * rate.ToDouble()),
                    k == 1 ? Time.FromTicks(987_654_321) : Time.Zero);
            }

            track.Clips.Add(clip);
            clips[k] = clip;
            start = clip.End;
        }

        Assert.Empty(ModelValidator.Validate(project));
        using GpuDevice gpu = GpuDevice.Create();
        using var backend = new DeviceClockBackend();
        using TimelineSession session = new(project, "sec", gpu, new TimelineSessionOptions(), backend);

        int seeks = Skip.LongRuns ? 2000 : 40;
        var rng = new Random(20261011);
        var failures = new List<string>();
        for (int s = 0; s < seeks; s++)
        {
            long frame = rng.NextInt64(session.FrameCount);
            session.SeekToFrame(frame);
            using CompositionFrame shown = TickUntilSettled(session);
            LayerFrame layer = Assert.Single(shown.Layers);
            int k = Array.FindIndex(clips, c => c.Id == layer.ClipId);
            long expected = SpeedClips.Expected(clips[k], rates[k], shown.TimelineTime);
            LumaImage luma = FrameReadback.ReadLuma(layer.Frame);
            int? code = FrameCode.Read(luma.Pixels, luma.Width, luma.Height, luma.Width);
            if (layer.Frame.Index != expected || code != expected)
            {
                failures.Add($"cuadro {frame} ({layer.ClipId}): índice {layer.Frame.Index}, código {code?.ToString(CultureInfo.InvariantCulture) ?? "ilegible"}, esperado {expected}");
            }
        }

        output.WriteLine($"{name}: {seeks} seeks, {seeks - failures.Count} exactos, seeks de keyframe {session.Stats.DecoderSeeks}.");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(20)));
        Assert.Equal(0, session.Stats.DecodeErrors);
    }

    [HardwareTheory(VideoCodec.H264)]
    [InlineData(2)]
    [InlineData(4)]
    [Trait("Categoria", "TiempoReal")]
    public void Playing_1080p_at_2x_and_4x_without_proxy_drops_no_frames(int speed)
    {
        GeneratedMedia media = TestMediaGenerator.CreateDefault()!.GetOrCreate(
            new MediaSpec("velocidad-h264-1080p-45s", 1920, 1080, Rational.Fps30, TimeSpan.FromSeconds(45)) { GopLength = 60, BFrames = 2 });
        var sequence = new Sequence { Id = "sec", Name = "Velocidad", FrameRate = SequenceRate, Width = 1920, Height = 1080 };
        var project = new Project { Id = "p", Name = "Velocidad", Sequences = [sequence] };
        var reference = new MediaRef { Id = "m", Path = media.Path };
        MediaProbe.Probe(media.Path).ApplyTo(reference);
        project.Media.Add(reference);
        var track = new Track { Id = "v0", Kind = TrackKind.Video, Name = "Video 1" };
        sequence.VideoTracks.Add(track);
        Time seconds = Time.FromSeconds(10);
        var clip = new Clip { Id = "c", MediaId = "m", Start = Time.Zero, SourceIn = Time.FromSeconds(1), SourceOut = Time.FromSeconds(2) };
        SpeedClips.WithRate(clip, new Rational(speed, 1), clip.SourceIn, clip.SourceIn + Time.FromTicks(seconds.Ticks * speed));
        track.Clips.Add(clip);
        Assert.Empty(ModelValidator.Validate(project));

        using GpuDevice gpu = GpuDevice.Create();
        using var backend = new DeviceClockBackend();
        using TimelineSession session = new(project, "sec", gpu, new TimelineSessionOptions { UseProxies = ProxyUsage.Never }, backend);
        TickUntilSettled(session).Dispose();
        session.ResetStats();

        session.Play();
        Time origin = backend.Clock.Position;
        long lastRefresh = (Time.FromSeconds(9.5).Ticks / Time.FromFrames(1, Rational.Fps60).Ticks) - 2;
        var pacer = new WallClockPacer(60);
        long wrong = 0;
        for (long slot = pacer.Next(); slot <= lastRefresh; slot = pacer.Next())
        {
            backend.Clock.Advance(origin + Time.FromFrames(slot, Rational.Fps60) - backend.Clock.Position);
            using CompositionFrame shown = session.Tick();
            LayerFrame layer = Assert.Single(shown.Layers);
            wrong += layer.Frame.Index == SpeedClips.Expected(clip, Rational.Fps30, shown.TimelineTime) ? 0 : 1;
        }

        TimelineStats stats = session.Stats;
        output.WriteLine($"1080p a {speed}×: mostrados {stats.FramesShown}, perdidos {stats.FramesDropped}, tarde {stats.LateRefreshes}, " +
            $"refrescos con otro cuadro {wrong}, saltados por el bucle {pacer.Skipped}, seeks de keyframe {stats.DecoderSeeks}, " +
            $"decodificadores {stats.DecodersOpen}, caché {stats.CacheFrames} cuadros.");
        // Si el propio bucle se atrasó (máquina cargada), los cuadros de los refrescos que saltó no son del motor: a 30 fps
        // de secuencia y 60 Hz, como mucho uno por cada dos refrescos saltados. El decodificador no se atrasa nunca.
        Assert.Equal(0, stats.LateRefreshes);
        Assert.InRange(stats.FramesDropped, 0, (pacer.Skipped + 1) / 2);
        Assert.Equal(0, stats.DecodeErrors);
    }

    [HardwareFact(VideoCodec.H264)]
    public void Playing_at_100x_shows_the_previous_keyframe_read_from_the_file()
    {
        MediaSpec spec = SeekSpecs[2];
        GeneratedMedia media = TestMediaGenerator.CreateDefault()!.GetOrCreate(spec);
        var sequence = new Sequence { Id = "sec", Name = "Velocidad", FrameRate = SequenceRate, Width = 1920, Height = 1080 };
        var project = new Project { Id = "p", Name = "Velocidad", Sequences = [sequence] };
        var reference = new MediaRef { Id = "m", Path = media.Path };
        MediaProbe.Probe(media.Path).ApplyTo(reference);
        project.Media.Add(reference);
        var track = new Track { Id = "v0", Kind = TrackKind.Video, Name = "Video 1" };
        sequence.VideoTracks.Add(track);
        var clip = new Clip { Id = "c", MediaId = "m", Start = Time.Zero, SourceIn = Time.FromSeconds(1), SourceOut = Time.FromSeconds(2) };
        SpeedClips.WithRate(clip, new Rational(100, 1), Time.FromSeconds(1), Time.FromSeconds(201));
        track.Clips.Add(clip);

        using GpuDevice gpu = GpuDevice.Create();
        using var backend = new DeviceClockBackend();
        using TimelineSession session = new(project, "sec", gpu, new TimelineSessionOptions { UseProxies = ProxyUsage.Never }, backend);
        TickUntilSettled(session).Dispose();
        session.ResetStats();

        // El reloj avanza a mano de a un refresco y cada refresco espera a que la capa muestre su cuadro: lo que se
        // comprueba es qué cuadro elige el motor, no si la máquina llega a tiempo. Mientras el keyframe llega, la capa
        // puede seguir mostrando un cuadro anterior (eso es un refresco tarde, no un cuadro equivocado).
        session.Play();
        Time origin = backend.Clock.Position;
        var keyframes = KeyframeIndex.Read(media.Path);
        long first = SpeedClips.Expected(clip, spec.FrameRate, Time.Zero);
        var wrong = new List<string>();
        int unreadable = 0;
        for (long slot = 1; slot <= 110; slot++)
        {
            backend.Clock.Advance(origin + Time.FromFrames(slot, Rational.Fps60) - backend.Clock.Position);
            var watch = Stopwatch.StartNew();
            while (true)
            {
                using CompositionFrame shown = session.Tick();
                LayerFrame layer = Assert.Single(shown.Layers);
                long index = layer.Frame.Index;
                long exact = SpeedClips.Expected(clip, spec.FrameRate, shown.TimelineTime);
                long keyframe = Math.Max(first, keyframes.AtOrBefore(exact) ?? first);

                // El keyframe anterior o, hasta que se leen los keyframes del archivo, el exacto.
                if (index == keyframe || index == exact)
                {
                    LumaImage luma = FrameReadback.ReadLuma(layer.Frame);
                    unreadable += FrameCode.Read(luma.Pixels, luma.Width, luma.Height, luma.Width) == index ? 0 : 1;
                    break;
                }

                if (index > exact || watch.Elapsed > TimeSpan.FromSeconds(5))
                {
                    wrong.Add($"refresco {slot}: cuadro {index}, exacto {exact}, keyframe {keyframe}");
                    break;
                }

                Thread.Sleep(1);
            }
        }

        TimelineStats stats = session.Stats;
        output.WriteLine($"100×: aproximados {stats.Approximated}, mostrados {stats.FramesShown}, perdidos {stats.FramesDropped}, " +
            $"tarde {stats.LateRefreshes}, seeks de keyframe {stats.DecoderSeeks}, keyframes cada {keyframes.MaxInterval} cuadros como mucho.");
        Assert.Equal(0, unreadable);
        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
        Assert.True(stats.Approximated > 0);
        Assert.Equal(0, stats.FramesDropped);
        Assert.Equal(0, stats.DecodeErrors);

        // En pausa vuelve al cuadro exacto.
        session.Pause();
        session.Tick().Dispose();
        Thread.Sleep(500);
        using CompositionFrame exactFrame = session.Tick();
        Assert.Equal(SpeedClips.Expected(clip, spec.FrameRate, exactFrame.TimelineTime), Assert.Single(exactFrame.Layers).Frame.Index);
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
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "La capa no llegó a su cuadro en 10 s.");
            Thread.Sleep(1);
        }
    }
}
