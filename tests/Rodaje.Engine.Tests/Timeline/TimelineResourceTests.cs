using System.Diagnostics;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Rodaje.Playback.Composition;
using static Rodaje.Engine.Tests.Support.Seconds;

namespace Rodaje.Engine.Tests.Timeline;

/// <summary>Decodificadores por medio, presupuesto de VRAM, cierres fuera del hilo de la ventana, medios rotos y audio en ediciones.</summary>
public sealed class TimelineResourceTests
{
    private static readonly Time Refresh60 = Time.FromFrames(1, Rational.Fps60);

    /// <summary>Una superficie NV12 de 4K con el relleno de D3D11VA (3840 × 2176).</summary>
    private const long Surface4K = 3840L * 2176 * 3 / 2;

    [Fact]
    public void The_same_media_on_four_tracks_two_seconds_apart_gets_one_decoder_per_track_that_never_seeks()
    {
        var backend = new FakeTimelineBackend { ManualDecoding = true };
        var b = new TimelineBuilder(4, 0);
        b.AddMedia("m", Rational.Fps30, 3000, backend: backend);
        for (int k = 0; k < 4; k++)
        {
            b.AddVideo(k, "m", Time.Zero, S(2 * k), S(10));
        }

        using TimelineSession session = TimelineSessionTests.Open(b, backend);
        Settle(session, backend);
        session.Play();
        long wrong = 0;
        for (int refresh = 0; refresh < 300; refresh++)
        {
            backend.Clock.Advance(Refresh60);
            using (CompositionFrame frame = session.Tick())
            {
                long sequenceFrame = frame.TimelineTime.ToFrames(Rational.Fps30);
                wrong += frame.Layers.Count(l => l.Frame.Index != sequenceFrame + (60 * l.TrackIndex));
            }

            backend.RunDecoders();
        }

        TimelineStats stats = session.Stats;
        Assert.Equal(0, stats.LateRefreshes);
        Assert.Equal(0, wrong);
        Assert.Equal(4, stats.DecodersOpen);
        foreach (FakeMediaVideo video in backend.Videos)
        {
            // Cada decodificador avanza de a un cuadro: ninguna búsqueda de keyframe para ir y volver entre capas.
            IReadOnlyList<long> requests = video.Requests;
            Assert.All(requests.Zip(requests.Skip(1)), pair => Assert.Equal(pair.First + 1, pair.Second));
        }
    }

    [Fact]
    public void With_four_4k_tracks_and_cuts_the_pools_stay_within_the_vram_budget_closing_idle_decoders_first()
    {
        // Unos 1,5 GB: lo que corresponde a una GPU de 4 GB. Cada pool de 4K HEVC: 7 referencias, 5 retenidas, 3 de NVDEC y
        // la caché. Ocho medios: en cada corte las cuatro pistas pasan a medios que no tienen decodificador.
        long budget = 124 * Surface4K;
        var backend = new FakeTimelineBackend { PoolSurfaceBytes = Surface4K, PoolReferences = 7 };
        TimelineBuilder b = CutsTimeline(backend, media: 8, segments: 6);
        var time = new ManualTimeProvider();
        using TimelineSession session = TimelineSessionTests.Open(b, backend, new TimelineSessionOptions { VramBudgetBytes = budget, TimeProvider = time });
        session.Tick().Dispose();
        session.Play();
        long worst = 0;
        while (session.Position < S(17.5))
        {
            backend.Clock.Advance(Refresh60);
            session.Tick().Dispose();
            worst = Math.Max(worst, session.Stats.VramCommittedBytes);
        }

        TimelineStats stats = session.Stats;
        Assert.True(worst <= budget, $"Se reservaron {worst / Surface4K} superficies de {budget / Surface4K}.");
        Assert.True(stats.VramPeakBytes <= budget, $"Pico de {stats.VramPeakBytes / Surface4K} superficies de {budget / Surface4K}.");
        Assert.Equal(0, stats.VramOverruns);
        Assert.Equal(budget, stats.VramBudgetBytes);
        // Seis pools de 4K caben en el presupuesto: cuatro capas y dos pre-roll. En cada corte entran cuatro medios nuevos,
        // así que dos llegan a tiempo y los otros tardan un refresco; nada más llega tarde.
        Assert.True(stats.PreRollHits >= stats.Cuts / 2, $"Pre-roll {stats.PreRollHits} de {stats.Cuts}.");
        Assert.Equal(stats.PreRollMisses, stats.LateAtCuts);
        Assert.Equal(stats.LateAtCuts, stats.LateRefreshes);
        // El reloj de inactividad nunca avanzó: todo lo cerrado se cerró para dejar lugar.
        Assert.True(stats.DecodersClosed > 0, "Ningún decodificador se cerró para dejar lugar.");
        // Con 4K la caché de cada decodificador baja de 16 a lo que cabe.
        Assert.All(backend.Videos, v => Assert.InRange(v.ExtraSurfaces, TimelineScheduler.HeldSurfaces + TimelineScheduler.MinCacheFrames,
            TimelineScheduler.HeldSurfaces + 8));
    }

