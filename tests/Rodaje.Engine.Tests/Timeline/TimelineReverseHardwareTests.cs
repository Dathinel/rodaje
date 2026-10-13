using System.Diagnostics;
using System.Globalization;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Rodaje.Media.Gpu;
using Rodaje.Media.Probe;
using Rodaje.Media.Proxies;
using Rodaje.Media.Video;
using Rodaje.Playback.Composition;
using Rodaje.TestMedia;
using Xunit.Abstractions;
using DeviceClockBackend = Rodaje.Engine.Tests.Timeline.TimelineHardwareTests.DeviceClockBackend;

namespace Rodaje.Engine.Tests.Timeline;

/// <summary>
/// Reversa con FFmpeg y D3D11VA reales sobre archivos con el número de cuadro dibujado: seeks exactos, 1080p a 1× y 2×
/// hacia atrás sin proxy, y 4K hacia atrás con su proxy, sin pérdidas y dentro del presupuesto de VRAM.
/// </summary>
[Collection(GpuHardwareGroup.Name)]
public sealed class TimelineReverseHardwareTests(ITestOutputHelper output) : IDisposable
{
    private static readonly MediaSpec Hd = new("velocidad-h264-1080p-45s", 1920, 1080, Rational.Fps30, TimeSpan.FromSeconds(45))
    {
        GopLength = 60,
        BFrames = 2,
    };

    private static readonly MediaSpec Uhd = new("reversa-hevc-2160p-30", 3840, 2160, Rational.Fps30, TimeSpan.FromSeconds(14))
    {
        Codec = VideoCodec.Hevc,
        GopLength = 30,
        BFrames = 2,
    };

    private readonly string _store = Path.Combine(Path.GetTempPath(), "rodaje-pruebas", "proxies-reversa-" + Guid.NewGuid().ToString("N"));

