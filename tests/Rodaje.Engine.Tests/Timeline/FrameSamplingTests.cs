using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Offline;
using Rodaje.Engine.Tests.Offline;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Rodaje.Media.Probe;
using Rodaje.Media.Proxies;
using Rodaje.Playback;
using Rodaje.Playback.Composition;
using static Rodaje.Engine.Tests.Support.Seconds;

namespace Rodaje.Engine.Tests.Timeline;

public sealed class FrameSamplingTests
{
    [Fact]
    public void Blend_at_one_times_conforms_24_to_30_without_changing_FrameAt()
    {
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Rational.Fps24, 240);
        Clip clip = b.AddVideo(0, "m", Time.Zero, Time.Zero, S(2));
        clip.Speed = new ClipSpeed { FrameSampling = FrameSampling.Blend };
        clip.Duration = S(2);
        VideoClipPlan plan = b.Plan().Layers[0].Clips[0];

        for (int f = 0; f < 60; f++)
        {
            Time time = F(f, Rational.Fps30);
            FrameSample sample = plan.SampleAt(time);
            Assert.Equal(plan.FrameAt(time), sample.A);
            float expected = (f * 4 % 5) / 5f;
            Assert.Equal(expected, sample.T, 6);
            Assert.Equal(expected == 0 ? sample.A : sample.A + 1, sample.B);
            Assert.Equal(expected == 0 ? FrameSampling.Nearest : FrameSampling.Blend, sample.Mode);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_speed_curve_uses_the_exact_integral_and_the_instantaneous_rate(bool reverse)
    {
        var speed = new ClipSpeed
        {
            Reverse = reverse,
            FrameSampling = FrameSampling.OpticalFlow,
            Curve = new SpeedCurve { Span = S(2), Points = [new(0, 50), new(10_000, 650)] },
        };
        ClipTimeMap map = ClipTimeMap.Create(Time.Zero, S(2), reverse ? S(20) : S(3), speed);
        var plan = new VideoClipPlan("c", new SourceKey("m", "m.mp4"), map, Time.Zero, S(2), S(30), Rational.Fps30, Rational.Fps30,
            null, sampling: speed.FrameSampling);

        // I(t) = 0,5t + 1,5t²; a 0,3 s recorre 0,285 s y a 1,3 s, 3,185 s.
        foreach ((double at, double progress, FrameSampling mode) in new[]
        {
            (0.3, 0.285, FrameSampling.OpticalFlow), (1.3, 3.185, FrameSampling.Blend),
        })
        {
            Time source = reverse ? S(20) - Time.FromTicks(1) - S(progress) : S(3) + S(progress);
            long a = source.ToFrames(Rational.Fps30);
            double remainder = (source - F(a, Rational.Fps30)).ToSeconds() * 30;
            FrameSample sample = plan.SampleAt(S(at));
            Assert.Equal(plan.FrameAt(S(at)), sample.A);
            Assert.Equal(a, sample.A);
            Assert.Equal(reverse ? a - 1 : a + 1, sample.B);
            Assert.Equal((float)(reverse ? 1 - remainder : remainder), sample.T, 6);
            Assert.Equal(mode, sample.Mode);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Trimming_with_Offset_preserves_the_pair_and_fraction(bool reverse)
    {
        var speed = new ClipSpeed
        {
            Reverse = reverse,
            FrameSampling = FrameSampling.Blend,
            Curve = new SpeedCurve { Span = S(4), Points = [new(0, 25), new(10_000, 175)] },
        };
        ClipTimeMap map = ClipTimeMap.Create(S(1), S(4), reverse ? S(12) : S(2), speed);
        Time cut = S(1.7);
        ClipTimeMap trimmed = map.TrimIn(cut);
        var original = new VideoClipPlan("c", new SourceKey("m", "m.mp4"), map, map.Start, map.End, S(20), Rational.Fps24,
            Rational.Fps30, null, sampling: FrameSampling.Blend);
        var after = new VideoClipPlan("c", new SourceKey("m", "m.mp4"), trimmed, cut, map.End, S(20), Rational.Fps24,
            Rational.Fps30, null, sampling: FrameSampling.Blend);

        Assert.Equal(S(0.7), trimmed.Offset);
        for (Time time = cut; time < map.End; time += F(1, Rational.Fps30))
        {
            Assert.Equal(original.SampleAt(time), after.SampleAt(time));
            Assert.Equal(after.FrameAt(time), after.SampleAt(time).A);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_pair_never_leaves_the_media(bool reverse)
    {
        var speed = new ClipSpeed { Reverse = reverse, Rate = new Rational(1, 2), FrameSampling = FrameSampling.Blend };
        ClipTimeMap map = ClipTimeMap.Create(Time.Zero, S(2), reverse ? S(1) : Time.Zero, speed);
        var plan = new VideoClipPlan("c", new SourceKey("m", "m.mp4"), map, Time.Zero, S(2), S(1), Rational.Fps30, Rational.Fps30,
            null, sampling: FrameSampling.Blend);
        Time edge = reverse ? S(1.99) : S(1.98);
        FrameSample sample = plan.SampleAt(edge);
        Assert.Equal(plan.FrameAt(edge), sample.A);
        Assert.Equal(new FrameSample(sample.A, sample.A, 0, FrameSampling.Nearest), sample);
    }

    [Theory]
    [InlineData(0.001, false)]
    [InlineData(0.002, true)]
    [InlineData(0.998, true)]
    [InlineData(0.999, false)]
    public void Only_fractions_within_the_interpolation_thresholds_have_a_pair(double fraction, bool paired)
    {
        ClipTimeMap map = ClipTimeMap.Create(Time.Zero, S(2), Time.Zero, null);
        var plan = new VideoClipPlan("c", new SourceKey("m", "m.mp4"), map, Time.Zero, S(2), S(3), Rational.Fps30, Rational.Fps30,
            null, sampling: FrameSampling.Blend);
        FrameSample sample = plan.SampleAt(Time.FromSeconds(fraction / 30));
        Assert.Equal(paired ? 1 : 0, sample.B);
        Assert.Equal(paired ? (float)fraction : 0, sample.T, 6);
        Assert.Equal(paired ? FrameSampling.Blend : FrameSampling.Nearest, sample.Mode);
    }

    [Theory]
    [InlineData(4, false, FrameSampling.OpticalFlow)]
    [InlineData(5, false, FrameSampling.Blend)]
    [InlineData(5, true, FrameSampling.Blend)]
    public void Optical_flow_falls_back_only_above_four_times(int rate, bool reverse, FrameSampling expected)
    {
        var speed = new ClipSpeed { Rate = new Rational(rate, 1), Reverse = reverse };
        ClipTimeMap map = ClipTimeMap.Create(Time.Zero, S(2), reverse ? S(20) : S(2), speed);
        var plan = new VideoClipPlan("c", new SourceKey("m", "m.mp4"), map, Time.Zero, S(2), S(30), Rational.Fps30, Rational.Fps30,
            null, sampling: FrameSampling.OpticalFlow);
        FrameSample sample = plan.SampleAt(S(0.013));
        Assert.True(sample.T > 0);
        Assert.Equal(expected, sample.Mode);
        Assert.Equal(plan.FrameAt(S(0.013)), sample.A);
    }

    [Theory]
    [InlineData(false, false, FrameSampling.Nearest)]
    [InlineData(true, false, FrameSampling.Blend)]
    [InlineData(false, true, FrameSampling.OpticalFlow)]
    public void Nearest_stills_and_freezes_have_no_pair(bool still, bool frozen, FrameSampling sampling)
    {
        var speed = new ClipSpeed { Freeze = frozen };
        ClipTimeMap map = ClipTimeMap.Create(Time.Zero, S(2), S(0.01), speed);
        var plan = new VideoClipPlan("c", new SourceKey("m", "m.mp4"), map, Time.Zero, S(2), S(5), Rational.Fps30, Rational.Fps30,
            null, isStillImage: still, sampling: sampling);
        FrameSample sample = plan.SampleAt(S(0.05));
        Assert.Equal(new FrameSample(plan.FrameAt(S(0.05)), plan.FrameAt(S(0.05)), 0, FrameSampling.Nearest), sample);
    }

    [Fact]
    public void One_thousand_paused_refreshes_and_seeks_release_both_frames()
    {
        var backend = new FakeTimelineBackend { ManualDecoding = true };
        TimelineBuilder b = SlowClip(backend, new Rational(1, 4), FrameSampling.Blend);
        var seen = new HashSet<DecodedFrame>();
        using (TimelineSession session = Open(b, backend))
        {
            session.SeekToFrame(1);
            Settle(session, backend).Dispose();
            for (int refresh = 0; refresh < 1000; refresh++)
            {
                if (refresh % 5 == 0)
                {
                    session.SeekToFrame(((refresh * 17 % 200) * 4) + 1);
                    using CompositionFrame held = session.Tick();
                    CheckPair(Assert.Single(held.Layers), 0.25f, seen);
                }

                using CompositionFrame frame = Settle(session, backend);
                CheckPair(Assert.Single(frame.Layers), 0.25f, seen);
            }
        }

        backend.RunDecoders();
        Assert.Equal(0, backend.Live.Value);
        Assert.All(seen, frame => Assert.True(frame.IsReleased));
    }

    [Fact]
    public void A_seek_waits_for_B_even_when_A_is_ready()
    {
        var backend = new FakeTimelineBackend { ManualDecoding = true, HangFrame = (_, index) => index == 31 };
        TimelineBuilder b = SlowClip(backend, new Rational(1, 4), FrameSampling.Blend);
        using (TimelineSession session = Open(b, backend))
        {
            session.SeekToFrame(1);
            backend.RunDecoders();
            using CompositionFrame frame = session.Tick();
            Assert.True(session.IsSeekPending);
            Assert.Empty(frame.Layers);
            Assert.Contains(30, backend.Videos.SelectMany(v => v.Requests));
            Assert.Contains(31, backend.Videos.SelectMany(v => v.Requests));
        }

        backend.RunDecoders();
        Assert.Equal(0, backend.Live.Value);
    }

    [Fact]
    public void Playing_does_not_wait_for_B()
    {
        var backend = new FakeTimelineBackend { ManualDecoding = true, HangFrame = (_, index) => index == 31 };
        TimelineBuilder b = SlowClip(backend, new Rational(1, 4), FrameSampling.Blend);
        var time = new ManualTimeProvider();
        using TimelineSession session = Open(b, backend, time);
        Settle(session, backend).Dispose();
        session.Play();
        time.Advance(TimeSpan.FromMilliseconds(34));
        session.Tick().Dispose();
        backend.RunDecoders();
        using CompositionFrame frame = session.Tick();
        LayerFrame layer = Assert.Single(frame.Layers);
        Assert.False(session.IsSeekPending);
        Assert.Equal(30, layer.Frame.Index);
        Assert.Null(layer.Next);
        Assert.Equal(0, layer.Fraction);
        Assert.Equal(FrameSampling.Nearest, layer.Sampling);
        Assert.Contains(31, backend.Videos.SelectMany(v => v.Requests));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Offline_half_speed_delivers_and_releases_the_pair(bool reverse)
    {
        var backend = new FakeTimelineBackend();
        TimelineBuilder b = SlowClip(backend, new Rational(1, 2), FrameSampling.Blend);
        b.Sequence.VideoTracks[0].Clips[0].Speed!.Reverse = reverse;
        var seen = new HashSet<DecodedFrame>();
        using (OfflineTimeline offline = OfflineProjects.Open(b, backend))
        {
            for (long f = 0; f < 120; f++)
            {
                using CompositionFrame frame = offline.Render(f, CancellationToken.None);
                LayerFrame layer = Assert.Single(frame.Layers);
                if (f % 2 == 1)
                {
                    CheckPair(layer, 0.5f, seen, reverse);
                }
                else
                {
                    Assert.Null(layer.Next);
                    Assert.Equal(0, layer.Fraction);
                    Assert.Equal(FrameSampling.Nearest, layer.Sampling);
                }
            }
        }

        Assert.Equal(0, backend.Live.Value);
        Assert.All(seen, frame => Assert.True(frame.IsReleased));
    }

    [Fact]
    public void Nearest_preview_and_export_never_carry_Next()
    {
        var backend = new FakeTimelineBackend();
        TimelineBuilder b = SlowClip(backend, new Rational(1, 2), FrameSampling.Nearest);
        using TimelineSession preview = Open(b, backend);
        using OfflineTimeline offline = OfflineProjects.Open(b, backend);
        preview.SeekToFrame(1);
        using CompositionFrame expected = Settle(preview, backend);
        using CompositionFrame actual = offline.Render(1, CancellationToken.None);
        foreach (LayerFrame layer in expected.Layers.Concat(actual.Layers))
        {
            Assert.Null(layer.Next);
            Assert.Equal(0, layer.Fraction);
            Assert.Equal(FrameSampling.Nearest, layer.Sampling);
        }
    }

    [Fact]
    public void Transition_companions_carry_their_own_pair_in_preview_and_export()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Rational.Fps30, 3000, backend: backend);
        Clip first = SpeedClips.WithRate(b.AddVideo(0, "m", Time.Zero, S(1), S(4), "a"), new Rational(1, 2), S(1), S(3));
        Clip second = SpeedClips.WithRate(b.AddVideo(0, "m", S(4), S(10), S(4), "b"), new Rational(1, 2), S(10), S(12));
        first.Speed!.FrameSampling = FrameSampling.Blend;
        second.Speed!.FrameSampling = FrameSampling.Blend;
        TimelineEffectsTests.AddTransition(b, BuiltInTransitions.Crossfade, "a", "b", S(1));
        var seen = new HashSet<DecodedFrame>();
        using (TimelineSession preview = Open(b, backend))
        using (OfflineTimeline offline = OfflineProjects.Open(b, backend))
        {
            preview.SeekToFrame(121);
            using CompositionFrame shown = Settle(preview, backend);
            using CompositionFrame exported = offline.Render(121, CancellationToken.None);
            foreach (LayerFrame layer in shown.Layers.Concat(exported.Layers))
            {
                CheckPair(layer, 0.5f, seen);
                Assert.NotNull(layer.Transition);
                CheckPair(layer.Transition.Value.Incoming, 0.5f, seen);
            }
        }

        backend.RunDecoders();
        Assert.Equal(0, backend.Live.Value);
        Assert.All(seen, frame => Assert.True(frame.IsReleased));
    }

    [Fact]
    public void Compound_children_interpolate_while_the_group_and_title_have_no_pair()
    {
        var backend = new FakeTimelineBackend();
        TimelineBuilder b = SlowClip(backend, new Rational(1, 4), FrameSampling.Blend);
        string id = b.Sequence.VideoTracks[0].Clips[0].Id;
        new UndoStack(b.Project).Execute(Edits.CreateCompound(b.Sequence.Id, [id], "Compuesto"));
        Clip compound = Assert.Single(b.Sequence.VideoTracks[0].Clips);
        compound.Effects.Add(TimelineEffectsTests.Effect(BuiltInEffects.Transform, (BuiltInEffects.TransformParameters.Opacity, 0.5)));
        b.Sequence.VideoTracks.Add(new Track { Id = "title", Kind = TrackKind.Video });
        b.AddTitle(1, Time.Zero, S(2), "Título");
        var seen = new HashSet<DecodedFrame>();
        using (TimelineSession preview = Open(b, backend))
        using (OfflineTimeline offline = OfflineProjects.Open(b, backend))
        {
            preview.SeekToFrame(1);
            using CompositionFrame shown = Settle(preview, backend);
            using CompositionFrame exported = offline.Render(1, CancellationToken.None);
            foreach (CompositionFrame frame in new[] { shown, exported })
            {
                Assert.Equal(2, frame.Layers.Count);
                foreach (LayerFrame layer in frame.Layers)
                {
                    Assert.Null(layer.Next);
                    Assert.Equal(0, layer.Fraction);
                    Assert.Equal(FrameSampling.Nearest, layer.Sampling);
                }

                CheckPair(Assert.Single(frame.Layers[0].Nested!), 0.25f, seen);
                Assert.NotNull(frame.Layers[1].Generated);
            }
        }

        Assert.Equal(0, backend.Live.Value);
        Assert.All(seen, frame => Assert.True(frame.IsReleased));
    }

    [Fact]
    public void Playing_prefetches_and_retains_B_when_FrameAt_skips_it_and_at_the_end_of_a_slow_clip()
    {
        var backend = new FakeTimelineBackend { ManualDecoding = true };
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Rational.Fps30, 3000, backend: backend);
        Clip fast = SpeedClips.WithRate(b.AddVideo(0, "m", Time.Zero, Time.Zero, S(2), "a"), new Rational(3, 2), Time.Zero, S(3));
        fast.Speed!.FrameSampling = FrameSampling.Blend;
        Clip slow = SpeedClips.WithRate(b.AddVideo(0, "m", S(2), S(10), S(1), "b"), new Rational(1, 2), S(10), S(11));
        slow.Speed!.FrameSampling = FrameSampling.Blend;

        TimelinePlan plan = b.Plan();
        VideoClipPlan fastPlan = plan.Layers[0].Clips[0];
        VideoClipPlan slowPlan = plan.Layers[0].Clips[1];
        Assert.True(fastPlan.IsShown(2));
        Assert.Equal(2, fastPlan.NextShown(2));
        Assert.Equal(2, fastPlan.PreviousShown(2));
        Assert.Equal(330, slowPlan.LastFrame);
        Assert.True(slowPlan.IsShown(330));

        var time = new ManualTimeProvider();
        using TimelineSession session = TimelineSessionTests.Open(b, backend, new TimelineSessionOptions
        {
            EnableAudio = false,
            TimeProvider = time,
            PrefetchFrames = 8,
        });
        Settle(session, backend).Dispose();
        session.ResetStats();
        session.Play();

        int interpolated = 0;
        for (int refresh = 0; refresh < 238; refresh++)
        {
            time.Advance(TimeSpan.FromTicks(166_667));
            using (CompositionFrame frame = session.Tick())
            {
                LayerFrame layer = Assert.Single(frame.Layers);
                VideoClipPlan active = frame.TimelineTime < S(2) ? fastPlan : slowPlan;
                FrameSample sample = active.SampleAt(frame.TimelineTime);
                Assert.Equal(sample.A, layer.Frame.Index);
                if (sample.T > 0)
                {
                    Assert.NotNull(layer.Next);
                    Assert.Equal(sample.B, layer.Next.Index);
                    Assert.Equal(sample.T, layer.Fraction, 6);
                    Assert.Equal(FrameSampling.Blend, layer.Sampling);
                    interpolated++;
                }
            }

            backend.RunDecoders();
        }

        Assert.True(interpolated >= 100, $"Solo {interpolated} refrescos interpolados.");
        Assert.Equal(0, session.Stats.FramesDropped);
        Assert.Equal(0, session.Stats.LateRefreshes);
    }

    [Fact]
    public void Offline_reverse_with_blend_retains_B_in_the_gop_reader_without_extra_seeks()
    {
        foreach (Rational rate in new[] { new Rational(3, 2), new Rational(1, 2) })
        {
            var backend = new FakeTimelineBackend { KeyframeInterval = 15 };
            var b = new TimelineBuilder(1, 0);
            b.AddMedia("m", Rational.Fps30, 3000, backend: backend);
            Clip clip = SpeedClips.Reversed(b.AddVideo(0, "m", Time.Zero, S(10), S(1)), rate, S(10), S(10) + Time.FromSeconds(rate.ToDouble()));
            clip.Speed!.FrameSampling = FrameSampling.Blend;

            var seen = new HashSet<DecodedFrame>();
            using (OfflineTimeline offline = OfflineProjects.Open(b, backend))
            {
                for (long f = 0; f < offline.FrameCount; f++)
                {
                    using CompositionFrame frame = offline.Render(f, CancellationToken.None);
                    LayerFrame layer = Assert.Single(frame.Layers);
                    if (f % 2 == 1)
                    {
                        CheckPair(layer, 0.5f, seen, reverse: true);
                    }
                }
            }

            FakeMediaVideo video = Assert.Single(backend.Videos);
            int gops = rate == new Rational(3, 2) ? 3 : 1;
            Assert.True(video.Seeks <= gops + 1, $"Rate {rate}: {video.Seeks} seeks (máximo esperado {gops + 1}).");
            Assert.Equal(0, backend.Live.Value);
        }
    }

    [Fact]
    public void Switching_to_a_proxy_never_pairs_B_from_another_variant_with_Frame()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rodaje-pruebas", "interp-proxy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string original = Path.Combine(dir, "original.mp4");
            File.WriteAllBytes(original, [1, 2, 3, 4]);
            var store = new ProxyStore(Path.Combine(dir, "almacen"));
            var backend = new FakeTimelineBackend { ManualDecoding = true };
            MediaInfo info = Samples.Info(Rational.Fps30, 3000, audio: false, 3840, 2160) with { Path = original };
            backend.Register(original, info);

            var b = new TimelineBuilder(1, 0);
            b.Project.Media.Add(new MediaRef
            {
                Id = "m",
                Path = original,
                Name = "m",
                Duration = info.Duration,
                HasVideo = true,
                FrameRate = Rational.Fps30,
                Width = 3840,
                Height = 2160,
            });
            Clip clip = SpeedClips.WithRate(b.AddVideo(0, "m", Time.Zero, S(1), S(4)), new Rational(1, 2), S(1), S(3));
            clip.Speed!.FrameSampling = FrameSampling.Blend;

            using var resolver = new MediaSourceResolver(store, generator: null);
            using TimelineSession session = TimelineSessionTests.Open(b, backend, new TimelineSessionOptions
            {
                EnableAudio = false,
                TimeProvider = new ManualTimeProvider(),
                ProxyResolver = resolver,
                UseProxies = ProxyUsage.Auto,
                PrefetchFrames = 1,
            });
            session.SeekToFrame(1);
            using (CompositionFrame initial = Settle(session, backend))
            {
                LayerFrame layer = Assert.Single(initial.Layers);
                Assert.Equal(original, ((FakeFramePayload)layer.Frame.Payload!).Path);
                Assert.NotNull(layer.Next);
                Assert.Equal(original, ((FakeFramePayload)layer.Next.Payload!).Path);
            }

            SourceFingerprint fp = SourceFingerprint.Compute(original);
            string temp = store.CreateTemporaryPath(fp, ProxySettings.Default);
            File.WriteAllBytes(temp, [5, 6, 7, 8]);
            ProxyEntry proxy = store.Commit(temp, new ProxyMetadata
            {
                SourcePath = fp.Path,
                SourceKey = fp.Key,
                SourceSize = fp.Size,
                SourceLastWriteUtc = fp.LastWriteUtc,
                SettingsTag = ProxySettings.Default.Tag,
                TargetHeight = 540,
                GopLength = 15,
                Encoder = "prueba",
                Width = 960,
                Height = 540,
                FrameRateNum = 30,
                FrameRateDen = 1,
                FrameCount = 3000,
                SourceFrameCount = 3000,
                SourceStartTicks = 0,
                ProxyStartTicks = 0,
                SourceDurationTicks = info.Duration.Ticks,
                ProxyDurationTicks = info.Duration.Ticks,
                CreatedUtc = DateTime.UtcNow,
            }, ProxySettings.Default);
            backend.Register(proxy.ProxyPath, Samples.Info(Rational.Fps30, 3000, audio: false, 960, 540) with { Path = proxy.ProxyPath });

            backend.FailFrames.Add((proxy.ProxyPath, 31));
            resolver.Resolve(original, MediaPurpose.Preview);
            bool sawProxyWithoutNext = false;
            for (int i = 0; i < 4; i++)
            {
                backend.RunDecoders();
                using CompositionFrame partial = session.Tick();
                LayerFrame layer = Assert.Single(partial.Layers);
                if (layer.IsProxy)
                {
                    Assert.Equal(proxy.ProxyPath, ((FakeFramePayload)layer.Frame.Payload!).Path);
                    Assert.Null(layer.Next);
                    sawProxyWithoutNext = true;
                }
            }

            Assert.True(sawProxyWithoutNext);
            backend.FailFrames.Clear();
            session.SeekToFrame(1);
            using CompositionFrame settled = Settle(session, backend);
            LayerFrame finalLayer = Assert.Single(settled.Layers);
            Assert.True(finalLayer.IsProxy);
            Assert.Equal(proxy.ProxyPath, ((FakeFramePayload)finalLayer.Frame.Payload!).Path);
            Assert.NotNull(finalLayer.Next);
            Assert.Equal(proxy.ProxyPath, ((FakeFramePayload)finalLayer.Next.Payload!).Path);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void Offline_prefetch_does_not_enqueue_A_alone_when_only_one_slot_remains_for_a_new_pair()
    {
        var backend = new FakeTimelineBackend { ManualDecoding = true };
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Rational.Fps30, 3000, backend: backend);
        Clip clip = SpeedClips.WithRate(b.AddVideo(0, "m", Time.Zero, S(1), S(4)), new Rational(3, 2), S(1), S(7));
        clip.Speed!.FrameSampling = FrameSampling.Blend;
        VideoClipPlan plan = b.Plan().Layers[0].Clips[0];

        using var readers = new OfflineReaders(backend, new Rodaje.Media.Video.VideoDecoderOptions(), maxPerSource: 1, lookahead: 2, vramBudget: 1L << 30);
        Assert.NotNull(readers.RequestSample(plan, Time.Zero, mustHave: true));
        Assert.Equal(1, readers.Slots);

        Assert.False(readers.HasRoomForSample(plan, F(1, Rational.Fps30)));
    }

    private static TimelineBuilder SlowClip(FakeTimelineBackend backend, Rational rate, FrameSampling sampling)
    {
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Rational.Fps30, 3000, backend: backend);
        Clip clip = SpeedClips.WithRate(b.AddVideo(0, "m", Time.Zero, S(1), S(10)), rate, S(1), S(11));
        clip.Speed!.FrameSampling = sampling;
        return b;
    }

    private static TimelineSession Open(TimelineBuilder b, FakeTimelineBackend backend, ManualTimeProvider? time = null) =>
        TimelineSessionTests.Open(b, backend, new TimelineSessionOptions
        {
            EnableAudio = false,
            TimeProvider = time ?? new ManualTimeProvider(),
            PrefetchFrames = 1,
        });

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

        throw new Xunit.Sdk.XunitException("No llegó el par de cuadros.");
    }

    private static void CheckPair(LayerFrame layer, float fraction, HashSet<DecodedFrame> seen, bool reverse = false)
    {
        Assert.NotNull(layer.Next);
        Assert.Equal(layer.Frame.Index + (reverse ? -1 : 1), layer.Next.Index);
        Assert.Equal(fraction, layer.Fraction, 6);
        Assert.Equal(FrameSampling.Blend, layer.Sampling);
        Assert.False(layer.Frame.IsReleased);
        Assert.False(layer.Next.IsReleased);
        seen.Add(layer.Frame);
        seen.Add(layer.Next);
    }
}
