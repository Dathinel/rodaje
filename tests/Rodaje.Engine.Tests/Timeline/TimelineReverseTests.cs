using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Rodaje.Playback.Composition;
using Xunit.Abstractions;
using static Rodaje.Engine.Tests.Support.Seconds;

namespace Rodaje.Engine.Tests.Timeline;

/// <summary>
/// Reversa en el motor de video (fase 5c.3): el plan cuenta los cuadros que se ven hacia abajo, la capa presenta por el
/// límite superior, cada clip en reversa tiene su decodificador y lo decodifica por GOP desde su keyframe.
/// </summary>
public sealed class TimelineReverseTests(ITestOutputHelper output)
{
    private static readonly Rational Fps30 = Rational.Fps30;

    public static TheoryData<string> Rates => ["1", "2", "0.5", "1.37", "4", "1001/1000"];

    [Theory]
    [MemberData(nameof(Rates))]
    public void The_reverse_plan_matches_an_integer_reference_and_counts_what_is_shown(string name)
    {
        Rational rate = SpeedClips.Parse(name);
        foreach (Rational sourceRate in new[] { Fps30, Rational.Fps24, Rational.Ntsc30, Rational.Fps60 })
        {
            var b = new TimelineBuilder(1, 0);
            b.AddMedia("m", sourceRate, 2_000_000);
            Clip clip = SpeedClips.Reversed(b.AddVideo(0, "m", F(17, Fps30), S(3.5), S(1)), rate, S(3.5),
                S(3.5) + Time.FromSeconds(4 * rate.ToDouble()), Time.FromTicks(123_456_789));
            VideoClipPlan plan = b.Plan().Layers[0].Clips[0];
            Assert.False(plan.Forward);

            for (long f = 17; Time.FromFrames(f, Fps30) < clip.End; f++)
            {
                Time t = Time.FromFrames(f, Fps30);
                Assert.Equal(SpeedClips.Expected(clip, sourceRate, t), plan.FrameAt(t));
            }

            AssertShownHelpers(plan);
        }
    }

    [Fact]
    public void A_crossfade_between_reverse_clips_keeps_the_helpers_exact_in_the_transition_material()
    {
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Fps30, 3000);
        SpeedClips.Reversed(b.AddVideo(0, "m", Time.Zero, S(10), S(1), "a"), new Rational(3, 2), S(10), S(13));
        SpeedClips.Reversed(b.AddVideo(0, "m", S(2), S(30), S(1), "c"), new Rational(1, 2), S(30), S(31));
        TimelineEffectsTests.AddTransition(b, BuiltInTransitions.Crossfade, "a", "c", S(1));
        TimelinePlan plan = b.Plan();
        VideoClipPlan outgoing = plan.Layers[0].Clips[0];
        VideoClipPlan incoming = plan.Layers[0].Companion!.Clips[0];

