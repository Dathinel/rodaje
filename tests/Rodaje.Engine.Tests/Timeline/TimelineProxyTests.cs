using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Rodaje.Media.Probe;
using Rodaje.Media.Proxies;
using Rodaje.Playback.Composition;
using static Rodaje.Engine.Tests.Support.Seconds;

namespace Rodaje.Engine.Tests.Timeline;

/// <summary>La vista previa decodifica el proxy cuando está listo, lo coloca con la geometría del original y cambia sin cortes.</summary>
public sealed class TimelineProxyTests : IDisposable
{
    private static readonly Time Refresh60 = Time.FromFrames(1, Rational.Fps60);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rodaje-pruebas", "proxies-" + Guid.NewGuid().ToString("N"));
    private readonly ProxyStore _store;
    private readonly FakeTimelineBackend _backend;

    public TimelineProxyTests()
    {
        Directory.CreateDirectory(_directory);
        _store = new ProxyStore(Path.Combine(_directory, "almacen"));
        _backend = new FakeTimelineBackend { ManualDecoding = true };
    }

    [Fact]
    public void A_ready_proxy_is_decoded_and_placed_with_the_size_of_the_original()
    {
        (TimelineBuilder b, string original) = Timeline(3840, 2160);
        ProxyEntry proxy = CommitProxy(original);
        using var resolver = new MediaSourceResolver(_store, generator: null);
        using TimelineSession session = Open(b, resolver, ProxyUsage.Always);
        Settle(session);

        using CompositionFrame frame = session.Tick();
        LayerFrame layer = Assert.Single(frame.Layers);
        Assert.Equal(proxy.ProxyPath, ((FakeFramePayload)layer.Frame.Payload!).Path);
        Assert.True(layer.IsProxy);
        Assert.Equal((3840, 2160), (layer.DisplayWidth, layer.DisplayHeight));
        Assert.Equal((new Rational(1, 1), 0), (layer.SampleAspectRatio, layer.Rotation));
        Assert.True(session.Stats.Layers[0].UsesProxy);
        Assert.Equal(1, session.Stats.ProxyDecoders);
    }

    [Theory]
    [InlineData(ProxyUsage.Auto, 3840, 2160, true)]
    [InlineData(ProxyUsage.Auto, 1920, 1080, false)]
    [InlineData(ProxyUsage.Always, 1920, 1080, true)]
    [InlineData(ProxyUsage.Never, 3840, 2160, false)]
    public void The_usage_option_decides_between_proxy_and_original(ProxyUsage usage, int width, int height, bool expectProxy)
    {
        (TimelineBuilder b, string original) = Timeline(width, height);
        ProxyEntry proxy = CommitProxy(original);
        using var resolver = new MediaSourceResolver(_store, generator: null);
        using TimelineSession session = Open(b, resolver, usage);
        Settle(session);

        using CompositionFrame frame = session.Tick();
        string decoded = ((FakeFramePayload)Assert.Single(frame.Layers).Frame.Payload!).Path;
        Assert.Equal(expectProxy ? proxy.ProxyPath : original, decoded);
    }

    [Fact]
    public void Playback_switches_to_a_proxy_that_becomes_ready_without_a_late_refresh()
    {
        (TimelineBuilder b, string original) = Timeline(3840, 2160);
        using var resolver = new MediaSourceResolver(_store, generator: null);
        using TimelineSession session = Open(b, resolver, ProxyUsage.Auto);
        Settle(session);
        session.Play();
        PlayFor(session, S(1));
        Assert.Equal(original, ShownPath(session));

        ProxyEntry proxy = CommitProxy(original);
        // Lo que haría el generador al terminar: el resolvedor ve el proxy y avisa.
        resolver.Resolve(original, MediaPurpose.Preview);
        PlayFor(session, S(1));

        Assert.Equal(proxy.ProxyPath, ShownPath(session));
        TimelineStats stats = session.Stats;
        Assert.Equal(0, stats.LateRefreshes);
        Assert.Equal(0, stats.FramesDropped);
        Assert.Equal(1, stats.DecodersOpen);
        Assert.True(_backend.Videos.Single(v => v.Path == original).Disposed);
    }

