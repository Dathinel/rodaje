using System.Diagnostics;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Rodaje.Playback.Composition;
using Xunit.Abstractions;
using static Rodaje.Engine.Tests.Support.Seconds;

namespace Rodaje.Engine.Tests.Timeline;

/// <summary>Velocidad en el motor de video (fase 5c.1): el plan con el mapeo, la demanda en tiempo de la secuencia y el seek exacto.</summary>
public sealed class TimelineSpeedTests(ITestOutputHelper output)
{
    private static readonly Rational Fps30 = Rational.Fps30;
    private static readonly Time Refresh60 = Time.FromFrames(1, Rational.Fps60);

    public static TheoryData<string> Cases => [.. SpeedClips.Cases];

    public static TheoryData<string> Rates => [.. SpeedClips.Cases.Where(c => c != "congelado")];

    [Theory]
    [MemberData(nameof(Rates))]
    public void Every_sequence_frame_maps_to_the_frame_of_an_independent_integer_reference(string name)
    {
        Rational rate = SpeedClips.Parse(name);
        foreach (Rational sourceRate in new[] { Fps30, Rational.Fps24, Rational.Ntsc30, Rational.Fps60 })
        {
            var b = new TimelineBuilder(1, 0);
            b.AddMedia("m", sourceRate, 2_000_000);
            Clip clip = SpeedClips.WithRate(b.AddVideo(0, "m", F(17, Fps30), S(3.5), S(1)), rate, S(3.5),
                S(3.5) + Time.FromSeconds(4 * rate.ToDouble()), Time.FromTicks(123_456_789));
            VideoClipPlan plan = b.Plan().Layers[0].Clips[0];

            var shown = new SortedSet<long>();
            for (long f = 17; Time.FromFrames(f, Fps30) < clip.End; f++)
            {
                Time t = Time.FromFrames(f, Fps30);
                long expected = SpeedClips.Expected(clip, sourceRate, t);
                Assert.Equal(expected, plan.FrameAt(t));
                shown.Add(expected);
            }

            Assert.Equal((shown.Min, shown.Max), (plan.FirstFrame, plan.LastFrame));
            for (long i = shown.Min - 2; i <= shown.Max + 2; i++)
            {
                SortedSet<long> after = shown.GetViewBetween(i, long.MaxValue);
                Assert.Equal(after.Count > 0 ? after.Min : long.MaxValue, plan.NextShown(i));
                Assert.Equal(shown.Contains(i), plan.IsShown(i));
                if (shown.Contains(i))
                {
                    // El primer cuadro de la secuencia que lo muestra empieza en o después de ShownAt.
                    Time at = plan.ShownAt(i);
                    Assert.True(plan.FrameAt(at) >= i);
                    Assert.True(at == plan.Start || plan.FrameAt(at - Time.FromTicks(1)) < i);
                }
            }

            long[] list = [.. shown];
            var rng = new Random(5);
            for (int k = 0; k < 50; k++)
            {
                long from = list[rng.Next(list.Length)];
                long to = from + rng.Next(1, 400);
                Assert.Equal(shown.Count(x => x > from && x < to), plan.ShownBetween(from, to));
            }
        }
    }

    [Fact]
    public void A_frozen_clip_shows_one_frame_and_its_effects_run_on_a_virtual_time()
    {
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Fps30, 3000);
        Clip clip = SpeedClips.Frozen(b.AddVideo(0, "m", S(2), S(5), S(1)), S(5) + Time.FromTicks(3), S(3));
        EffectInstance transform = TimelineEffectsTests.Effect(BuiltInEffects.Transform);
        transform.Parameters[BuiltInEffects.TransformParameters.X] = new Parameter
        {
            Keyframes = [new Keyframe(clip.SourceIn, 0), new Keyframe(clip.SourceIn + S(2), 100)],
        };
        clip.Effects.Add(transform);
        VideoClipPlan plan = b.Plan().Layers[0].Clips[0];