    [Fact]
    public void With_room_to_spare_each_decoder_gets_all_the_cache_it_can_use()
    {
        var backend = new FakeTimelineBackend { PoolSurfaceBytes = 1920L * 1088 * 3 / 2 };
        var b = new TimelineBuilder(2, 0);
        b.AddMedia("a", Rational.Fps30, 3000, backend: backend);
        b.AddMedia("b", Rational.Fps30, 3000, backend: backend);
        b.AddVideo(0, "a", Time.Zero, Time.Zero, S(4));
        b.AddVideo(1, "b", Time.Zero, Time.Zero, S(4));
        using TimelineSession session = TimelineSessionTests.Open(b, backend);
        using CompositionFrame frame = session.Tick();

        Assert.Equal(2, frame.Layers.Count);
        // Lo adelantado (8), el pre-roll (6) y dos de margen; más no se usaría.
        Assert.All(backend.Videos, v => Assert.Equal(TimelineScheduler.HeldSurfaces + 16, v.ExtraSurfaces));
        Assert.Equal(2 * (10 + TimelineScheduler.HeldSurfaces + TimelineScheduler.OverheadSurfaces + 16) * (1920L * 1088 * 3 / 2),
            session.Stats.VramCommittedBytes);
    }

    [Fact]
    public void A_visible_layer_without_a_decoder_gets_one_even_past_the_budget_and_it_is_counted()
    {
        var backend = new FakeTimelineBackend { PoolSurfaceBytes = Surface4K };
        var b = new TimelineBuilder(2, 0);
        (b.AddMedia("a", Rational.Fps30, 3000, backend: backend).Width, b.Project.Media[0].Height) = (3840, 2160);
        (b.AddMedia("b", Rational.Fps30, 3000, backend: backend).Width, b.Project.Media[1].Height) = (3840, 2160);
        b.AddVideo(0, "a", Time.Zero, Time.Zero, S(4));
        b.AddVideo(1, "b", Time.Zero, Time.Zero, S(4));
        using TimelineSession session = TimelineSessionTests.Open(b, backend, new TimelineSessionOptions { VramBudgetBytes = 25 * Surface4K });
        using CompositionFrame frame = session.Tick();

        Assert.Equal(2, frame.Layers.Count);
        Assert.Equal(1, session.Stats.VramOverruns);
    }

    [Fact]
    public void Many_open_decoders_close_after_a_quarter_of_the_idle_timeout()
    {
        var time = new ManualTimeProvider();
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(1, 0);
        foreach (string id in new[] { "a", "b", "c", "d" })
        {
            b.AddMedia(id, Rational.Fps30, 3000, backend: backend);
        }

        b.AddVideo(0, "a", Time.Zero, Time.Zero, S(1));
        b.AddVideo(0, "b", S(1), Time.Zero, S(1));
        b.AddVideo(0, "c", S(2), Time.Zero, S(1));
        b.AddVideo(0, "d", S(3), Time.Zero, S(1));
        using TimelineSession session = TimelineSessionTests.Open(b, backend, new TimelineSessionOptions { TimeProvider = time, PreRoll = TimeSpan.Zero });
        foreach (double at in new[] { 0.5, 1.5, 2.5, 3.5 })
        {
            session.Seek(S(at));
            session.Tick().Dispose();
        }

        Assert.Equal(4, session.Stats.DecodersOpen);
        time.Advance(TimeSpan.FromSeconds(1.5));
        session.Tick().Dispose();

        Assert.Equal((1, 3), (session.Stats.DecodersOpen, session.Stats.DecodersClosed));
    }