    [HardwareFact(VideoCodec.H264)]
    public void Seeks_in_reverse_show_the_frame_drawn_in_the_image()
    {
        (Project project, Clip clip) = ReverseProject(Hd, new Rational(3, 2), Time.FromSeconds(5), Time.FromSeconds(20));
        using GpuDevice gpu = GpuDevice.Create();
        using var backend = new DeviceClockBackend();
        using TimelineSession session = new(project, "sec", gpu, new TimelineSessionOptions { UseProxies = ProxyUsage.Never }, backend);
        int seeks = Skip.LongRuns ? 400 : 40;
        var rng = new Random(20261012);
        var failures = new List<string>();
        for (int s = 0; s < seeks; s++)
        {
            session.SeekToFrame(rng.NextInt64(session.FrameCount));
            using CompositionFrame shown = TickUntilSettled(session);
            LayerFrame layer = Assert.Single(shown.Layers);
            long expected = SpeedClips.Expected(clip, Hd.FrameRate, shown.TimelineTime);
            LumaImage luma = FrameReadback.ReadLuma(layer.Frame);
            int? code = FrameCode.Read(luma.Pixels, luma.Width, luma.Height, luma.Width);
            if (layer.Frame.Index != expected || code != expected)
            {
                failures.Add($"{shown.TimelineTime}: índice {layer.Frame.Index}, código {code?.ToString(CultureInfo.InvariantCulture) ?? "ilegible"}, esperado {expected}");
            }
        }

        output.WriteLine($"Reversa a 1,5×: {seeks} seeks, {seeks - failures.Count} exactos.");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures.Take(20)));
        Assert.Equal(0, session.Stats.DecodeErrors);
    }

    /// <summary>Criterio de la fase 5c: reversa 1080p a 1× sin proxy con 0 cuadros perdidos (y 2× como margen).</summary>
    [HardwareTheory(VideoCodec.H264)]
    [InlineData(1)]
    [InlineData(2)]
    [Trait("Categoria", "TiempoReal")]
    public void Playing_1080p_in_reverse_without_proxy_drops_no_frames(int speed)
    {
        (Project project, Clip clip) = ReverseProject(Hd, new Rational(speed, 1), Time.FromSeconds(2), Time.FromSeconds(2 + (10 * speed)));
        using GpuDevice gpu = GpuDevice.Create();
        using var backend = new DeviceClockBackend();
        using TimelineSession session = new(project, "sec", gpu, new TimelineSessionOptions { UseProxies = ProxyUsage.Never }, backend);
        (TimelineStats stats, long wrong, long skipped) = PlayInRealTime(session, backend, clip, Hd.FrameRate, Time.FromSeconds(9.5));

        output.WriteLine($"1080p a {speed}× hacia atrás: mostrados {stats.FramesShown}, perdidos {stats.FramesDropped}, tarde {stats.LateRefreshes}, " +
            $"refrescos con otro cuadro {wrong}, saltados por el bucle {skipped}, seeks de keyframe {stats.DecoderSeeks}, " +
            $"decodificadores {stats.DecodersOpen}, caché {stats.CacheFrames} cuadros, VRAM pico {stats.VramPeakBytes / 1048576} de {stats.VramBudgetBytes / 1048576} MB.");
        Assert.Equal(0, stats.LateRefreshes);
        Assert.InRange(stats.FramesDropped, 0, (skipped + 1) / 2);
        Assert.Equal(0, stats.DecodeErrors);
        Assert.True(stats.VramPeakBytes <= stats.VramBudgetBytes, "La reversa pasó del presupuesto de VRAM.");
    }

    /// <summary>
    /// Criterio de la fase 5c: reversa 4K con proxy, 0 cuadros perdidos. En Auto la política exige el proxy (reversa por
    /// encima de 1080p) y lo genera aunque no se generen solos los recomendados; mientras no está, el original se lee por
    /// pasadas. Después la reproducción va del proxy, con el número de cada cuadro comprobado.
    /// </summary>
    [HardwareFact(VideoCodec.Hevc, VideoCodec.H264, LongRunning = true)]
    [Trait("Categoria", "TiempoReal")]
    public void Playing_4k_in_reverse_from_its_required_proxy_drops_no_frames()
    {
        using PreviewProxies proxies = PreviewProxies.Create(_store, generate: false);
        Assert.True(proxies.CanGenerate, proxies.UnavailableReason);
        (Project project, Clip clip) = ReverseProject(Uhd, new Rational(1, 1), Time.FromSeconds(1), Time.FromSeconds(13));
        using GpuDevice gpu = GpuDevice.Create();
        using var backend = new DeviceClockBackend();
        using var session = new TimelineSession(project, "sec", gpu,
            new TimelineSessionOptions { ProxyResolver = proxies.Resolver, UseProxies = ProxyUsage.Auto }, backend);

        // Sin proxy todavía: el original 4K hacia atrás, exacto aunque lento.
        session.SeekToFrame(30);
        using (CompositionFrame first = TickUntilSettled(session))
        {
            LayerFrame layer = Assert.Single(first.Layers);
            Assert.False(layer.IsProxy);
            Assert.Equal(SpeedClips.Expected(clip, Uhd.FrameRate, first.TimelineTime), layer.Frame.Index);
        }

        var watch = Stopwatch.StartNew();
        while (proxies.Resolver.GetStatus(Path.GetFullPath(project.Media[0].Path)).State != ProxyState.Ready)
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(120), "El proxy obligatorio no se generó.");
            session.Tick().Dispose();
            Thread.Sleep(20);
        }

        output.WriteLine($"Proxy obligatorio listo en {watch.Elapsed.TotalSeconds:F1} s.");
        session.SeekToFrame(0);
        while (true)
        {
            using CompositionFrame frame = TickUntilSettled(session);
            if (frame.Layers is [{ IsProxy: true }])
            {
                break;
            }

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(150), "La vista previa no pasó al proxy.");
            Thread.Sleep(20);
            session.SeekToFrame(0);
        }

        (TimelineStats stats, long wrong, long skipped) = PlayInRealTime(session, backend, clip, Uhd.FrameRate, Time.FromSeconds(11), readCodes: true);
        output.WriteLine($"4K hacia atrás con proxy: mostrados {stats.FramesShown}, perdidos {stats.FramesDropped}, tarde {stats.LateRefreshes}, " +
            $"cuadros distintos o ilegibles {wrong}, saltados por el bucle {skipped}, decodificadores {stats.DecodersOpen} (proxy {stats.ProxyDecoders}), " +
            $"VRAM pico {stats.VramPeakBytes / 1048576} de {stats.VramBudgetBytes / 1048576} MB, desbordes {stats.VramOverruns}.");
        Assert.Equal(0, wrong);
        Assert.Equal(0, stats.LateRefreshes);
        Assert.InRange(stats.FramesDropped, 0, (skipped + 1) / 2);
        Assert.Equal(1, stats.ProxyDecoders);
        Assert.True(stats.VramPeakBytes <= stats.VramBudgetBytes, "La reversa pasó del presupuesto de VRAM.");
        Assert.Equal(0, stats.DecodeErrors);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_store, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Un clip en reversa que recorre <c>[sourceIn, sourceOut)</c> del medio, al principio de la secuencia.</summary>
    private static (Project Project, Clip Clip) ReverseProject(MediaSpec spec, Rational rate, Time sourceIn, Time sourceOut)
    {
        GeneratedMedia media = TestMediaGenerator.CreateDefault()!.GetOrCreate(spec);
        var sequence = new Sequence { Id = "sec", Name = "Reversa", FrameRate = Rational.Fps30, Width = spec.Width, Height = spec.Height };
        var project = new Project { Id = "p", Name = "Reversa", Sequences = [sequence] };
        var reference = new MediaRef { Id = "m", Path = media.Path };
        MediaProbe.Probe(media.Path).ApplyTo(reference);
        project.Media.Add(reference);
        var track = new Track { Id = "v0", Kind = TrackKind.Video, Name = "Video 1" };
        sequence.VideoTracks.Add(track);
        var clip = new Clip { Id = "c", MediaId = "m", Start = Time.Zero, SourceIn = sourceIn, SourceOut = sourceOut };
        SpeedClips.Reversed(clip, rate, sourceIn, sourceOut);
        track.Clips.Add(clip);
        Assert.Empty(ModelValidator.Validate(project));
        return (project, clip);
    }

    /// <summary>Reproduce con el reloj de pared a 60 Hz desde el principio; cuenta los refrescos con un cuadro distinto del debido.</summary>
    private static (TimelineStats Stats, long Wrong, long Skipped) PlayInRealTime(TimelineSession session, DeviceClockBackend backend, Clip clip,
        Rational sourceRate, Time length, bool readCodes = false)
    {
        session.SeekToFrame(0);
        TickUntilSettled(session).Dispose();
        session.ResetStats();
        session.Play();
        Time origin = backend.Clock.Position;
        long lastRefresh = (length.Ticks / Time.FromFrames(1, Rational.Fps60).Ticks) - 2;
        var pacer = new WallClockPacer(60);
        long wrong = 0;
        long lastChecked = -1;
        for (long slot = pacer.Next(); slot <= lastRefresh; slot = pacer.Next())
        {
            backend.Clock.Advance(origin + Time.FromFrames(slot, Rational.Fps60) - backend.Clock.Position);
            using CompositionFrame shown = session.Tick();
            LayerFrame layer = Assert.Single(shown.Layers);
            long expected = SpeedClips.Expected(clip, sourceRate, shown.TimelineTime);
            wrong += layer.Frame.Index == expected ? 0 : 1;
            if (readCodes && layer.Frame.Index != lastChecked)
            {
                LumaImage luma = FrameReadback.ReadLuma(layer.Frame);
                wrong += FrameCode.Read(luma.Pixels, luma.Width, luma.Height, luma.Width) == layer.Frame.Index ? 0 : 1;
                lastChecked = layer.Frame.Index;
            }
        }

        session.Pause();
        return (session.Stats, wrong, pacer.Skipped);
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
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), "La capa no llegó a su cuadro en 20 s.");
            Thread.Sleep(1);
        }
    }
}
