using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Rodaje.Playback.Composition;
using Xunit.Abstractions;
using static Rodaje.Engine.Tests.Support.Seconds;

namespace Rodaje.Engine.Tests.Timeline;

/// <summary>Curvas de velocidad en el motor de video (fase 5c.2): el cuadro de cada instante, la demanda y el pico real.</summary>
public sealed class TimelineCurveTests(ITestOutputHelper output)
{
    private static readonly Rational Fps30 = Rational.Fps30;

    public static TheoryData<SpeedPreset> Presets => [.. SpeedPresets.All];

    [Theory]
    [MemberData(nameof(Presets))]
    public void Every_sequence_frame_maps_to_the_frame_of_an_independent_integer_reference(SpeedPreset preset)
    {
        foreach (Rational sourceRate in new[] { Fps30, Rational.Fps24, Rational.Ntsc30, Rational.Fps60 })
        {
            var b = new TimelineBuilder(1, 0);
            b.AddMedia("m", sourceRate, 2_000_000);
            // Como la segunda mitad de un clip dividido: la curva empieza 1,3 s antes del clip.
            Clip clip = SpeedClips.WithCurve(b.AddVideo(0, "m", F(17, Fps30), S(3.5), S(1)), preset, S(6), S(3.5), S(9.5), S(1.3));
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
                    Time at = plan.ShownAt(i);
                    Assert.True(plan.FrameAt(at) >= i);
                    Assert.True(at == plan.Start || plan.FrameAt(at - Time.FromTicks(1)) < i);
                }
            }