        // El que sale sigue bajando a 1,5× más allá de su final; el que entra baja a 0,5× desde antes de su inicio.
        Assert.True(outgoing.FrameAt(S(2.4)) < outgoing.FrameAt(S(1.9)));
        Assert.True(incoming.FrameAt(S(1.5)) > incoming.FrameAt(S(2.4)));
        AssertShownHelpers(outgoing);
        AssertShownHelpers(incoming);
        AssertShownHelpers(plan.Layers[0].Clips[1]);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("0.5")]
    [InlineData("1.37")]
    public void Two_thousand_seeks_in_reverse_show_the_exact_frame(string name)
    {
        var backend = new FakeTimelineBackend { ManualDecoding = true, KeyframeInterval = 30 };
        Rational rate = SpeedClips.Parse(name);
        Rational[] rates = [Fps30, Rational.Fps24, Rational.Ntsc30];
        var b = new TimelineBuilder(1, 0);
        var clips = new Clip[3];
        Time start = Time.Zero;
        for (int k = 0; k < 3; k++)
        {
            b.AddMedia($"m{k}", rates[k], 900_000, backend: backend);
            clips[k] = SpeedClips.Reversed(b.AddVideo(0, $"m{k}", start, S(2 + k), S(1), $"c{k}"), rate, S(2 + k),
                S(2 + k) + Time.FromSeconds(4 * rate.ToDouble()), k == 1 ? Time.FromTicks(987_654_321) : Time.Zero);
            start = clips[k].End;
        }

        var time = new ManualTimeProvider();
        using TimelineSession session = TimelineSessionTests.Open(b, backend, new TimelineSessionOptions { EnableAudio = false, TimeProvider = time });
        var rng = new Random(2027);
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
    }

    /// <summary>
    /// Reproduciendo en reversa con keyframes cada 30 o 60 cuadros y el pool de D3D11VA simulado: se ve cada cuadro debido,
    /// con un solo decodificador, y cada GOP se decodifica entero una vez (dos pasadas si no cabe en la caché).
    /// </summary>
    [Theory]
    [InlineData(1, 1, 30, 1.05)]
    [InlineData(1, 1, 60, 1.7)]
    [InlineData(2, 1, 30, 1.15)]
    [InlineData(1, 2, 30, 1.15)]
    [InlineData(4, 1, 60, 1.15)]
    public void Playing_in_reverse_shows_every_due_frame_and_decodes_each_gop_about_once(int num, int den, int gop, double maxCost)
    {
        var backend = new FakeTimelineBackend { ManualDecoding = true, KeyframeInterval = gop, PoolSurfaceBytes = 3_133_440 };
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Fps30, 30_000, backend: backend);
        var rate = new Rational(num, den);
        Clip clip = SpeedClips.Reversed(b.AddVideo(0, "m", Time.Zero, S(100), S(1), "r"), rate, S(100),
            S(100) + Time.FromSeconds(12 * rate.ToDouble()));
        var time = new ManualTimeProvider();
        using TimelineSession session = TimelineSessionTests.Open(b, backend, new TimelineSessionOptions { EnableAudio = false, TimeProvider = time });
        Settle(session, backend).Dispose();
        session.ResetStats();

        (int wrong, long shown) = PlayFor(session, backend, time, S(10), [clip]);

        TimelineStats stats = session.Stats;
        FakeMediaVideo video = Assert.Single(backend.Videos);
        double cost = (double)video.DecodedFrames / (video.Requests.Max() - video.Requests.Min() + 1);
        output.WriteLine($"{num}/{den}× en reversa, GOP {gop}: mostrados {stats.FramesShown}, perdidos {stats.FramesDropped}, tarde " +
            $"{stats.LateRefreshes}, pedidos {video.Requests.Count}, decodificados {video.DecodedFrames} ({cost:F2} por cuadro del tramo recorrido), " +
            $"seeks {video.Seeks}, caché {stats.CacheFrames} cuadros.");
        Assert.Equal(0, wrong);
        Assert.Equal(0, stats.FramesDropped);
        Assert.Equal(0, stats.LateRefreshes);
        Assert.True(shown > 250);
        Assert.InRange(cost, 1, maxCost);
        Assert.All(video.Requests, i => Assert.True(i >= clip.SourceIn.ToFrames(Fps30) && i < clip.SourceOut.ToFrames(Fps30) + 1));
    }

    [Fact]
    public void A_cut_into_a_reverse_clip_arrives_from_its_pre_roll_starting_at_its_last_gop()
    {
        var backend = new FakeTimelineBackend { ManualDecoding = true, KeyframeInterval = 30 };
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("a", Fps30, 3000, backend: backend);
        b.AddMedia("b", Fps30, 3000, backend: backend);
        Clip forward = b.AddVideo(0, "a", Time.Zero, Time.Zero, S(2), "a");
        Clip reverse = SpeedClips.Reversed(b.AddVideo(0, "b", S(2), S(10), S(1), "b"), new Rational(1, 1), S(10), S(13));
        var time = new ManualTimeProvider();
        using TimelineSession session = TimelineSessionTests.Open(b, backend, new TimelineSessionOptions { EnableAudio = false, TimeProvider = time });
        Settle(session, backend).Dispose();
        session.ResetStats();

        (int wrong, _) = PlayFor(session, backend, time, S(3), [forward, reverse]);

        TimelineStats stats = session.Stats;
        FakeMediaVideo video = backend.Videos.Single(v => v.Path.EndsWith("b.mp4", StringComparison.Ordinal));
        output.WriteLine($"Pedidos del clip en reversa: {string.Join(", ", video.Requests.Take(40))}");
        Assert.Equal(0, wrong);
        Assert.Equal((1L, 1L, 0L), (stats.Cuts, stats.PreRollHits, stats.LateAtCuts));
        Assert.Equal(0, stats.FramesDropped);

        // Lo primero que se pide es lo más bajo del último GOP (el keyframe 360), para seguir hacia adelante hasta el 389.
        Assert.Equal(360, video.Requests[0]);
        Assert.Equal(389, video.Requests.Take(30).Max());
    }

    [Fact]
    public void The_same_media_forward_and_in_reverse_get_their_own_decoders_and_drop_nothing()
    {
        var backend = new FakeTimelineBackend { ManualDecoding = true, KeyframeInterval = 30, PoolSurfaceBytes = 3_133_440 };
        var b = new TimelineBuilder(2, 0);
        b.AddMedia("m", Fps30, 30_000, backend: backend);
        Clip forward = b.AddVideo(0, "m", Time.Zero, S(60), S(20), "adelante");
        Clip reverse = SpeedClips.Reversed(b.AddVideo(1, "m", Time.Zero, S(60), S(1), "atras"), new Rational(1, 1), S(60), S(80));
        var time = new ManualTimeProvider();
        using TimelineSession session = TimelineSessionTests.Open(b, backend, new TimelineSessionOptions { EnableAudio = false, TimeProvider = time });
        Settle(session, backend).Dispose();
        session.ResetStats();

        (int wrong, _) = PlayFor(session, backend, time, S(8), [forward, reverse]);

        TimelineStats stats = session.Stats;
        output.WriteLine($"Decodificadores {backend.Videos.Count}, seeks {string.Join("/", backend.Videos.Select(v => v.Seeks))}, " +
            $"decodificados {string.Join("/", backend.Videos.Select(v => v.DecodedFrames))}, perdidos {stats.FramesDropped}, tarde {stats.LateRefreshes}.");
        Assert.Equal(0, wrong);
        Assert.Equal(0, stats.FramesDropped);
        Assert.Equal(0, stats.LateRefreshes);
        Assert.Equal(2, backend.Videos.Count);

        // Cada uno sigue su sentido: el de adelante no busca nunca más; el de reversa, una vez por GOP.
        Assert.Equal(1, backend.Videos.Min(v => v.Seeks));
        Assert.InRange(backend.Videos.Max(v => v.Seeks), 8, 12);
    }

    [Fact]
    public void A_paused_tick_reserves_the_same_in_reverse_as_forward()
    {
        Assert.Equal(PausedTickBytes(reverse: false), PausedTickBytes(reverse: true));
    }

    private static long PausedTickBytes(bool reverse)
    {
        var backend = new FakeTimelineBackend { ManualDecoding = true, KeyframeInterval = 30 };
        var b = new TimelineBuilder(4, 0);
        for (int i = 0; i < 4; i++)
        {
            b.AddMedia($"m{i}", Fps30, 300_000, backend: backend);
            Clip clip = b.AddVideo(i, $"m{i}", Time.Zero, S(10), S(60));
            if (reverse)
            {
                SpeedClips.Reversed(clip, new Rational(1, 1), S(10), S(70));
            }
            else
            {
                SpeedClips.WithRate(clip, new Rational(1, 1), S(10), S(70));
            }
        }

        using TimelineSession session = TimelineSessionTests.Open(b, backend, new TimelineSessionOptions { EnableAudio = false, TimeProvider = new ManualTimeProvider() });
        Settle(session, backend).Dispose();
        for (int i = 0; i < 50; i++)
        {
            session.Tick().Dispose();
        }

        // La ventana con menos bytes de tres: lo que reserva cada Tick sale en todas. En la CI de Windows, con el runner
        // cargado, una ventana sumó unos 4,8 KB de una vez (24 bytes por Tick de diferencia entre sentidos), que no son
        // del Tick.
        long fewest = long.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 200; i++)
            {
                session.Tick().Dispose();
            }

            fewest = Math.Min(fewest, (GC.GetAllocatedBytesForCurrentThread() - before) / 200);
        }

        return fewest;
    }

    /// <summary>Reproduce con refrescos de 60 Hz y los decodificadores al día; devuelve los cuadros distintos de la referencia.</summary>
    private static (int Wrong, long Shown) PlayFor(TimelineSession session, FakeTimelineBackend backend, ManualTimeProvider time, Time length, Clip[] clips)
    {
        session.Play();
        int wrong = 0;
        long shown = 0;
        int refreshes = (int)(length.Ticks / Time.FromFrames(1, Rational.Fps60).Ticks);
        for (int refresh = 0; refresh < refreshes; refresh++)
        {
            time.Advance(TimeSpan.FromTicks(166_667));
            using (CompositionFrame frame = session.Tick())
            {
                foreach (LayerFrame layer in frame.Layers)
                {
                    Clip clip = clips.Single(c => c.Id == layer.ClipId);
                    wrong += layer.Frame.Index == Expected(clip, frame.TimelineTime) ? 0 : 1;
                    shown++;
                }
            }

            backend.RunDecoders();
        }

        return (wrong, shown);
    }

    private static long Expected(Clip clip, Time timeline) =>
        clip.Speed is null ? (clip.SourceIn + (timeline - clip.Start)).ToFrames(Fps30) : SpeedClips.Expected(clip, Fps30, timeline);

    /// <summary>
    /// Comprueba contra la fuerza bruta (el cuadro de cada cuadro de la secuencia) los cuadros mostrados, el siguiente y el
    /// anterior mostrado, cuándo se ve cada uno y cuántos hay entre dos.
    /// </summary>
    private static void AssertShownHelpers(VideoClipPlan plan)
    {
        var shown = new SortedSet<long>();
        long firstGrid = plan.FirstSequenceFrame;
        for (long g = firstGrid; Time.FromFrames(g, plan.SequenceRate) < plan.End; g++)
        {
            shown.Add(plan.FrameAt(Time.FromFrames(g, plan.SequenceRate)));
        }

        Assert.Equal((shown.Min, shown.Max), (plan.LowestFrame, plan.HighestFrame));
        for (long i = shown.Min - 2; i <= shown.Max + 2; i++)
        {
            SortedSet<long> above = shown.GetViewBetween(i, long.MaxValue);
            SortedSet<long> below = shown.GetViewBetween(long.MinValue, i);
            Assert.Equal(above.Count > 0 ? above.Min : long.MaxValue, plan.NextShown(i));
            Assert.Equal(below.Count > 0 ? below.Max : -1, plan.PreviousShown(i));
            Assert.Equal(shown.Contains(i), plan.IsShown(i));
            if (shown.Contains(i))
            {
                // El primer instante en que se ve ese cuadro o uno que va después en la reproducción.
                Time at = plan.ShownAt(i);
                bool reached = plan.Forward ? plan.FrameAt(at) >= i : plan.FrameAt(at) <= i;
                bool before = at == plan.Start || (plan.Forward ? plan.FrameAt(at - Time.FromTicks(1)) < i : plan.FrameAt(at - Time.FromTicks(1)) > i);
                Assert.True(reached && before, $"ShownAt({i}) = {at}");
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
