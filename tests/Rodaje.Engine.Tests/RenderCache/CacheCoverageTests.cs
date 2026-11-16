using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.RenderCache;
using Rodaje.Engine.Tests.Offline;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Tests.Timeline;
using Rodaje.Engine.Timeline;
using Rodaje.Media.RenderCache;
using Rodaje.Playback.Composition;
using Xunit.Abstractions;
using static Rodaje.Engine.Tests.Support.Seconds;

namespace Rodaje.Engine.Tests.RenderCache;

public sealed class CacheCoverageTests(ITestOutputHelper output)
{
    private const string FragmentA = @"C:\render\a.mov";
    private const string FragmentB = @"C:\render\b.mov";

    [Fact]
    public void Lo_que_queda_fuera_de_los_tramos_cubiertos()
    {
        (Time, Time)[] covered = [(S(1), S(2)), (S(3), S(4))];
        Assert.Equal([(S(0), S(1)), (S(2), S(3)), (S(4), S(5))], CacheCoverage.Pieces(S(0), S(5), covered));
        Assert.Equal([(S(2), S(2.5))], CacheCoverage.Pieces(S(1.5), S(2.5), covered));
        Assert.Empty(CacheCoverage.Pieces(S(3), S(4), covered));
        Assert.Equal([(S(5), S(6))], CacheCoverage.Pieces(S(5), S(6), covered));
    }

    [Fact]
    public void Agrupa_los_aciertos_seguidos_y_limita_las_aperturas()
    {
        var store = new FakeStore();
        RenderCacheKey[] a = store.Add(FragmentA, 60);
        RenderCacheKey[] b = store.Add(FragmentB, 60);
        var keys = new RenderCacheKey?[200];
        for (int i = 0; i < 60; i++)
        {
            keys[10 + i] = a[i];
        }

        keys[75] = b[0];
        keys[80] = b[5];
        for (int i = 20; i < 60; i++)
        {
            keys[100 + i] = b[i];
        }

        CacheCoverage coverage = CacheCoverage.Find(3, keys, store, Rational.Fps30);

        Assert.Equal(3, coverage.Revision);
        Assert.Equal([new CachedSpan(10, 60, FragmentA, 0), new CachedSpan(120, 40, FragmentB, 20)], coverage.Spans);
    }

    [Fact]
    public void Cada_cuadro_se_ve_entero_desde_el_cache_o_entero_en_vivo_tras_2000_seeks()
    {
        var backend = new FakeTimelineBackend();
        var content = new FakeEffectContent();
        TimelineBuilder b = OfflineProjects.Full(Rational.Fps30, backend, content);
        backend.Register(FragmentA, Samples.Info(Rational.Fps30, 60, audio: false) with { Path = FragmentA });
        backend.Register(FragmentB, Samples.Info(Rational.Fps30, 60, audio: false) with { Path = FragmentB });
        var options = new TimelineSessionOptions { Content = content, TimeProvider = new ManualTimeProvider() };
        using TimelineSession live = TimelineSessionTests.Open(b, backend, options);
        using TimelineSession cached = TimelineSessionTests.Open(b, backend, options);

        // Un tramo que empieza dentro de una transición y otro que corta un clip por la mitad.
        CachedSpan[] spans = [new(5, 40, FragmentA, 10), new(100, 37, FragmentB, 0)];
        Assert.True(cached.ApplyCoverage(new CacheCoverage(cached.PlanRevision, spans)));
        Assert.True(cached.HasCoverage);

        var random = new Random(15);
        long count = live.FrameCount;
        var borders = spans.SelectMany(s => new[] { s.FirstFrame - 1, s.FirstFrame, s.EndFrame - 1, s.EndFrame }).ToArray();
        int fromCache = 0;
        for (int i = 0; i < 2000; i++)
        {
            long f = i % 4 == 0 ? borders[random.Next(borders.Length)] : random.Next((int)count);
            Check(live, cached, spans, f, ref fromCache);
        }

        for (long f = 0; f < count; f++)
        {
            Check(live, cached, spans, f, ref fromCache);
        }

        output.WriteLine($"{2000 + count} comprobaciones, {fromCache} desde el caché.");
        Assert.True(fromCache > 300);
    }

    [Fact]
    public void Reproducir_cruzando_cache_y_vivo_no_llega_tarde()
    {
        (TimelineStats live, int liveWrong) = PlayThrough(withCoverage: false);
        (TimelineStats cached, int cachedWrong) = PlayThrough(withCoverage: true);

        output.WriteLine($"vivo: tarde {live.LateRefreshes}, perdidos {live.FramesDropped}; con caché: tarde {cached.LateRefreshes}, " +
            $"perdidos {cached.FramesDropped}, pre-roll {cached.PreRollHits}/{cached.PreRollMisses}");
        Assert.Equal(0, liveWrong);
        Assert.Equal(0, cachedWrong);
        Assert.Equal(0, cached.LateRefreshes);
        Assert.Equal(0, cached.PreRollMisses);
    }