    [Fact]
    public void Tick_never_waits_for_a_decoder_that_is_closing()
    {
        var time = new ManualTimeProvider();
        var backend = new FakeTimelineBackend { RealThreads = true, DisposeDelay = TimeSpan.FromMilliseconds(300) };
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("a", Rational.Fps30, 3000, backend: backend);
        b.AddMedia("b", Rational.Fps30, 3000, backend: backend);
        b.AddVideo(0, "a", Time.Zero, Time.Zero, S(1));
        b.AddVideo(0, "b", S(1), Time.Zero, S(1));
        TimelineSession session = TimelineSessionTests.Open(b, backend, new TimelineSessionOptions { TimeProvider = time, PreRoll = TimeSpan.Zero });
        TickUntilSettled(session);
        session.Seek(S(1.5));
        TickUntilSettled(session);
        Assert.Equal(2, session.Stats.DecodersOpen);

        time.Advance(TimeSpan.FromSeconds(6));
        double slowest = 0;
        for (int i = 0; i < 20; i++)
        {
            long start = Stopwatch.GetTimestamp();
            session.Tick().Dispose();
            slowest = Math.Max(slowest, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        Assert.True(slowest < 2, $"Un Tick tardó {slowest:0.0} ms mientras se cerraba un decodificador.");
        Assert.Equal(1, session.Stats.DecodersClosed);
        FakeMediaVideo closing = backend.Videos.Single(v => v.Path.EndsWith("a.mp4", StringComparison.Ordinal));
        var wait = Stopwatch.StartNew();
        while (!closing.Disposed && wait.Elapsed < TimeSpan.FromSeconds(5))
        {
            Thread.Sleep(5);
        }

        Assert.True(closing.Disposed);
        session.Dispose();
        Assert.All(backend.Videos, v => Assert.True(v.Disposed));
    }

    [Fact]
    public void A_clip_whose_media_does_not_open_leaves_its_layer_empty_instead_of_freezing_the_previous_clip()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("ok", Rational.Fps30, 3000, backend: backend);
        b.AddMedia("roto", Rational.Fps30, 3000, backend: backend);
        backend.FailOpen.Add(TimelineBuilder.PathOf("roto"));
        b.AddVideo(0, "ok", Time.Zero, Time.Zero, S(1), "antes");
        b.AddVideo(0, "roto", S(1), Time.Zero, S(1), "roto");
        var failures = new List<TimelineDecodeFailedEventArgs>();
        using TimelineSession session = TimelineSessionTests.Open(b, backend);
        session.DecodeFailed += (_, e) => failures.Add(e);
        session.Tick().Dispose();
        session.Play();
        while (session.Position < S(1.5))
        {
            backend.Clock.Advance(Refresh60);
            using CompositionFrame frame = session.Tick();
            if (session.Position >= S(1))
            {
                Assert.Empty(frame.Layers);
            }
        }

        TimelineStats stats = session.Stats;
        LayerStats layer = Assert.Single(stats.Layers);
        Assert.NotNull(layer.Failure);
        Assert.Equal(1, stats.FailedMedia);
        Assert.Equal("roto", Assert.Single(failures).MediaId);
    }

    [Fact]
    public void A_clip_whose_frames_do_not_decode_leaves_its_layer_empty_after_the_cut()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("ok", Rational.Fps30, 3000, backend: backend);
        b.AddMedia("danado", Rational.Fps30, 3000, backend: backend);
        for (long i = 0; i < 60; i++)
        {
            backend.FailFrames.Add((TimelineBuilder.PathOf("danado"), i));
        }

        b.AddVideo(0, "ok", Time.Zero, Time.Zero, S(1), "antes");
        b.AddVideo(0, "danado", S(1), Time.Zero, S(1), "danado");
        using TimelineSession session = TimelineSessionTests.Open(b, backend);
        session.Seek(S(0.9));
        session.Tick().Dispose();

        session.Seek(S(1.2));
        using CompositionFrame frame = session.Tick();
        Assert.Empty(frame.Layers);
        Assert.False(session.IsSeekPending);
    }