        Assert.Equal((150L, 150L), (plan.FirstFrame, plan.LastFrame));
        Assert.All(Enumerable.Range(60, 90), f => Assert.Equal(150, plan.FrameAt(F(f, Fps30))));
        Assert.True(plan.IsShown(150));
        Assert.False(plan.IsShown(151));
        Assert.Equal((1L, 0L), (plan.ShownBetween(140, 160), plan.ShownBetween(150, 160)));
        Assert.Equal(50, EffectPlan.Transform(plan.Effects, plan.EffectTimeAt(S(3))).X, 6);
        Assert.Equal(100, EffectPlan.Transform(plan.Effects, plan.EffectTimeAt(S(4.5))).X, 6);
    }

    [Fact]
    public void Effect_keyframes_follow_the_source_so_a_fade_at_2x_takes_half_the_time()
    {
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Fps30, 3000);
        Clip clip = SpeedClips.WithRate(b.AddVideo(0, "m", S(5), S(1), S(1)), new Rational(2, 1), S(1), S(9));
        EffectInstance transform = TimelineEffectsTests.Effect(BuiltInEffects.Transform);
        transform.Parameters[BuiltInEffects.TransformParameters.Opacity] = new Parameter
        {
            Keyframes = [new Keyframe(S(1), 0), new Keyframe(S(2), 1)],
        };
        clip.Effects.Add(transform);
        VideoClipPlan plan = b.Plan().Layers[0].Clips[0];

        Assert.Equal(S(9), clip.End);
        Assert.Equal(0.5, EffectPlan.Transform(plan.Effects, plan.EffectTimeAt(S(5.25))).Opacity, 6);
        Assert.Equal(1, EffectPlan.Transform(plan.Effects, plan.EffectTimeAt(S(5.5))).Opacity, 6);
    }

    [Fact]
    public void Transition_material_runs_at_the_edge_rate_and_freezes_where_the_media_ends()
    {
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Fps30, 450);
        Clip a = SpeedClips.WithRate(b.AddVideo(0, "m", Time.Zero, S(2), S(1), "a"), new Rational(2, 1), S(2), S(6));
        Clip c = SpeedClips.WithRate(b.AddVideo(0, "m", S(2), S(4), S(1), "c"), new Rational(1, 2), S(4), S(5));
        TimelineEffectsTests.AddTransition(b, BuiltInTransitions.Crossfade, "a", "c", S(1));
        TimelinePlan plan = b.Plan();
        VideoClipPlan outgoing = plan.Layers[0].Clips[0];
        VideoClipPlan incoming = plan.Layers[0].Companion!.Clips[0];

        Assert.Equal((Time.Zero, S(2.5)), (outgoing.Start, outgoing.End));
        Assert.Equal(S(6.5).ToFrames(Fps30), outgoing.FrameAt(S(2.25)));
        Assert.Equal(208, outgoing.LastFrame);
        Assert.Equal((S(1.5), S(2.5)), (incoming.Start, incoming.End));
        Assert.Equal(S(3.75).ToFrames(Fps30), incoming.FrameAt(S(1.5)));
        Assert.Equal(incoming.FirstFrame, incoming.FrameAt(S(1.5)));

        // Con un medio de 7 s, lo que pasa del material se congela en su último cuadro.
        b.Project.Media[0].Duration = S(6.6);
        VideoClipPlan clamped = b.Plan().Layers[0].Clips[0];
        Assert.Equal(S(6.6).ToFrames(Fps30) - 1, clamped.FrameAt(S(2.49)));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Two_thousand_seeks_show_the_exact_frame(string name)
    {
        var backend = new FakeTimelineBackend { ManualDecoding = true, KeyframeInterval = 30 };
        (TimelineBuilder b, Clip[] clips, Rational[] rates) = SpeedProject(name, backend);
        var time = new ManualTimeProvider();
        using TimelineSession session = TimelineSessionTests.Open(b, backend, new TimelineSessionOptions { EnableAudio = false, TimeProvider = time });

        var rng = new Random(2026);
        int wrong = 0;
        for (int seek = 0; seek < 2000; seek++)
        {
            session.SeekToFrame(rng.NextInt64(session.FrameCount));
            using CompositionFrame frame = Settle(session, backend);
            foreach (LayerFrame layer in frame.Layers)
            {
                int k = Array.FindIndex(clips, c => c.Id == layer.ClipId);
                if (layer.Frame.Index != SpeedClips.Expected(clips[k], rates[k], frame.TimelineTime))
                {
                    wrong++;
                }
            }

            Assert.Single(frame.Layers);
        }

        Assert.Equal(0, wrong);
        Assert.Equal(0, session.Stats.Approximated);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void Playing_at_2x_and_4x_shows_every_due_frame_and_decodes_only_those(int speed)
    {
        var backend = new FakeTimelineBackend { ManualDecoding = true };
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Fps30, 30_000, backend: backend);
        var rate = new Rational(speed, 1);
        Clip first = SpeedClips.WithRate(b.AddVideo(0, "m", Time.Zero, S(1), S(1), "a"), rate, S(1), S(1 + (3 * speed)));
        Clip second = SpeedClips.WithRate(b.AddVideo(0, "m", S(3), S(100), S(1), "b"), rate, S(100), S(100 + (3 * speed)));
        Clip[] clips = [first, second];
        var time = new ManualTimeProvider();
        using TimelineSession session = TimelineSessionTests.Open(b, backend, new TimelineSessionOptions { EnableAudio = false, TimeProvider = time });
        Settle(session, backend).Dispose();
        session.ResetStats();

        session.Play();
        int wrong = 0;
        var shownFrames = new HashSet<long>();
        for (int refresh = 0; refresh < 340; refresh++)
        {
            time.Advance(TimeSpan.FromTicks(166_667));
            using (CompositionFrame frame = session.Tick())
            {
                LayerFrame layer = Assert.Single(frame.Layers);
                Clip clip = clips.Single(c => c.Id == layer.ClipId);
                wrong += layer.Frame.Index == SpeedClips.Expected(clip, Fps30, frame.TimelineTime) ? 0 : 1;
            }

            backend.RunDecoders();
        }

        foreach (Clip clip in clips)
        {
            for (Time t = clip.Start; t < clip.End; t += F(1, Fps30))
            {
                shownFrames.Add(SpeedClips.Expected(clip, Fps30, t));
            }
        }

        TimelineStats stats = session.Stats;
        long[] requested = [.. backend.Videos.SelectMany(v => v.Requests)];
        output.WriteLine($"{speed}×: mostrados {stats.FramesShown}, perdidos {stats.FramesDropped}, tarde {stats.LateRefreshes}, pedidos {requested.Length}, caché {stats.CacheFrames}");
        Assert.Equal(0, wrong);
        Assert.Equal(0, stats.FramesDropped);
        Assert.Equal(0, stats.LateRefreshes);
        Assert.Equal((1L, 1L), (stats.Cuts, stats.PreRollHits));
        Assert.All(requested, i => Assert.Contains(i, shownFrames));
    }

    [Theory]
    [InlineData(1, 4, new long[] { 300, 301 })]
    [InlineData(4, 1, new long[] { 300, 304, 308, 312, 316, 320 })]
    public void Pre_roll_asks_for_what_the_first_sequence_frames_will_show(long num, long den, long[] expected)
    {
        var backend = new FakeTimelineBackend { ManualDecoding = true };
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("a", Fps30, 3000, backend: backend);
        b.AddMedia("b", Fps30, 3000, backend: backend);
        b.AddVideo(0, "a", Time.Zero, Time.Zero, S(2), "a");
        SpeedClips.WithRate(b.AddVideo(0, "b", S(2), S(10), S(1), "b"), new Rational(num, den), S(10), S(12));
        var time = new ManualTimeProvider();
        using TimelineSession session = TimelineSessionTests.Open(b, backend, new TimelineSessionOptions { EnableAudio = false, TimeProvider = time });
        Settle(session, backend).Dispose();

        session.Play();
        while (session.Position < S(1.95))
        {
            time.Advance(TimeSpan.FromTicks(166_667));
            session.Tick().Dispose();
            backend.RunDecoders();
        }

        Assert.Equal(expected, backend.Videos.Single(v => v.Path.EndsWith("b.mp4", StringComparison.Ordinal)).Requests);
    }

    [Fact]
    public void From_16x_playback_shows_the_previous_keyframe_and_pausing_returns_to_the_exact_frame()
    {
        var backend = new FakeTimelineBackend { ManualDecoding = true, KeyframeInterval = 30 };
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Fps30, 300_000, backend: backend);
        Clip clip = SpeedClips.WithRate(b.AddVideo(0, "m", Time.Zero, S(7), S(1), "a"), new Rational(50, 1), S(7), S(7 + 500));
        var time = new ManualTimeProvider();
        using TimelineSession session = TimelineSessionTests.Open(b, backend, new TimelineSessionOptions { EnableAudio = false, TimeProvider = time });
        Settle(session, backend).Dispose();
        session.ResetStats();
        int pausedRequests = backend.Videos.Sum(v => v.Requests.Count);

        session.Play();
        int notKeyframe = 0;
        for (int refresh = 0; refresh < 180; refresh++)
        {
            time.Advance(TimeSpan.FromTicks(166_667));
            using (CompositionFrame frame = session.Tick())
            {
                long index = Assert.Single(frame.Layers).Frame.Index;
                long exact = SpeedClips.Expected(clip, Fps30, frame.TimelineTime);
                Assert.InRange(index, exact - 29, exact);
                notKeyframe += index % 30 == 0 || index == 210 ? 0 : 1;
            }

            backend.RunDecoders();
        }

        TimelineStats stats = session.Stats;
        output.WriteLine($"50×: aproximados {stats.Approximated}, mostrados {stats.FramesShown}, perdidos {stats.FramesDropped}, pedidos {backend.Videos.Sum(v => v.Requests.Count)}");
        // A 50× el cuadro exacto avanza 50 por cuadro de la secuencia: es un keyframe uno de cada tres.
        Assert.Equal(0, notKeyframe);
        Assert.InRange(stats.Approximated, 110, 130);
        Assert.Equal(0, stats.FramesDropped);
        Assert.Equal(0, stats.LateRefreshes);
        FakeMediaVideo video = Assert.Single(backend.Videos);
        Assert.All(video.Requests.Skip(pausedRequests), i => Assert.True(i % 30 == 0, $"Se pidió el cuadro {i}."));

        session.Pause();
        using (CompositionFrame paused = Settle(session, backend, untilExact: true))
        {
            Assert.Equal(SpeedClips.Expected(clip, Fps30, paused.TimelineTime), Assert.Single(paused.Layers).Frame.Index);
        }

        // Un seek reproduciendo también llega al cuadro exacto antes de volver a aproximar.
        session.Play();
        session.Seek(S(3) + Time.FromTicks(1));
        using (CompositionFrame afterSeek = Settle(session, backend))
        {
            Assert.Equal(SpeedClips.Expected(clip, Fps30, afterSeek.TimelineTime), Assert.Single(afterSeek.Layers).Frame.Index);
        }
    }

    [Theory]
    [InlineData(10, 30)]
    [InlineData(50, null)]
    public void Below_16x_or_without_keyframes_playback_stays_exact(int speed, int? keyframeInterval)
    {
        var backend = new FakeTimelineBackend { ManualDecoding = true, KeyframeInterval = keyframeInterval };
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Fps30, 300_000, backend: backend);
        Clip clip = SpeedClips.WithRate(b.AddVideo(0, "m", Time.Zero, S(7), S(1)), new Rational(speed, 1), S(7), S(7 + (10 * speed)));
        var time = new ManualTimeProvider();
        using TimelineSession session = TimelineSessionTests.Open(b, backend, new TimelineSessionOptions { EnableAudio = false, TimeProvider = time });
        Settle(session, backend).Dispose();

        session.Play();
        for (int refresh = 0; refresh < 120; refresh++)
        {
            time.Advance(TimeSpan.FromTicks(166_667));
            using (CompositionFrame frame = session.Tick())
            {
                Assert.Equal(SpeedClips.Expected(clip, Fps30, frame.TimelineTime), Assert.Single(frame.Layers).Frame.Index);
            }

            backend.RunDecoders();
        }

        Assert.Equal(0, session.Stats.Approximated);
        Assert.Equal(0, session.Stats.FramesDropped);
    }

    /// <summary>
    /// Con 8 capas con velocidad (una congelada), el <c>Tick</c> en pausa reserva lo mismo que a 1×. Reproduciendo, lo que
    /// reserva el reparto de la demanda depende de cuántos tramos y pedidos haya; la diferencia queda en ruido (≤2 %).
    /// </summary>
    [Fact]
    public void Speed_adds_no_bytes_to_what_each_tick_already_reserves()
    {
        (long plainPaused, long plainPlaying, _) = MeasureTicks(speed: false);
        (long speedPaused, long speedPlaying, _) = MeasureTicks(speed: true);
        output.WriteLine($"MEDIDA tick_bytes_pausa = {plainPaused} (1×) / {speedPaused} (velocidad)");
        output.WriteLine($"MEDIDA tick_bytes_reproduciendo = {plainPlaying} (1×) / {speedPlaying} (velocidad)");

        Assert.Equal(plainPaused, speedPaused);
        Assert.True(speedPlaying <= plainPlaying * 1.02, $"La velocidad suma {speedPlaying - plainPlaying} B por Tick reproduciendo.");
    }

    /// <summary>Lo que el motor calcula por capa y refresco con velocidad (cuadro, efectos, cuadros mostrados) no reserva memoria.</summary>
    [Fact]
    public void Speed_mapping_in_the_plan_reserves_nothing()
    {
        var b = new TimelineBuilder(3, 0);
        b.AddMedia("m", Fps30, 300_000);
        SpeedClips.WithRate(b.AddVideo(0, "m", Time.Zero, S(1), S(1)), new Rational(137, 100), S(1), S(300));
        SpeedClips.WithRate(b.AddVideo(1, "m", Time.Zero, S(1), S(1)), new Rational(100, 1), S(1), S(9000));
        SpeedClips.Frozen(b.AddVideo(2, "m", Time.Zero, S(1), S(1)), S(3), S(100));
        VideoClipPlan[] plans = [.. b.Plan().Layers.Select(l => l.Clips[0])];
        long sum = Exercise(plans, 50);

        long before = GC.GetAllocatedBytesForCurrentThread();
        sum += Exercise(plans, 5_000);
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
        Assert.NotEqual(0, sum);
    }

    private static long Exercise(VideoClipPlan[] plans, int count)
    {
        long sum = 0;
        for (int i = 0; i < count; i++)
        {
            foreach (VideoClipPlan plan in plans)
            {
                Time t = F(i % 2500, Fps30);
                long frame = plan.FrameAt(t);
                sum += frame + plan.NextShown(frame + 1) % 7 + plan.ShownUntil(i + 8) + plan.ShownAt(frame + 3).Ticks % 5
                    + plan.EffectTimeAt(t).Ticks % 3 + (long)plan.RateAt(t);
            }
        }

        return sum;
    }

    [Fact]
    [Trait("Categoria", "TiempoReal")]
    public void Tick_with_speed_clips_stays_under_the_budget()
    {
        (_, _, double medianMs) = MeasureTicks(speed: true);
        output.WriteLine($"MEDIDA tick_mediana_ms_8_capas_velocidad = {medianMs:F3}");
        Assert.True(medianMs <= 0.3, $"Tick con 8 capas con velocidad: {medianMs:F3} ms de mediana.");
    }

    /// <summary>Bytes por <c>Tick</c> en pausa y reproduciendo, y la mediana del tiempo de <c>Tick</c> reproduciendo.</summary>
    private static (long Paused, long Playing, double MedianMs) MeasureTicks(bool speed)
    {
        const int Layers = 8;
        var backend = new FakeTimelineBackend { ManualDecoding = true };
        var b = new TimelineBuilder(Layers, 0);
        for (int i = 0; i < Layers; i++)
        {
            b.AddMedia($"m{i}", Fps30, 300_000, backend: backend);
            Clip clip = b.AddVideo(i, $"m{i}", Time.Zero, S(i), S(600));
            if (speed)
            {
                if (i == Layers - 1)
                {
                    SpeedClips.Frozen(clip, S(i), S(600));
                }
                else
                {
                    SpeedClips.WithRate(clip, i % 2 == 0 ? new Rational(2, 1) : new Rational(137, 100), S(i), S(i + 1200));
                }
            }
        }

        var time = new ManualTimeProvider();
        using TimelineSession session = TimelineSessionTests.Open(b, backend, new TimelineSessionOptions { EnableAudio = false, TimeProvider = time });
        Settle(session, backend).Dispose();
        for (int i = 0; i < 50; i++)
        {
            session.Tick().Dispose();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        const int Ticks = 200;
        for (int i = 0; i < Ticks; i++)
        {
            session.Tick().Dispose();
        }

        long paused = (GC.GetAllocatedBytesForCurrentThread() - before) / Ticks;

        session.Play();
        for (int i = 0; i < 60; i++)
        {
            time.Advance(TimeSpan.FromTicks(166_667));
            session.Tick().Dispose();
            backend.RunDecoders();
        }

        long playing = 0;
        var times = new List<double>();
        for (int i = 0; i < 600; i++)
        {
            time.Advance(TimeSpan.FromTicks(166_667));
            long bytes = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            session.Tick().Dispose();
            times.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            playing += GC.GetAllocatedBytesForCurrentThread() - bytes;
            backend.RunDecoders();
        }

        times.Sort();
        return (paused, playing / 600, times[times.Count / 2]);
    }

    /// <summary>Tres clips seguidos con la velocidad del caso (el del medio con desplazamiento, como tras dividir) en medios de distinta tasa.</summary>
    private static (TimelineBuilder Builder, Clip[] Clips, Rational[] Rates) SpeedProject(string name, FakeTimelineBackend backend)
    {
        Rational[] rates = [Fps30, Rational.Fps24, Rational.Ntsc30];
        var b = new TimelineBuilder(1, 0);
        for (int m = 0; m < rates.Length; m++)
        {
            b.AddMedia($"m{m}", rates[m], 900_000, backend: backend);
        }

        var clips = new Clip[3];
        Time start = Time.Zero;
        for (int k = 0; k < 3; k++)
        {
            Clip clip = b.AddVideo(0, $"m{k}", start, S(2 + k), S(1), $"c{k}");
            if (name == "congelado")
            {
                SpeedClips.Frozen(clip, S(2 + k) + Time.FromTicks(k * 1_000_003), S(3));
            }
            else
            {
                Rational rate = SpeedClips.Parse(name);
                SpeedClips.WithRate(clip, rate, S(2 + k), S(2 + k) + Time.FromSeconds(4 * rate.ToDouble()),
                    k == 1 ? Time.FromTicks(987_654_321) : Time.Zero);
            }

            clips[k] = clip;
            start = clip.End;
        }

        return (b, clips, rates);
    }

    private static CompositionFrame Settle(TimelineSession session, FakeTimelineBackend backend, bool untilExact = false)
    {
        for (int i = 0; i < 20; i++)
        {
            backend.RunDecoders();
            CompositionFrame frame = session.Tick();
            if (!session.IsSeekPending && (!untilExact || i > 2))
            {
                return frame;
            }

            frame.Dispose();
        }

        throw new Xunit.Sdk.XunitException("Las capas no llegaron a su cuadro.");
    }
}