    private static (TimelineStats Stats, int Wrong) PlayThrough(bool withCoverage)
    {
        var backend = new FakeTimelineBackend();
        var content = new FakeEffectContent();
        TimelineBuilder b = OfflineProjects.Full(Rational.Fps30, backend, content);
        backend.Register(FragmentA, Samples.Info(Rational.Fps30, 60, audio: false) with { Path = FragmentA });
        backend.Register(FragmentB, Samples.Info(Rational.Fps30, 60, audio: false) with { Path = FragmentB });
        var time = new ManualTimeProvider();
        using TimelineSession session = TimelineSessionTests.Open(b, backend,
            new TimelineSessionOptions { Content = content, TimeProvider = time, EnableAudio = false });
        CachedSpan[] spans = withCoverage ? [new(5, 40, FragmentA, 10), new(100, 37, FragmentB, 0)] : [];
        Assert.True(session.ApplyCoverage(new CacheCoverage(session.PlanRevision, spans)));
        for (int i = 0; i < 20 && session.IsSeekPending; i++)
        {
            backend.RunDecoders();
            session.Tick().Dispose();
        }

        session.ResetStats();
        session.Play();
        int wrong = 0;
        Time until = session.Duration - S(0.2);
        while (session.Position < until)
        {
            time.Advance(TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 60));
            using (CompositionFrame frame = session.Tick())
            {
                long f = frame.TimelineTime.ToFrames(Rational.Fps30);
                if (spans.FirstOrDefault(s => f >= s.FirstFrame && f < s.EndFrame) is { MoviePath: not null } span
                    && (frame.Layers.Count != 1 || frame.Layers[0].Frame.Index != span.FirstFragmentFrame + (f - span.FirstFrame)))
                {
                    wrong++;
                }
            }

            backend.RunDecoders();
        }

        return (session.Stats, wrong);
    }

    [Fact]
    public void Editar_retira_la_cobertura_y_una_cobertura_vieja_no_entra()
    {
        var backend = new FakeTimelineBackend();
        var content = new FakeEffectContent();
        TimelineBuilder b = OfflineProjects.Full(Rational.Fps30, backend, content);
        backend.Register(FragmentA, Samples.Info(Rational.Fps30, 60, audio: false) with { Path = FragmentA });
        using TimelineSession session = TimelineSessionTests.Open(b, backend,
            new TimelineSessionOptions { Content = content, TimeProvider = new ManualTimeProvider() });
        var old = new CacheCoverage(session.PlanRevision, [new CachedSpan(0, 30, FragmentA, 0)]);
        Assert.True(session.ApplyCoverage(old));

        session.Reload(b.Project);

        Assert.False(session.HasCoverage);
        Assert.False(session.ApplyCoverage(old));
        session.SeekToFrame(10);
        using CompositionFrame frame = Settle(session);
        Assert.DoesNotContain(frame.Layers, l => l.ClipId.StartsWith(TimelinePlan.CacheTrackId, StringComparison.Ordinal));
        Assert.True(session.ApplyCoverage(new CacheCoverage(session.PlanRevision, [])));
        Assert.False(session.HasCoverage);
    }

    private static void Check(TimelineSession live, TimelineSession cached, CachedSpan[] spans, long f, ref int fromCache)
    {
        if (f < 0 || f >= live.FrameCount)
        {
            return;
        }

        cached.SeekToFrame(f);
        using CompositionFrame actual = Settle(cached);
        if (spans.FirstOrDefault(s => f >= s.FirstFrame && f < s.EndFrame) is { MoviePath: not null } span)
        {
            LayerFrame layer = Assert.Single(actual.Layers);
            var payload = Assert.IsType<FakeFramePayload>(layer.Frame.Payload);
            Assert.Equal(span.MoviePath, payload.Path);
            Assert.Equal(span.FirstFragmentFrame + (f - span.FirstFrame), layer.Frame.Index);
            Assert.True(layer.IsPlain && layer.Transform == LayerTransform.Identity, $"cuadro {f}");
            fromCache++;
            return;
        }

        live.SeekToFrame(f);
        using CompositionFrame expected = Settle(live);
        Assert.Equal(OfflineProjects.Describe(expected), OfflineProjects.Describe(actual));
    }

    private static CompositionFrame Settle(TimelineSession session)
    {
        for (int i = 0; ; i++)
        {
            CompositionFrame frame = session.Tick();
            if (!session.IsSeekPending)
            {
                return frame;
            }

            frame.Dispose();
            Assert.True(i < 20, "La vista previa no llegó a la composición completa.");
        }
    }

    private sealed class FakeStore : IRenderCacheStore
    {
        private readonly Dictionary<RenderCacheKey, RenderCacheHit> _hits = [];
        private ulong _next;

        public string Directory => @"C:\render";

        public int Count => _hits.Count;

        public RenderCacheKey[] Add(string movie, int frames)
        {
            var keys = new RenderCacheKey[frames];
            for (int i = 0; i < frames; i++)
            {
                keys[i] = new RenderCacheKey(0, ++_next);
            }

            var fragment = new RenderCacheFragment(movie, movie + ".json",
                new RenderCacheFragmentMetadata(1920, 1080, Rational.Fps30, SequenceColorSpace.Sdr709, "prores_hq", false, keys, 1));
            for (int i = 0; i < frames; i++)
            {
                _hits[keys[i]] = new RenderCacheHit(fragment, i);
            }

            return keys;
        }

        public bool TryGet(RenderCacheKey key, RenderCacheHit? previous, out RenderCacheHit hit) => _hits.TryGetValue(key, out hit);

        public string CreateTemporaryPath() => throw new NotSupportedException();

        public RenderCacheFragment Commit(string temporaryMoviePath, RenderCacheFragmentMetadata metadata) => throw new NotSupportedException();

        public void Forget(RenderCacheFragment fragment) => throw new NotSupportedException();

        public long TotalBytes() => 0;

        public RenderCacheCleanupReport Cleanup(RenderCacheCleanupOptions? options = null) => throw new NotSupportedException();
    }
}
