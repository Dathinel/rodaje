using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Rodaje.Playback.Composition;
using static Rodaje.Engine.Tests.Support.Seconds;

namespace Rodaje.Engine.Tests.Timeline;

public sealed class TimelineSeekPresentationTests
{
    private static readonly Rational Fps30 = Rational.Fps30;

    [Fact]
    public void After_a_seek_the_last_complete_composition_stays_until_every_layer_has_its_frame()
    {
        var time = new ManualTimeProvider();
        var backend = new FakeTimelineBackend { ManualDecoding = true };
        TimelineBuilder b = TwoTracks(backend);
        using TimelineSession session = TimelineSessionTests.Open(b, backend, new TimelineSessionOptions { TimeProvider = time });

        using (CompositionFrame opening = session.Tick())
        {
            Assert.Empty(opening.Layers);
        }

        backend.RunDecoders();
        Assert.Equal(new long[] { 0, 300 }, Indices(session));

        session.Seek(S(2));
        Assert.Equal(new long[] { 0, 300 }, Indices(session));
        Decoder(backend, b, "abajo").RunAll();
        Assert.Equal(new long[] { 0, 300 }, Indices(session));
        Assert.True(session.IsSeekPending);

        backend.RunDecoders();
        Assert.Equal(new long[] { 60, 360 }, Indices(session));
        TimelineStats stats = session.Stats;
        Assert.Equal(3, stats.HeldRefreshes);
        Assert.Equal(0, stats.PartialPresentations);
    }

    [Fact]
    public void Past_the_limit_what_there_is_is_shown_and_counted()
    {
        var time = new ManualTimeProvider();
        var backend = new FakeTimelineBackend { ManualDecoding = true };
        TimelineBuilder b = TwoTracks(backend);
        using TimelineSession session = TimelineSessionTests.Open(b, backend,
            new TimelineSessionOptions { TimeProvider = time, PartialFrameTimeout = TimeSpan.FromMilliseconds(300) });
        backend.RunDecoders();
        session.Tick().Dispose();

        session.Seek(S(2));
        Decoder(backend, b, "abajo").RunAll();
        time.Advance(TimeSpan.FromMilliseconds(299));
        Assert.Equal(new long[] { 0, 300 }, Indices(session));
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(new long[] { 60, 300 }, Indices(session));
        Assert.Equal(new long[] { 60, 300 }, Indices(session));

        Assert.Equal(1, session.Stats.PartialPresentations);
    }

    [Fact]
    public void A_seek_whose_frames_are_ready_shows_them_on_the_next_refresh()
    {
        var backend = new FakeTimelineBackend();
        TimelineBuilder b = TwoTracks(backend);
        using TimelineSession session = TimelineSessionTests.Open(b, backend);
        session.Tick().Dispose();

        session.Seek(S(3));

        Assert.Equal(new long[] { 90, 390 }, Indices(session));
        Assert.Equal(0, session.Stats.HeldRefreshes);
    }

    [Fact]
    public void Held_layers_keep_their_effects_and_transition()
    {
        var time = new ManualTimeProvider();
        var backend = new FakeTimelineBackend { ManualDecoding = true };
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("a", Fps30, 3000, backend: backend);
        b.AddMedia("b", Fps30, 3000, backend: backend);
        b.AddVideo(0, "a", Time.Zero, S(10), S(2), "sale").Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Vignette));
        b.AddVideo(0, "b", S(2), S(10), S(2), "entra");
        TimelineEffectsTests.AddTransition(b, BuiltInTransitions.Crossfade, "sale", "entra", S(1));
        using TimelineSession session = TimelineSessionTests.Open(b, backend, new TimelineSessionOptions { TimeProvider = time });
        session.Seek(S(2));
        backend.RunDecoders();
        session.Tick().Dispose();

        session.Seek(S(3.5));
        using CompositionFrame held = session.Tick();

        LayerFrame layer = Assert.Single(held.Layers);
        Assert.Equal(("sale", 3), (layer.ClipId, layer.Effects.Single().Code));
        Assert.Equal(("entra", 0.5f), (layer.Transition!.Value.Incoming.ClipId, layer.Transition.Value.Progress));
    }

    [Fact]
    public void Decoders_open_from_the_top_track_down()
    {
        var backend = new FakeTimelineBackend { ManualDecoding = true };
        var b = new TimelineBuilder(3, 0);
        b.AddMedia("fondo", Fps30, 3000, backend: backend);
        b.AddMedia("medio", Fps30, 3000, backend: backend);
        b.AddMedia("arriba", Fps30, 3000, backend: backend);
        b.AddVideo(0, "fondo", Time.Zero, Time.Zero, S(4));
        b.AddVideo(1, "medio", Time.Zero, Time.Zero, S(4))
            .Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Transform, (BuiltInEffects.TransformParameters.Scale, 0.5)));
        b.AddVideo(2, "arriba", Time.Zero, Time.Zero, S(4))
            .Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Transform, (BuiltInEffects.TransformParameters.Opacity, 0.5)));
        using TimelineSession session = TimelineSessionTests.Open(b, backend);

        Assert.Equal(new[] { "arriba", "medio", "fondo" }, OpenOrder(backend));
    }

    private static TimelineBuilder TwoTracks(FakeTimelineBackend backend)
    {
        var b = new TimelineBuilder(2, 0);
        b.AddMedia("abajo", Fps30, 3000, backend: backend);
        b.AddMedia("arriba", Fps30, 3000, backend: backend);
        b.AddVideo(0, "abajo", Time.Zero, Time.Zero, S(10));
        b.AddVideo(1, "arriba", Time.Zero, S(10), S(10));
        return b;
    }

    /// <summary>Corre de a uno los ejecutores de los decodificadores en el orden en que se crearon y anota qué medio abrió cada uno.</summary>
    private static string[] OpenOrder(FakeTimelineBackend backend)
    {
        var order = new List<string>();
        foreach (ManualExecutor executor in backend.Executors.Skip(1).ToList())
        {
            int before = backend.Videos.Count;
            executor.RunAll(1);
            order.AddRange(backend.Videos.Skip(before).Select(v => Path.GetFileNameWithoutExtension(v.Path)));
        }

        return [.. order];
    }

    /// <summary>El ejecutor del decodificador del medio, por el orden en que se crearon (el primero es el de servicio).</summary>
    private static ManualExecutor Decoder(FakeTimelineBackend backend, TimelineBuilder b, string mediaId)
    {
        string path = TimelineBuilder.PathOf(mediaId);
        int index = backend.Videos.FindIndex(v => v.Path == path);
        return backend.Executors[index + 1];
    }

    private static long[] Indices(TimelineSession session)
    {
        using CompositionFrame frame = session.Tick();
        return [.. frame.Layers.Select(l => l.Frame.Index)];
    }
}