            for (long f = 17; Time.FromFrames(f, Fps30) < clip.End; f += 7)
            {
                long from = plan.FrameAt(Time.FromFrames(f, Fps30));
                Assert.Equal(shown.Count(x => x > from && x < from + 90), plan.ShownBetween(from, from + 90));
            }
        }
    }

    [Fact]
    public void Two_thousand_seeks_into_bullet_clips_show_the_exact_frame()
    {
        var backend = new FakeTimelineBackend { ManualDecoding = true, KeyframeInterval = 30 };
        Rational[] rates = [Fps30, Rational.Fps24, Rational.Ntsc30];
        var b = new TimelineBuilder(1, 0);
        var clips = new Clip[3];
        Time start = Time.Zero;
        for (int k = 0; k < 3; k++)
        {
            b.AddMedia($"m{k}", rates[k], 900_000, backend: backend);
            clips[k] = SpeedClips.WithCurve(b.AddVideo(0, $"m{k}", start, S(2 + k), S(1), $"c{k}"), SpeedPreset.Bullet, S(5), S(2 + k), S(11 + k),
                k == 1 ? Time.FromTicks(987_654_321) : Time.Zero);
            start = clips[k].End;
        }

        var time = new ManualTimeProvider();
        using TimelineSession session = TimelineSessionTests.Open(b, backend, new TimelineSessionOptions { EnableAudio = false, TimeProvider = time });
        var rng = new Random(2026);
        int wrong = 0;
        for (int seek = 0; seek < 2000; seek++)
        {
            session.SeekToFrame(rng.NextInt64(session.FrameCount));
            using CompositionFrame frame = Settle(session, backend);
            LayerFrame layer = Assert.Single(frame.Layers);
            int k = Array.FindIndex(clips, c => c.Id == layer.ClipId);
            wrong += layer.Frame.Index == SpeedClips.Expected(clips[k], rates[k], frame.TimelineTime) ? 0 : 1;
        }

        Assert.Equal(0, wrong);
        Assert.Equal(0, session.Stats.Approximated);
    }

    [Fact]
    public void Playing_montage_curves_shows_every_due_frame_and_decodes_only_those()
    {
        var backend = new FakeTimelineBackend { ManualDecoding = true };
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Fps30, 30_000, backend: backend);
        Clip first = SpeedClips.WithCurve(b.AddVideo(0, "m", Time.Zero, S(1), S(1), "a"), SpeedPreset.Montage, S(3), S(1), S(6.3));
        Clip second = SpeedClips.WithCurve(b.AddVideo(0, "m", first.End, S(100), S(1), "b"), SpeedPreset.Montage, S(3), S(100), S(105.3));
        Clip[] clips = [first, second];
        var time = new ManualTimeProvider();
        using TimelineSession session = TimelineSessionTests.Open(b, backend, new TimelineSessionOptions { EnableAudio = false, TimeProvider = time });
        Settle(session, backend).Dispose();
        session.ResetStats();

        session.Play();
        int wrong = 0;
        int refreshes = (int)(second.End.ToSeconds() * 60) - 6;
        for (int refresh = 0; refresh < refreshes; refresh++)
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

        var shownFrames = new HashSet<long>();
        foreach (Clip clip in clips)
        {
            long firstGrid = clip.Start.ToFrames(Fps30) + (Time.FromFrames(clip.Start.ToFrames(Fps30), Fps30) < clip.Start ? 1 : 0);
            for (long f = firstGrid; Time.FromFrames(f, Fps30) < clip.End; f++)
            {
                shownFrames.Add(SpeedClips.Expected(clip, Fps30, Time.FromFrames(f, Fps30)));
            }
        }

        TimelineStats stats = session.Stats;
        long[] requested = [.. backend.Videos.SelectMany(v => v.Requests)];
        output.WriteLine($"Montaje: mostrados {stats.FramesShown}, perdidos {stats.FramesDropped}, tarde {stats.LateRefreshes}, pedidos {requested.Length}");
        Assert.Equal(0, wrong);
        Assert.Equal(0, stats.FramesDropped);
        Assert.Equal(0, stats.LateRefreshes);
        Assert.Equal((1L, 1L), (stats.Cuts, stats.PreRollHits));
        Assert.All(requested, i => Assert.Contains(i, shownFrames));
    }

    [Fact]
    public void Pre_roll_asks_for_what_the_first_frames_of_a_curve_will_show()
    {
        var backend = new FakeTimelineBackend { ManualDecoding = true };
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("a", Fps30, 3000, backend: backend);
        b.AddMedia("b", Fps30, 3000, backend: backend);
        b.AddVideo(0, "a", Time.Zero, Time.Zero, S(2), "a");
        Clip next = SpeedClips.WithCurve(b.AddVideo(0, "b", S(2), S(10), S(1), "b"), SpeedPreset.FastIn, S(4), S(10), S(20));
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

        // FastIn empieza a 4×: los primeros cuadros de la secuencia muestran uno de cada cuatro del origen.
        long[] expected = [.. Enumerable.Range(0, 6).Select(f => SpeedClips.Expected(next, Fps30, S(2) + F(f, Fps30))).Distinct()];
        Assert.Equal(expected, backend.Videos.Single(v => v.Path.EndsWith("b.mp4", StringComparison.Ordinal)).Requests);
        Assert.True(expected[1] - expected[0] >= 3, $"El pre-roll pidió {string.Join(", ", expected)}.");
    }

    [Fact]
    public void The_plan_takes_the_real_peak_of_a_curve_for_proxies_and_the_minimum_for_approximating()
    {
        var b = new TimelineBuilder(2, 0);
        b.AddMedia("m", Fps30, 900_000);
        Clip peaked = b.AddVideo(0, "m", Time.Zero, S(1), S(1));
        peaked.Speed = new ClipSpeed { Curve = new SpeedCurve { Span = S(4), Points = [new(0, 100), new(5000, 800), new(10_000, 100)] } };
        ClipTimeMap.Fit(peaked.Start, S(1), S(17), peaked.Speed).ApplyTo(peaked);
        SpeedClips.WithRate(b.AddVideo(1, "m", Time.Zero, S(1), S(1)), new Rational(5, 2), S(1), S(11));
        IReadOnlyList<VideoLayerPlan> layers = b.Plan().Layers;
        VideoClipPlan curve = layers[0].Clips[0];
        VideoClipPlan constant = layers[1].Clips[0];

        Assert.Equal(8.0, curve.MaxRate);
        Assert.Equal(2.5, constant.MaxRate);
        Assert.Equal(1.0, curve.MinRate(curve.Start, curve.End), 2);
        Assert.InRange(curve.MinRate(S(1.5), S(2.5)), 4.4, 8);
        Assert.Equal(2.5, constant.MinRate(constant.Start, constant.End));
    }

    private static CompositionFrame Settle(TimelineSession session, FakeTimelineBackend backend)
    {
        for (int i = 0; i < 20; i++)
        {
            backend.RunDecoders();
            CompositionFrame frame = session.Tick();
            if (!session.IsSeekPending)
            {
                return frame;
            }

            frame.Dispose();
        }

        throw new Xunit.Sdk.XunitException("Las capas no llegaron a su cuadro.");
    }
}