    [Fact]
    public void Transform_edits_do_not_rewind_the_audio_mix_and_cause_no_underruns()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(1, 1);
        b.AddMedia("m", Rational.Fps30, 3000, backend: backend);
        Clip video = b.AddVideo(0, "m", Time.Zero, Time.Zero, S(20));
        Clip audio = b.AddAudio(0, "m", Time.Zero, Time.Zero, S(20));
        using TimelineSession session = TimelineSessionTests.Open(b, backend);
        session.Tick().Dispose();
        session.Play();
        WaitForFullMix(session);
        int seeksBefore = backend.Pcms.Sum(p => p.Seeks);
        var buffer = new float[480 * 2];
        for (int edit = 0; edit < 20; edit++)
        {
            video.Effects.RemoveAll(e => e.Type == BuiltInEffects.Transform);
            video.Effects.Add(new EffectInstance
            {
                Id = $"t{edit}",
                Type = BuiltInEffects.Transform,
                Parameters = new() { [BuiltInEffects.TransformParameters.X] = new Parameter { Value = edit * 10 } },
            });
            session.Invalidate();
            backend.Output!.Source.Read(buffer);
            backend.Clock.Advance(S(0.01));
            session.Tick().Dispose();
            Thread.Sleep(10);
        }

        Assert.Equal(seeksBefore, backend.Pcms.Sum(p => p.Seeks));
        Assert.Equal(0, session.Stats.Audio.Underruns);

        // Una edición de audio sí rehace la mezcla por delante.
        audio.Start = S(0.5);
        session.Invalidate();
        var wait = Stopwatch.StartNew();
        while (backend.Pcms.Sum(p => p.Seeks) == seeksBefore && wait.Elapsed < TimeSpan.FromSeconds(5))
        {
            Thread.Sleep(5);
        }

        Assert.True(backend.Pcms.Sum(p => p.Seeks) > seeksBefore);
    }

    /// <summary>Pista k, tramo j: medio <c>(4j + k) % media</c>; en cada corte unos medios son nuevos y otros cambian de pista.</summary>
    internal static TimelineBuilder CutsTimeline(FakeTimelineBackend backend, int media, int segments)
    {
        var b = new TimelineBuilder(4, 0);
        for (int m = 0; m < media; m++)
        {
            MediaRef reference = b.AddMedia($"m{m}", Rational.Fps30, 3000, backend: backend);
            (reference.Width, reference.Height) = (3840, 2160);
        }

        for (int k = 0; k < 4; k++)
        {
            for (int j = 0; j < segments; j++)
            {
                b.AddVideo(k, $"m{((4 * j) + k) % media}", S(3 * j), S(((j * 37) + (k * 53)) % 140 / 10.0), S(3));
            }
        }

        return b;
    }

    private static void Settle(TimelineSession session, FakeTimelineBackend backend)
    {
        for (int i = 0; i < 10 && session.IsSeekPending; i++)
        {
            backend.RunDecoders();
            session.Tick().Dispose();
        }

        Assert.False(session.IsSeekPending);
    }

    private static void TickUntilSettled(TimelineSession session)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            session.Tick().Dispose();
            if (!session.IsSeekPending)
            {
                return;
            }

            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "Las capas no llegaron a su cuadro.");
            Thread.Sleep(1);
        }
    }

    private static void WaitForFullMix(TimelineSession session)
    {
        var watch = Stopwatch.StartNew();
        while (session.BufferedAudioFrames < 48_000 * 4 / 10 && watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            Thread.Sleep(5);
        }

        Assert.True(session.BufferedAudioFrames >= 48_000 * 4 / 10, "La mezcla no se llenó.");
    }
}
