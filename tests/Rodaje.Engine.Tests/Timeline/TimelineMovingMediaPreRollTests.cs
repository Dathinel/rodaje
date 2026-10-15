using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Rodaje.Playback.Composition;
using Xunit.Abstractions;
using static Rodaje.Engine.Tests.Support.Seconds;

namespace Rodaje.Engine.Tests.Timeline;

/// <summary>
/// Un medio que en el corte pasa a otra pista y vuelve a un punto de su material que la pista de arriba ya mostró, con un
/// solo decodificador para ese medio (en 4K, el presupuesto de VRAM no deja abrir otro). El pre-roll no debe llevarse el
/// decodificador hacia atrás mientras la pista de arriba todavía lo necesita: volvía con otro seek y la capa de arriba se
/// quedaba sin cuadros hasta el corte (con 4 pistas 4K HEVC, esperas de 300 a 480 ms en casi todos los cortes).
/// </summary>
public sealed class TimelineMovingMediaPreRollTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Refresh60 = TimeSpan.FromTicks(166_667);

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void The_pre_roll_of_a_media_moving_to_another_track_does_not_seek_away_from_its_visible_clip(double sourceInSeconds)
    {
        var backend = new FakeTimelineBackend { ManualDecoding = true, KeyframeInterval = 30 };
        var b = new TimelineBuilder(2, 0);
        b.AddMedia("a", Rational.Fps30, 3_000, backend: backend);
        b.AddMedia("b", Rational.Fps30, 3_000, backend: backend);
        Clip top = b.AddVideo(1, "a", Time.Zero, Time.Zero, S(3), "a-arriba");
        Clip below = b.AddVideo(0, "b", Time.Zero, Time.Zero, S(3), "b-abajo");
        Clip moved = b.AddVideo(0, "a", S(3), S(sourceInSeconds), S(3), "a-abajo");
        Clip[] clips = [top, below, moved];

        var time = new ManualTimeProvider();
        using TimelineSession session = TimelineSessionTests.Open(b, backend,
            new TimelineSessionOptions { EnableAudio = false, TimeProvider = time, MaxDecodersPerMedia = 1 });
        Settle(session, backend);
        FakeMediaVideo a = backend.Videos.Single(v => v.Path == TimelineBuilder.PathOf("a"));
        long seeksBefore = a.Seeks;
        long decodedBefore = a.DecodedFrames;
        session.ResetStats();

        session.Play();
        int wrong = Play(session, backend, time, S(4.5), clips);

        TimelineStats stats = session.Stats;
        long seeks = a.Seeks - seeksBefore;
        long decoded = a.DecodedFrames - decodedBefore;
        output.WriteLine($"entrada {sourceInSeconds} s: seeks de «a» {seeks}, decodificados {decoded}, cortes {stats.Cuts} " +
            $"(pre-roll {stats.PreRollHits}/{stats.PreRollMisses}), tarde {stats.LateRefreshes}, cuadros distintos del debido {wrong}");
        Assert.Equal(0, wrong);
        Assert.Equal(1, backend.Videos.Count(v => v.Path == TimelineBuilder.PathOf("a")));

        // Uno solo: el del pre-roll, después de decodificar lo que le quedaba al clip de arriba.
        Assert.Equal(1, seeks);
        Assert.Equal((1, 1), (stats.Cuts, stats.PreRollHits));
        Assert.Equal(0, stats.LateRefreshes);
    }

    private static int Play(TimelineSession session, FakeTimelineBackend backend, ManualTimeProvider time, Time until, IReadOnlyList<Clip> clips)
    {
        int wrong = 0;
        while (session.Position < until)
        {
            time.Advance(Refresh60);
            using (CompositionFrame frame = session.Tick())
            {
                foreach (LayerFrame layer in frame.Layers)
                {
                    Clip? clip = clips.FirstOrDefault(c => c.Id == layer.ClipId);
                    if (clip is not null && frame.TimelineTime >= clip.Start && frame.TimelineTime < clip.End
                        && layer.Frame.Index != clip.TimeMap.Source(frame.TimelineTime).ToFrames(Rational.Fps30))
                    {
                        wrong++;
                    }
                }
            }

            backend.RunDecoders();
        }

        return wrong;
    }

    private static void Settle(TimelineSession session, FakeTimelineBackend backend)
    {
        for (int i = 0; i < 20 && session.IsSeekPending; i++)
        {
            backend.RunDecoders();
            session.Tick().Dispose();
        }

        Assert.False(session.IsSeekPending);
    }
}