    [Fact]
    public void Reload_picks_up_a_proxy_that_appeared_on_disk()
    {
        (TimelineBuilder b, string original) = Timeline(3840, 2160);
        using var resolver = new MediaSourceResolver(_store, generator: null);
        using TimelineSession session = Open(b, resolver, ProxyUsage.Always);
        Settle(session);
        Assert.Equal(original, ShownPath(session));

        ProxyEntry proxy = CommitProxy(original);
        resolver.Forget(original);
        session.Invalidate();
        for (int i = 0; i < 4; i++)
        {
            _backend.RunDecoders();
            session.Tick().Dispose();
        }

        Assert.Equal(proxy.ProxyPath, ShownPath(session));
        Assert.Equal(1, session.Stats.DecodersOpen);
    }

    [Fact]
    public void A_proxy_with_other_frames_than_the_original_is_not_used()
    {
        (TimelineBuilder b, string original) = Timeline(3840, 2160);
        CommitProxy(original, frameCount: 2999);
        using var resolver = new MediaSourceResolver(_store, generator: null);
        using TimelineSession session = Open(b, resolver, ProxyUsage.Always);
        Settle(session);

        Assert.Equal(original, ShownPath(session));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private (TimelineBuilder Builder, string Original) Timeline(int width, int height)
    {
        string original = Path.Combine(_directory, $"original-{width}x{height}.mp4");
        File.WriteAllBytes(original, [.. Enumerable.Range(0, 8192).Select(i => (byte)i)]);
        var b = new TimelineBuilder(1, 0);
        MediaInfo info = Samples.Info(Rational.Fps30, 3000, audio: false, width, height) with { Path = original };
        _backend.Register(original, info);
        b.Project.Media.Add(new MediaRef
        {
            Id = "m",
            Path = original,
            Name = "m",
            Duration = info.Duration,
            HasVideo = true,
            FrameRate = Rational.Fps30,
            Width = width,
            Height = height,
        });
        b.AddVideo(0, "m", Time.Zero, Time.Zero, S(20));
        return (b, original);
    }

    private ProxyEntry CommitProxy(string original, long frameCount = 3000)
    {
        SourceFingerprint source = SourceFingerprint.Compute(original);
        string temporary = _store.CreateTemporaryPath(source, ProxySettings.Default);
        File.WriteAllBytes(temporary, [0, 1, 2, 3]);
        Time duration = Time.FromFrames(3000, Rational.Fps30);
        ProxyEntry entry = _store.Commit(temporary, new ProxyMetadata
        {
            SourcePath = source.Path,
            SourceKey = source.Key,
            SourceSize = source.Size,
            SourceLastWriteUtc = source.LastWriteUtc,
            SettingsTag = ProxySettings.Default.Tag,
            TargetHeight = 540,
            GopLength = 15,
            Encoder = "prueba",
            Width = 960,
            Height = 540,
            FrameRateNum = 30,
            FrameRateDen = 1,
            FrameCount = frameCount,
            SourceFrameCount = 3000,
            SourceStartTicks = 0,
            ProxyStartTicks = 0,
            SourceDurationTicks = duration.Ticks,
            ProxyDurationTicks = duration.Ticks,
            CreatedUtc = DateTime.UtcNow,
        }, ProxySettings.Default);
        MediaInfo info = Samples.Info(Rational.Fps30, 3000, audio: false, 960, 540) with { Path = entry.ProxyPath };
        _backend.Register(entry.ProxyPath, info);
        return entry;
    }

    private TimelineSession Open(TimelineBuilder b, MediaSourceResolver resolver, ProxyUsage usage) =>
        TimelineSessionTests.Open(b, _backend, new TimelineSessionOptions { ProxyResolver = resolver, UseProxies = usage });

    private static string ShownPath(TimelineSession session)
    {
        using CompositionFrame frame = session.Tick();
        return ((FakeFramePayload)Assert.Single(frame.Layers).Frame.Payload!).Path;
    }

    private void Settle(TimelineSession session)
    {
        for (int i = 0; i < 10 && session.IsSeekPending; i++)
        {
            _backend.RunDecoders();
            session.Tick().Dispose();
        }

        Assert.False(session.IsSeekPending);
    }

    private void PlayFor(TimelineSession session, Time span)
    {
        for (Time played = Time.Zero; played < span; played += Refresh60)
        {
            _backend.Clock.Advance(Refresh60);
            session.Tick().Dispose();
            _backend.RunDecoders();
        }
    }
}
