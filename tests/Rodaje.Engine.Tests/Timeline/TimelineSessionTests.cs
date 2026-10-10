using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Rodaje.Media.Probe;
using Rodaje.Playback.Composition;
using static Rodaje.Engine.Tests.Support.Seconds;

namespace Rodaje.Engine.Tests.Timeline;

public sealed class TimelineSessionTests
{
    private static readonly Rational Fps30 = Rational.Fps30;
    private static readonly Time Refresh60 = Time.FromFrames(1, Rational.Fps60);

    [Fact]
    public void Layers_come_bottom_to_top_with_the_frame_of_each_source()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(3, 0);
        b.AddMedia("m0", Rational.Fps30, 3000, backend: backend);
        b.AddMedia("m1", Rational.Fps25, 3000, backend: backend);
        b.AddMedia("m2", Rational.Fps24, 3000, backend: backend);
        b.AddVideo(0, "m0", Time.Zero, S(1), S(4), "fondo");
        b.AddVideo(1, "m1", Time.Zero, Time.Zero, S(4), "medio");
        b.AddVideo(2, "m2", S(1), S(2), S(2), "arriba");
        using TimelineSession session = Open(b, backend);

        session.Seek(F(45, Fps30));
        using CompositionFrame frame = session.Tick();

        Assert.Equal(F(45, Fps30), frame.TimelineTime);
        Assert.Equal((1920, 1080), (frame.Width, frame.Height));
        Assert.Equal(new[] { 0, 1, 2 }, frame.Layers.Select(l => l.TrackIndex));
        Assert.Equal(new[] { "fondo", "medio", "arriba" }, frame.Layers.Select(l => l.ClipId));
        Assert.Equal(new long[] { 75, 37, 60 }, Indices(frame));
        Assert.Equal(new[] { "m0", "m1", "m2" }, Media(frame));
        Assert.All(frame.Layers, l => Assert.Equal(LayerTransform.Identity, l.Transform));
        Assert.False(session.IsSeekPending);
    }

    [Fact]
    public void Each_layer_carries_the_pixel_shape_and_rotation_of_its_source()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(2, 0);
        b.AddMedia("cuadrado", Rational.Fps30, 3000, backend: backend);
        b.AddMedia("girado", Rational.Fps30, 3000);
        MediaInfo info = Samples.Info(Rational.Fps30, 3000);
        backend.Register(TimelineBuilder.PathOf("girado"), info with
        {
            Path = TimelineBuilder.PathOf("girado"),
            Video = info.Video! with { SampleAspectRatio = new Rational(4, 3), Rotation = -90 },
        });
        b.AddVideo(0, "cuadrado", Time.Zero, Time.Zero, S(2));
        b.AddVideo(1, "girado", Time.Zero, Time.Zero, S(2));
        using TimelineSession session = Open(b, backend);

        using CompositionFrame frame = session.Tick();

        Assert.Equal((new Rational(1, 1), 0), (frame.Layers[0].SampleAspectRatio, frame.Layers[0].Rotation));
        Assert.Equal((new Rational(4, 3), 270), (frame.Layers[1].SampleAspectRatio, frame.Layers[1].Rotation));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 90)]
    [InlineData(-90, 270)]
    [InlineData(450, 90)]
    [InlineData(181, 180)]
    [InlineData(-180, 180)]
    public void Rotations_are_normalized_to_quarter_turns(int degrees, int expected) =>
        Assert.Equal(expected, TimelineScheduler.NormalizeRotation(degrees));

    [Fact]
    public void Gaps_hidden_tracks_and_disabled_clips_produce_no_layer()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(3, 0);
        b.AddMedia("m", Rational.Fps30, 3000, backend: backend);
        b.AddVideo(0, "m", Time.Zero, Time.Zero, S(1));
        b.AddVideo(0, "m", S(2), Time.Zero, S(1));
        b.AddVideo(1, "m", Time.Zero, Time.Zero, S(3));
        b.Sequence.VideoTracks[1].Hidden = true;
        b.AddVideo(2, "m", Time.Zero, Time.Zero, S(3)).Enabled = false;
        using TimelineSession session = Open(b, backend);

        session.Seek(S(0.5));
        using (CompositionFrame before = session.Tick())
        {
            Assert.Equal(new[] { 0 }, before.Layers.Select(l => l.TrackIndex));
        }

        session.Seek(S(1.5));
        using (CompositionFrame gap = session.Tick())
        {
            Assert.Empty(gap.Layers);
            Assert.False(session.IsSeekPending);
        }

        session.Seek(S(2.5));
        using CompositionFrame after = session.Tick();
        Assert.Equal(new long[] { 15 }, Indices(after));
    }

    [Fact]
    public void Stepping_across_a_cut_shows_the_last_frame_then_the_first_frame_exactly()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("a", Rational.Fps30, 3000, backend: backend);
        b.AddMedia("b", Rational.Fps25, 3000, backend: backend);
        b.AddVideo(0, "a", Time.Zero, F(100, Fps30), F(60, Fps30), "a");
        b.AddVideo(0, "b", F(60, Fps30), S(20), S(2), "b");
        using TimelineSession session = Open(b, backend);
        session.SeekToFrame(55);

        var seen = new List<(string Clip, long Index)>();
        for (int i = 0; i < 10; i++)
        {
            using CompositionFrame frame = session.Tick();
            LayerFrame layer = Assert.Single(frame.Layers);
            seen.Add((layer.ClipId, layer.Frame.Index));
            session.StepFrame(1);
        }

        Assert.Equal(("a", 159L), seen[4]);
        Assert.Equal(("b", 500L), seen[5]);
        Assert.Equal(("b", 500L), seen[6]);
        Assert.Equal(("b", 501L), seen[7]);
        Assert.Equal(65, session.CurrentFrame);
    }

    [Fact]
    public void Playing_mixed_rates_on_a_30_fps_sequence_shows_the_exact_source_frame_on_every_refresh()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(3, 0);
        Rational[] rates = [Rational.Fps24, Rational.Fps25, Rational.Ntsc30];
        for (int m = 0; m < 3; m++)
        {
            b.AddMedia($"m{m}", rates[m], 10_000, backend: backend);
        }

        for (int track = 0; track < 3; track++)
        {
            for (int segment = 0; segment < 3; segment++)
            {
                b.AddVideo(track, $"m{(segment + track) % 3}", S(2 * segment), S(((segment * 37) + (track * 53)) % 100 / 10.0), S(2));
            }
        }

        using TimelineSession session = Open(b, backend);
        TimelinePlan plan = b.Plan();
        session.Tick().Dispose();
        session.Play();
        for (int refresh = 0; refresh < 340; refresh++)
        {
            using CompositionFrame frame = session.Tick();
            Assert.Equal(3, frame.Layers.Count);
            foreach (LayerFrame layer in frame.Layers)
            {
                VideoClipPlan clip = plan.Layers[layer.TrackIndex].ClipAt(frame.TimelineTime)!;
                Int128 sourceTicks = frame.TimelineTime.Ticks - clip.Start.Ticks + clip.SourceIn.Ticks;
                long expected = (long)(sourceTicks * clip.FrameRate.Num / ((Int128)Time.TicksPerSecond * clip.FrameRate.Den));
                Assert.Equal(clip.ClipId, layer.ClipId);
                Assert.Equal(expected, layer.Frame.Index);
                Assert.Equal(clip.Source.Path, ((FakeFramePayload)layer.Frame.Payload!).Path);
            }

            backend.Clock.Advance(Refresh60);
        }

        TimelineStats stats = session.Stats;
        Assert.Equal(0, stats.LateRefreshes);
        Assert.Equal(6, stats.Cuts);
        Assert.Equal(6, stats.PreRollHits);
        Assert.InRange(stats.DecodersOpen, 3, 6);
        Assert.Equal(3, stats.Layers.Count);
    }

    [Fact]
    public void Pre_roll_decodes_the_next_clip_before_its_cut()
    {
        var backend = new FakeTimelineBackend { ManualDecoding = true };
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("a", Rational.Fps30, 3000, backend: backend);
        b.AddMedia("b", Rational.Fps25, 3000, backend: backend);
        b.AddVideo(0, "a", Time.Zero, Time.Zero, S(2), "a");
        b.AddVideo(0, "b", S(2), S(10), S(2), "b");
        using TimelineSession session = Open(b, backend);
        Settle(session, backend);

        session.Play();
        IReadOnlyList<long>? requestsBeforeCut = null;
        while (session.Position < S(3))
        {
            backend.Clock.Advance(Refresh60);
            session.Tick().Dispose();
            backend.RunDecoders();
            if (session.Position < S(2) && session.Position >= S(1.9))
            {
                requestsBeforeCut ??= backend.Videos.Single(v => v.Path.EndsWith("b.mp4", StringComparison.Ordinal)).Requests;
            }
        }

        Assert.NotNull(requestsBeforeCut);
        Assert.Equal(new long[] { 250, 251, 252, 253, 254, 255 }, requestsBeforeCut!.Take(6));
        LayerStats layer = Assert.Single(session.Stats.Layers);
        Assert.Equal((1L, 1L, 0L, 0L, 0L), (layer.Cuts, layer.PreRollHits, layer.PreRollMisses, layer.LateAtCuts, layer.LateRefreshes));
    }

    [Fact]
    public void Without_pre_roll_the_cut_arrives_before_its_frame_and_counts_as_late()
    {
        var backend = new FakeTimelineBackend { ManualDecoding = true };
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("a", Rational.Fps30, 3000, backend: backend);
        b.AddMedia("b", Rational.Fps25, 3000, backend: backend);
        b.AddVideo(0, "a", Time.Zero, Time.Zero, S(2), "a");
        b.AddVideo(0, "b", S(2), S(10), S(2), "b");
        using TimelineSession session = Open(b, backend, new TimelineSessionOptions { PreRoll = TimeSpan.Zero });
        Settle(session, backend);

        session.Play();
        while (session.Position < S(2.5))
        {
            backend.Clock.Advance(Refresh60);
            using (CompositionFrame frame = session.Tick())
            {
                if (session.Position >= S(2) && frame.Layers.Count > 0 && frame.Layers[0].ClipId == "a")
                {
                    Assert.Equal(59, frame.Layers[0].Frame.Index);
                }
            }

            backend.RunDecoders();
        }

        LayerStats layer = Assert.Single(session.Stats.Layers);
        Assert.Equal((1L, 0L, 1L), (layer.Cuts, layer.PreRollHits, layer.PreRollMisses));
        Assert.True(layer.LateAtCuts >= 1);
        Assert.Equal(layer.LateAtCuts, layer.LateRefreshes);
    }

    [Fact]
    public void Due_frames_of_every_layer_are_decoded_before_any_look_ahead()
    {
        // Con un solo decodificador por medio las dos capas lo comparten: los cuadros debidos van primero.
        var backend = new FakeTimelineBackend { ManualDecoding = true };
        var b = new TimelineBuilder(2, 0);
        b.AddMedia("m", Rational.Fps30, 3000, backend: backend);
        b.AddVideo(0, "m", Time.Zero, Time.Zero, S(4));
        b.AddVideo(1, "m", Time.Zero, S(50), S(4));
        using TimelineSession session = Open(b, backend, new TimelineSessionOptions { MaxDecodersPerMedia = 1 });

        backend.RunDecoders();

        FakeMediaVideo video = Assert.Single(backend.Videos);
        Assert.Equal(new long[] { 0, 1500, 1 }, video.Requests.Take(3));
    }

    [Fact]
    public void Reload_while_playing_keeps_the_position_and_shows_the_edited_clip()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(2, 1);
        b.AddMedia("m", Rational.Fps30, 3000, backend: backend);
        Clip under = b.AddVideo(0, "m", Time.Zero, Time.Zero, S(4), "abajo");
        b.AddVideo(1, "m", Time.Zero, S(10), S(4), "arriba");
        b.AddAudio(0, "m", Time.Zero, Time.Zero, S(4));
        using TimelineSession session = Open(b, backend);
        session.Tick().Dispose();
        session.Play();
        backend.Clock.Advance(S(1));
        session.Tick().Dispose();
        using (CompositionFrame before = session.Tick())
        {
            Assert.Equal(new long[] { 30, 330 }, Indices(before));
        }

        under.SourceIn = S(2);
        under.SourceOut = S(6);
        b.Sequence.VideoTracks[1].Clips.Clear();
        session.Invalidate();

        Assert.True(session.IsPlaying);
        Assert.Equal(S(1), session.Position);
        using (CompositionFrame after = session.Tick())
        {
            Assert.Equal(new long[] { 90 }, Indices(after));
            Assert.Equal("abajo", after.Layers[0].ClipId);
        }

        backend.Clock.Advance(F(1, Fps30));
        using CompositionFrame next = session.Tick();
        Assert.Equal(new long[] { 91 }, Indices(next));
        Assert.Equal(0, session.Stats.Cuts);
    }

    [Fact]
    public void Reload_applies_transform_edits_to_the_frame_already_on_screen()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Rational.Fps30, 3000, backend: backend);
        Clip clip = b.AddVideo(0, "m", Time.Zero, Time.Zero, S(4));
        using TimelineSession session = Open(b, backend);
        session.Seek(S(1));
        session.Tick().Dispose();

        clip.Effects.Add(new EffectInstance
        {
            Id = "t",
            Type = BuiltInEffects.Transform,
            Parameters = new() { [BuiltInEffects.TransformParameters.Opacity] = new Parameter { Value = 0.25 } },
        });
        session.Reload(b.Project);

        using CompositionFrame frame = session.Tick();
        Assert.Equal(0.25, frame.Layers[0].Transform.Opacity);
        Assert.Equal(30, frame.Layers[0].Frame.Index);
    }

    [Fact]
    public void Reload_with_a_new_track_and_a_hidden_one_updates_the_layers()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(2, 0);
        b.AddMedia("m", Rational.Fps30, 3000, backend: backend);
        b.AddVideo(0, "m", Time.Zero, Time.Zero, S(4));
        using TimelineSession session = Open(b, backend);
        session.Seek(S(1));
        session.Tick().Dispose();

        b.AddVideo(1, "m", Time.Zero, S(20), S(4));
        b.Sequence.VideoTracks[0].Hidden = true;
        session.Invalidate();

        using CompositionFrame frame = session.Tick();
        LayerFrame layer = Assert.Single(frame.Layers);
        Assert.Equal((1, 630L), (layer.TrackIndex, layer.Frame.Index));
    }

    [Fact]
    public void A_media_used_at_two_distant_places_gets_a_decoder_for_each_and_idle_ones_close_and_reopen()
    {
        var time = new ManualTimeProvider();
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(2, 0);
        b.AddMedia("a", Rational.Fps30, 3000, backend: backend);
        b.AddMedia("b", Rational.Fps30, 3000, backend: backend);
        b.AddVideo(0, "a", Time.Zero, Time.Zero, S(1));
        b.AddVideo(0, "b", S(1), Time.Zero, S(1));
        b.AddVideo(1, "a", Time.Zero, S(30), S(2));
        using TimelineSession session = Open(b, backend, new TimelineSessionOptions { TimeProvider = time, PreRoll = TimeSpan.Zero });
        session.Seek(S(0.5));
        session.Tick().Dispose();
        Assert.Equal(2, session.Stats.DecodersOpen);
        Assert.Equal(2, backend.Videos.Count(v => v.Path.EndsWith("a.mp4", StringComparison.Ordinal)));

        session.Seek(S(1.5));
        session.Tick().Dispose();
        Assert.Equal(3, session.Stats.DecodersOpen);

        session.Seek(S(1.9));
        session.Tick().Dispose();
        time.Advance(TimeSpan.FromSeconds(6));
        session.Tick().Dispose();
        Assert.Equal(2, session.Stats.DecodersOpen);

        b.Sequence.VideoTracks[1].Clips.Clear();
        session.Invalidate();
        session.Tick().Dispose();
        time.Advance(TimeSpan.FromSeconds(6));
        using (CompositionFrame frame = session.Tick())
        {
            Assert.Equal(new[] { "b" }, Media(frame));
        }

        TimelineStats stats = session.Stats;
        Assert.Equal((1, 2), (stats.DecodersOpen, stats.DecodersClosed));
        Assert.All(backend.Videos.Where(v => v.Path.EndsWith("a.mp4", StringComparison.Ordinal)), v => Assert.True(v.Disposed));

        session.Seek(S(0.2));
        using CompositionFrame back = session.Tick();
        Assert.Equal(new[] { "a" }, Media(back));
        Assert.Equal(3, backend.Videos.Count(v => v.Path.EndsWith("a.mp4", StringComparison.Ordinal)));
    }
    [Fact]
    public void The_cache_stays_within_the_global_budget_and_the_per_source_limit()
    {
        // Cuadros de 1080p NV12 y un presupuesto de 30: no alcanza para todo lo adelantado de tres capas más el pre-roll.
        const long frameBytes = 1920L * 1080 * 3 / 2;
        const long budget = 30 * frameBytes;
        var backend = new FakeTimelineBackend { FrameBytes = frameBytes };
        var b = new TimelineBuilder(3, 0);
        for (int m = 0; m < 3; m++)
        {
            b.AddMedia($"m{m}", Rational.Fps30, 3000, backend: backend);
            b.AddVideo(m, $"m{m}", Time.Zero, S(m), S(3));
            b.AddVideo(m, $"m{(m + 1) % 3}", S(3), S(10 + m), S(3));
        }

        using TimelineSession session = Open(b, backend, new TimelineSessionOptions { MaxCacheBytes = budget, PrefetchFrames = 12 });
        session.Tick().Dispose();
        session.Play();
        for (int refresh = 0; refresh < 300; refresh++)
        {
            backend.Clock.Advance(Refresh60);
            using CompositionFrame frame = session.Tick();
            Assert.Equal(3, frame.Layers.Count);
            Assert.True(session.Stats.CacheBytes <= budget, $"La caché usa {session.Stats.CacheBytes} bytes.");
        }

        TimelineStats stats = session.Stats;
        Assert.Equal(0, stats.LateRefreshes);
        Assert.Equal((3L, 3L), (stats.Cuts, stats.PreRollHits));
        Assert.True(stats.CacheEvictions > 0);
        Assert.Equal(budget, stats.CacheBudgetBytes);
    }

    [Fact]
    public void A_single_source_never_holds_more_than_its_limit()
    {
        var backend = new FakeTimelineBackend { FrameBytes = 10 };
        var b = new TimelineBuilder(4, 0);
        b.AddMedia("m", Rational.Fps30, 30_000, backend: backend);
        for (int track = 0; track < 4; track++)
        {
            b.AddVideo(track, "m", Time.Zero, S(track * 100), S(10));
        }

        using TimelineSession session = Open(b, backend,
            new TimelineSessionOptions { MaxFramesPerSource = 16, PrefetchFrames = 8, PreRollFrames = 6, MaxDecodersPerMedia = 1 });
        session.Tick().Dispose();
        session.Play();
        for (int refresh = 0; refresh < 60; refresh++)
        {
            backend.Clock.Advance(Refresh60);
            using CompositionFrame frame = session.Tick();
            Assert.Equal(4, frame.Layers.Count);
            Assert.True(session.Stats.CacheFrames <= 16);
        }

        Assert.Equal(0, session.Stats.LateRefreshes);
    }

    [Fact]
    public void While_a_seek_settles_the_old_frame_stays_and_lateness_is_not_counted()
    {
        var backend = new FakeTimelineBackend { ManualDecoding = true };
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Rational.Fps30, 3000, backend: backend);
        b.AddVideo(0, "m", Time.Zero, Time.Zero, S(10));
        using TimelineSession session = Open(b, backend);
        Settle(session, backend);
        session.Play();

        session.Seek(S(5));
        for (int i = 0; i < 3; i++)
        {
            backend.Clock.Advance(Refresh60);
            using CompositionFrame waiting = session.Tick();
            Assert.Equal(0, waiting.Layers[0].Frame.Index);
            Assert.True(session.IsSeekPending);
        }

        backend.RunDecoders();
        using CompositionFrame settled = session.Tick();
        Assert.Equal(151, settled.Layers[0].Frame.Index);
        Assert.False(session.IsSeekPending);
        Assert.Equal(0, session.Stats.LateRefreshes);
        Assert.Equal(1, session.Stats.Seeks);
    }

    [Fact]
    public void Playback_stops_at_the_end_of_the_sequence_and_shows_the_last_frame()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Rational.Fps30, 3000, backend: backend);
        b.AddVideo(0, "m", Time.Zero, Time.Zero, S(1));
        using TimelineSession session = Open(b, backend);
        int ends = 0;
        session.EndReached += (_, _) => ends++;
        session.Play();

        backend.Clock.Advance(S(1.5));
        session.Tick().Dispose();
        using (CompositionFrame last = session.Tick())
        {
            Assert.Equal(29, last.Layers[0].Frame.Index);
        }

        Assert.False(session.IsPlaying);
        Assert.Equal(1, ends);

        session.Play();
        Assert.Equal(Time.Zero, session.Position);
        Assert.True(session.IsPlaying);
    }

    [Fact]
    public void Step_frame_moves_by_sequence_frames_and_pauses()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(1, 0, Rational.Fps25);
        b.AddMedia("m", Rational.Fps30, 3000, backend: backend);
        b.AddVideo(0, "m", Time.Zero, Time.Zero, S(2));
        using TimelineSession session = Open(b, backend);
        session.Play();

        session.StepFrame(3);
        Assert.False(session.IsPlaying);
        Assert.Equal(F(3, Rational.Fps25), session.Position);
        session.StepFrame(-10);
        Assert.Equal(Time.Zero, session.Position);
        session.SeekToFrame(1000);
        Assert.Equal(49, session.CurrentFrame);
        Assert.Equal(50, session.FrameCount);
    }

    [Fact]
    public void A_media_that_does_not_open_leaves_its_layer_empty_and_is_reported()
    {
        var backend = new FakeTimelineBackend { ManualDecoding = true };
        var b = new TimelineBuilder(2, 0);
        b.AddMedia("ok", Rational.Fps30, 3000, backend: backend);
        b.AddMedia("roto", Rational.Fps30, 3000, backend: backend);
        backend.FailOpen.Add(TimelineBuilder.PathOf("roto"));
        b.AddVideo(0, "ok", Time.Zero, Time.Zero, S(2));
        b.AddVideo(1, "roto", Time.Zero, Time.Zero, S(2));
        var failures = new List<TimelineDecodeFailedEventArgs>();
        using TimelineSession session = Open(b, backend);
        session.DecodeFailed += (_, e) => failures.Add(e);

        backend.RunDecoders();
        using CompositionFrame frame = session.Tick();

        Assert.Equal(new[] { "ok" }, Media(frame));
        Assert.False(session.IsSeekPending);
        TimelineDecodeFailedEventArgs failure = Assert.Single(failures);
        Assert.Equal(("roto", -1L), (failure.MediaId, failure.FrameIndex));
        Assert.Equal(1, session.Stats.DecodeErrors);
    }

    [Fact]
    public void A_frame_that_fails_to_decode_is_skipped_without_blocking_the_seek()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Rational.Fps30, 3000, backend: backend);
        backend.FailFrames.Add((TimelineBuilder.PathOf("m"), 30));
        b.AddVideo(0, "m", Time.Zero, Time.Zero, S(2));
        using TimelineSession session = Open(b, backend);

        session.Seek(S(1));
        session.Tick().Dispose();

        Assert.False(session.IsSeekPending);
        Assert.Equal(1, session.Stats.DecodeErrors);
    }

    [Fact]
    public void Frames_skipped_by_a_clock_jump_count_as_dropped()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Rational.Fps30, 3000, backend: backend);
        b.AddVideo(0, "m", Time.Zero, Time.Zero, S(5));
        using TimelineSession session = Open(b, backend);
        session.Tick().Dispose();
        session.Play();
        backend.Clock.Advance(F(1, Fps30));
        session.Tick().Dispose();

        backend.Clock.Advance(F(5, Fps30));
        session.Tick().Dispose();

        Assert.Equal(4, session.Stats.FramesDropped);
        Assert.Equal(3, session.Stats.FramesShown);
    }

    [Fact]
    public void A_faster_source_skips_frames_by_design_and_that_is_not_counted_as_dropped()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(1, 0, Rational.Fps24);
        b.AddMedia("m", Rational.Fps30, 3000, backend: backend);
        b.AddVideo(0, "m", Time.Zero, Time.Zero, S(5));
        using TimelineSession session = Open(b, backend);
        session.Tick().Dispose();
        session.Play();
        for (int refresh = 0; refresh < 120; refresh++)
        {
            backend.Clock.Advance(Refresh60);
            session.Tick().Dispose();
        }

        Assert.Equal(0, session.Stats.FramesDropped);
        Assert.Equal(0, session.Stats.LateRefreshes);

        backend.Clock.Advance(F(5, Rational.Fps24));
        session.Tick().Dispose();
        Assert.Equal(4, session.Stats.FramesDropped);
    }

    [Fact]
    public void Showing_a_track_hidden_while_playing_resumes_on_the_exact_frame_without_drops_or_lateness()
    {
        var backend = new FakeTimelineBackend { ManualDecoding = true };
        var b = new TimelineBuilder(2, 0);
        b.AddMedia("fondo", Rational.Fps30, 3000, backend: backend);
        b.AddMedia("pip", Rational.Fps30, 3000, backend: backend);
        b.AddVideo(0, "fondo", Time.Zero, Time.Zero, S(20));
        b.AddVideo(1, "pip", Time.Zero, Time.Zero, S(20));
        using TimelineSession session = Open(b, backend);
        Settle(session, backend);
        session.Play();
        PlayFor(session, backend, S(1));

        b.Sequence.VideoTracks[1].Hidden = true;
        session.Invalidate();
        PlayFor(session, backend, S(1.5));

        b.Sequence.VideoTracks[1].Hidden = false;
        session.Invalidate();
        long due = session.CurrentFrame;
        using (CompositionFrame shown = session.Tick())
        {
            Assert.Equal(new long[] { due, due }, Indices(shown));
        }

        PlayFor(session, backend, S(0.5));
        LayerStats pip = session.Stats.Layers[1];
        Assert.Equal((0L, 0L, 0L), (pip.FramesDropped, pip.LateRefreshes, pip.Cuts));
        Assert.Equal(0, session.Stats.FramesDropped);
    }

    [Fact]
    public void A_track_hidden_for_long_is_released_and_comes_back_on_its_exact_frame_without_counting_drops()
    {
        var time = new ManualTimeProvider();
        var backend = new FakeTimelineBackend { ManualDecoding = true };
        var b = new TimelineBuilder(2, 0);
        b.AddMedia("fondo", Rational.Fps30, 3000, backend: backend);
        b.AddMedia("pip", Rational.Fps30, 3000, backend: backend);
        b.AddVideo(0, "fondo", Time.Zero, Time.Zero, S(30));
        b.AddVideo(1, "pip", Time.Zero, Time.Zero, S(30));
        using TimelineSession session = Open(b, backend, new TimelineSessionOptions { TimeProvider = time });
        Settle(session, backend);
        session.Play();
        PlayFor(session, backend, S(1), time);

        b.Sequence.VideoTracks[1].Hidden = true;
        session.Invalidate();
        PlayFor(session, backend, S(12), time);
        Assert.Equal(1, session.Stats.DecodersOpen);
        Assert.True(backend.Videos.Single(v => v.Path.EndsWith("pip.mp4", StringComparison.Ordinal)).Disposed);

        b.Sequence.VideoTracks[1].Hidden = false;
        session.Invalidate();
        using (CompositionFrame waiting = session.Tick())
        {
            Assert.Equal(new[] { 0 }, waiting.Layers.Select(l => l.TrackIndex));
        }

        backend.RunDecoders();
        long due = session.CurrentFrame;
        using (CompositionFrame back = session.Tick())
        {
            Assert.Equal(new long[] { due, due }, Indices(back));
        }

        PlayFor(session, backend, S(0.5), time);
        LayerStats pip = session.Stats.Layers[1];
        Assert.Equal((0L, 0L), (pip.FramesDropped, pip.Cuts));
        Assert.Equal(0, session.Stats.FramesDropped);
    }

    [Fact]
    public void Disposing_releases_every_frame_and_decoder()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(2, 1);
        b.AddMedia("a", Rational.Fps30, 3000, backend: backend);
        b.AddMedia("b", Rational.Fps24, 3000, backend: backend);
        b.AddVideo(0, "a", Time.Zero, Time.Zero, S(3));
        b.AddVideo(1, "b", S(1), Time.Zero, S(3));
        b.AddAudio(0, "a", Time.Zero, Time.Zero, S(3));
        TimelineSession session = Open(b, backend);
        session.Play();
        CompositionFrame kept = session.Tick();
        for (int i = 0; i < 90; i++)
        {
            backend.Clock.Advance(Refresh60);
            session.Tick().Dispose();
        }

        session.Dispose();
        Assert.True(backend.Live.Value > 0);
        kept.Dispose();

        Assert.Equal(0, backend.Live.Value);
        Assert.All(backend.Videos, v => Assert.True(v.Disposed));
        Assert.Contains("output", backend.Log.Entries);
        Assert.Throws<ObjectDisposedException>(() => session.Tick());
        session.Dispose();
    }

    [Fact]
    public void Without_audio_the_system_clock_drives_playback()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(1, 1);
        b.AddMedia("m", Rational.Fps30, 3000, backend: backend);
        b.AddVideo(0, "m", Time.Zero, Time.Zero, S(2));
        using TimelineSession session = Open(b, backend, new TimelineSessionOptions { EnableAudio = false });

        Assert.Null(backend.Output);
        Assert.False(session.HasAudioDevice);
        Assert.Contains("desactivado", session.AudioStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_options_are_rejected()
    {
        var b = new TimelineBuilder(1, 0);
        var backend = new FakeTimelineBackend();
        Assert.Throws<ArgumentOutOfRangeException>(() => Open(b, backend, new TimelineSessionOptions { MaxFramesPerSource = 8 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => Open(b, backend, new TimelineSessionOptions { PrefetchFrames = 0 }));
        Assert.Throws<KeyNotFoundException>(() => new TimelineSession(b.Project, "otra", null, new TimelineSessionOptions(), backend));
    }

    internal static TimelineSession Open(TimelineBuilder builder, FakeTimelineBackend backend, TimelineSessionOptions? options = null) =>
        new(builder.Project, builder.Sequence.Id, null, (options ?? new TimelineSessionOptions()) with { ProjectDirectory = @"C:\proyecto" }, backend);

    private static void Settle(TimelineSession session, FakeTimelineBackend backend)
    {
        for (int i = 0; i < 10 && session.IsSeekPending; i++)
        {
            backend.RunDecoders();
            session.Tick().Dispose();
        }

        Assert.False(session.IsSeekPending);
    }

    /// <summary>Reproduce a 60 Hz con un decodificador que entrega todo lo pedido entre refresco y refresco.</summary>
    private static void PlayFor(TimelineSession session, FakeTimelineBackend backend, Time span, ManualTimeProvider? time = null)
    {
        for (Time played = Time.Zero; played < span; played += Refresh60)
        {
            backend.Clock.Advance(Refresh60);
            time?.Advance(TimeSpan.FromTicks((long)(Refresh60.ToSeconds() * TimeSpan.TicksPerSecond)));
            session.Tick().Dispose();
            backend.RunDecoders();
        }
    }

    private static long[] Indices(CompositionFrame frame) => [.. frame.Layers.Select(l => l.Frame.Index)];

    private static string[] Media(CompositionFrame frame) =>
        [.. frame.Layers.Select(l => Path.GetFileNameWithoutExtension(((FakeFramePayload)l.Frame.Payload!).Path))];
}
