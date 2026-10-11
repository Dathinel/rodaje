using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Editing.Speed;

public sealed class SpeedEditsTests
{
    private static readonly Rational Two = new(2, 1);
    private static readonly Rational Half = new(1, 2);

    [Fact]
    public void SetSpeed_keeps_the_material_and_changes_the_duration_of_the_whole_link()
    {
        var f = new TrimFixture()
            .Add("v1", "v", "m1", 10, 50, 0, link: "L")
            .Add("a1", "a", "m1", 10, 50, 0, link: "L");

        TimelineEdits.SetSpeed(f.Ctx, "v", Two);

        f.AssertValid();
        foreach (string id in (string[])["v", "a"])
        {
            Clip clip = f.Clip(id);
            Assert.Equal(Two, clip.Speed!.Rate);
            Assert.Equal(F(20), clip.Duration);
            Assert.Equal(F(10), clip.SourceIn);
            Assert.Equal(F(50), clip.SourceOut);
        }
    }

    [Fact]
    public void SetSpeed_aligns_the_end_to_a_frame_without_eating_material_when_repeated()
    {
        var f = new TrimFixture().Add("v1", "c", "m1", 10, 50, 0);
        Rational[] rates = [new(37, 100), new(13, 7), new(1001, 1000), new(21, 10), new(3, 10), new(5, 2), new(7, 9)];

        for (int round = 0; round < 6; round++)
        {
            foreach (Rational rate in rates)
            {
                TimelineEdits.SetSpeed(f.Ctx, "c", rate);
                Clip clip = f.Clip("c");
                Assert.Equal(clip.End, clip.End.SnapToFrame(Rational.Fps25));
                f.AssertValid();
            }
        }

        TimelineEdits.SetSpeed(f.Ctx, "c", new Rational(1, 1));
        Clip back = f.Clip("c");
        Assert.Null(back.Speed);
        Assert.Equal(F(10), back.SourceIn);
        // Redondear al cuadro más cercano no come material de forma sistemática: tras 42 cambios queda a pocos cuadros.
        Assert.True(Math.Abs((back.SourceOut - F(50)).Ticks) <= F(4).Ticks, $"material {back.SourceOut}");
    }

    [Fact]
    public void Slowing_down_pushes_only_what_it_overlaps_and_its_links()
    {
        var f = new TrimFixture()
            .Add("v1", "c", "m1", 0, 20, 0)
            .Add("v1", "n", "m2", 0, 10, 30, link: "N")
            .Add("a1", "na", "m2", 0, 10, 30, link: "N")
            .Add("a1", "far", "m3", 0, 5, 45);

        TimelineEdits.SetSpeed(f.Ctx, "c", Half);

        f.AssertValid();
        Assert.Equal(F(40), f.Clip("c").End);
        Assert.Equal(F(40), f.Clip("n").Start);
        Assert.Equal(F(40), f.Clip("na").Start);
        Assert.Equal(F(55), f.Clip("far").Start);

        var g = new TrimFixture()
            .Add("v1", "c", "m1", 0, 20, 0)
            .Add("v1", "n", "m2", 0, 10, 60);

        TimelineEdits.SetSpeed(g.Ctx, "c", Half);

        Assert.Equal(F(60), g.Clip("n").Start);
    }

    [Fact]
    public void Speeding_up_on_a_free_track_joins_the_next_clip_by_magnetism()
    {
        var f = new TrimFixture()
            .Add("v1", "c", "m1", 0, 20, 0)
            .Add("v1", "n", "m2", 0, 10, 20)
            .Add("v2", "otra", "m2", 0, 10, 20);

        TimelineEdits.SetSpeed(f.Ctx, "c", Two);

        Assert.Equal(F(10), f.Clip("n").Start);
        Assert.Equal(F(20), f.Clip("otra").Start);
        f.AssertValid();
    }

    [Fact]
    public void Growing_into_a_gap_moves_nothing_even_with_all_tracks()
    {
        var f = new TrimFixture()
            .Add("v1", "c", "m1", 0, 20, 0)
            .Add("v1", "n", "m2", 0, 10, 60)
            .Add("v2", "otra", "m2", 0, 10, 25);
        SpeedSamples.Add(f, "v2", "z", "m2", 0, 10, "congelado");

        TimelineEdits.SetSpeed(f.Ctx, "c", Half, RippleScope.AllTracks);
        TimelineEdits.RateStretch(f.Ctx, "z", TrimEdge.Out, F(10));

        Assert.Equal(F(40), f.Clip("c").End);
        Assert.Equal(F(60), f.Clip("n").Start);
        Assert.Equal(F(20), f.Clip("z").End);
        Assert.Equal(F(25), f.Clip("otra").Start);
        f.AssertValid();

        TimelineEdits.RateStretch(f.Ctx, "z", TrimEdge.Out, F(10));

        Assert.Equal(F(30), f.Clip("otra").Start);
        f.AssertValid();
    }

    [Fact]
    public void With_the_all_tracks_mode_every_track_and_the_markers_follow()
    {
        var f = new TrimFixture()
            .Add("v1", "c", "m1", 0, 20, 0)
            .Add("v1", "n", "m2", 0, 10, 20)
            .Add("v2", "otra", "m2", 0, 10, 30);
        f.Sequence.Markers.Add(new Marker { Id = "mk", Time = F(25) });
        var g = new TrimFixture()
            .Add("v1", "c", "m1", 0, 20, 0)
            .Add("v2", "otra", "m2", 0, 10, 30);
        g.Sequence.Markers.Add(new Marker { Id = "mk", Time = F(25) });

        TimelineEdits.SetSpeed(f.Ctx, "c", Two, RippleScope.AllTracksAndMarkers);
        TimelineEdits.FreezeFrame(g.Ctx, "c", F(5), F(10), RippleScope.AllTracks);

        Assert.Equal(F(10), f.Clip("n").Start);
        Assert.Equal(F(20), f.Clip("otra").Start);
        Assert.Equal(F(15), f.Sequence.Markers[0].Time);
        Assert.Equal(F(40), g.Clip("otra").Start);
        Assert.Equal(F(25), g.Sequence.Markers[0].Time);
        f.AssertValid();
        g.AssertValid();
    }

    [Fact]
    public void Linked_clips_of_different_length_push_each_track_by_its_own_delta()
    {
        var f = new TrimFixture()
            .Add("v1", "v", "m1", 0, 20, 0, link: "L")
            .Add("a1", "a", "m1", 0, 30, 0, link: "L")
            .Add("v1", "nv", "m2", 0, 10, 20)
            .Add("a1", "na", "m2", 0, 10, 30);

        TimelineEdits.SetSpeed(f.Ctx, "v", Half);

        f.AssertValid();
        Assert.Equal(F(40), f.Clip("nv").Start);
        Assert.Equal(F(60), f.Clip("na").Start);
    }

    [Fact]
    public void SetSpeed_rejects_bad_rates_and_frozen_clips()
    {
        var f = new TrimFixture();
        SpeedSamples.Add(f, "v1", "z", "m2", 0, 10, "congelado");
        f.Add("v1", "c", "m1", 0, 20, 20);

        AssertFails(() => TimelineEdits.SetSpeed(f.Ctx, "c", new Rational(101, 1)), "fuera de 0,1× a 100×");
        AssertFails(() => TimelineEdits.SetSpeed(f.Ctx, "z", Two), "congelado no tiene velocidad");
        AssertFails(() => TimelineEdits.SetReverse(f.Ctx, "z", true), "congelado no se puede invertir");
    }

    [Fact]
    public void A_clip_too_short_for_the_speed_is_refused()
    {
        var f = new TrimFixture();
        f.Track("v1").Clips.Add(new Clip { Id = "c", MediaId = "m1", SourceIn = F(1), SourceOut = F(1) + Time.FromTicks(1), Start = F(0) });

        AssertFails(() => TimelineEdits.SetSpeed(f.Ctx, "c", new Rational(100, 1)), "demasiado corto");
    }

    [Fact]
    public void SetSpeed_on_a_reversed_clip_keeps_the_reverse()
    {
        var f = new TrimFixture();
        SpeedSamples.Add(f, "v1", "c", "m2", 0, 40, "reversa 3/2");
        Time first = f.Clip("c").ToSourceTime(Time.Zero);

        TimelineEdits.SetSpeed(f.Ctx, "c", Two);

        Clip clip = f.Clip("c");
        Assert.True(clip.Speed!.Reverse);
        Assert.Equal(first, clip.ToSourceTime(Time.Zero));
        f.AssertValid();
    }

    [Fact]
    public void RateStretch_out_edge_reaches_the_dragged_length_with_the_same_material()
    {
        var f = new TrimFixture().Add("v1", "c", "m1", 10, 50, 0).Add("v1", "n", "m2", 0, 10, 40);

        TimelineEdits.RateStretch(f.Ctx, "c", TrimEdge.Out, F(20));

        Clip clip = f.Clip("c");
        Assert.Equal(new Rational(2, 3), clip.Speed!.Rate);
        Assert.Equal(F(60), clip.End);
        Assert.Equal(F(10), clip.SourceIn);
        Assert.Equal(F(50), clip.SourceOut);
        Assert.Equal(F(60), f.Clip("n").Start);
        f.AssertValid();
    }

    [Fact]
    public void RateStretch_in_edge_keeps_the_start_and_the_rest_follows()
    {
        var f = new TrimFixture().Add("v1", "c", "m1", 10, 50, 20).Add("v1", "n", "m2", 0, 10, 60).Add("v2", "otra", "m2", 0, 10, 70);
        var g = new TrimFixture().Add("v1", "c", "m1", 10, 50, 20).Add("v1", "n", "m2", 0, 10, 60).Add("v2", "otra", "m2", 0, 10, 70);

        TimelineEdits.RateStretch(f.Ctx, "c", TrimEdge.In, F(20));
        TimelineEdits.RateStretch(g.Ctx, "c", TrimEdge.In, F(20), RippleScope.AllTracks);

        Assert.Equal(F(20), f.Clip("c").Start);
        Assert.Equal(F(40), f.Clip("c").End);
        Assert.Equal(F(40), f.Clip("n").Start);
        Assert.Equal(F(70), f.Clip("otra").Start);
        Assert.Equal(F(50), g.Clip("otra").Start);
        Assert.Equal(Two, f.Clip("c").Speed!.Rate);
        f.AssertValid();
        g.AssertValid();
    }

    [Fact]
    public void RateStretch_keeps_linked_clips_with_their_own_length()
    {
        var f = new TrimFixture()
            .Add("v1", "v", "m1", 0, 40, 0, link: "L")
            .Add("a1", "a", "m1", 0, 20, 0, link: "L");

        TimelineEdits.RateStretch(f.Ctx, "v", TrimEdge.Out, -F(20));

        Assert.Equal(F(20), f.Clip("v").Duration);
        Assert.Equal(F(10), f.Clip("a").Duration);
        f.AssertValid();
    }

    [Fact]
    public void RateStretch_stops_at_the_speed_limits_and_on_frozen_and_curved_clips()
    {
        var f = new TrimFixture().Add("v1", "c", "m1", 0, 50, 0);
        SpeedSamples.Add(f, "v2", "z", "m2", 0, 10, "congelado");
        SpeedSamples.Add(f, "v2", "k", "m2", 20, 40, "curva");

        TimelineEdits.RateStretch(f.Ctx, "c", TrimEdge.Out, F(5000));
        Assert.Equal(RetimeMath.MinRate, f.Clip("c").Speed!.Rate);
        Assert.Equal(F(500), f.Clip("c").Duration);

        TimelineEdits.RateStretch(f.Ctx, "z", TrimEdge.Out, F(5));
        Assert.Equal(F(15), f.Clip("z").Duration);
        TimelineEdits.RateStretch(f.Ctx, "z", TrimEdge.In, F(2));
        Assert.Equal(F(13), f.Clip("z").Duration);

        AssertFails(() => TimelineEdits.RateStretch(f.Ctx, "k", TrimEdge.Out, F(5)), "curva");
        AssertFails(() => TimelineEdits.RateStretch(f.Ctx, "c", TrimEdge.Out, -F(500)), "sin duración");
    }

    [Fact]
    public void RateStretch_falls_back_when_the_file_has_no_material_for_the_exact_length()
    {
        var f = new TrimFixture().Add("v1", "c", "m1", 0, 100, 0);

        TimelineEdits.RateStretch(f.Ctx, "c", TrimEdge.Out, Time.FromTicks(3));

        Clip clip = f.Clip("c");
        Assert.Equal(F(100), clip.SourceOut);
        Assert.Equal(F(100), clip.Duration);
        f.AssertValid();
    }

    [Theory]
    [InlineData(3, 1, 3, 1)]
    [InlineData(1, 1000, 1, 10)]
    [InlineData(1000, 1, 100, 1)]
    [InlineData(355_000_000, 113_000_000, 355, 113)]
    [InlineData(314_159_265, 100_000_000, 3, 1)]
    public void ApproximateRate_uses_continued_fractions_with_a_small_denominator(long material, long duration, long num, long den)
    {
        Rational rate = TimelineEdits.ApproximateRate(material, duration);

        if (material == 314_159_265)
        {
            Assert.True(rate.Den <= TimelineEdits.StretchMaxDenominator);
            Assert.Equal(new Rational(355, 113), rate);
        }
        else
        {
            Assert.Equal(new Rational(num, den), rate);
        }
    }

    [Fact]
    public void ApproximateRate_is_the_best_fraction_up_to_the_limit()
    {
        var random = new Random(5);
        for (int i = 0; i < 2000; i++)
        {
            long material = random.NextInt64(1, 1L << 40);
            long duration = Math.Max(1, (long)(material / (0.1 + (random.NextDouble() * 99.9))));
            Rational rate = TimelineEdits.ApproximateRate(material, duration);
            double x = (double)material / duration;
            Assert.True(rate.Den <= TimelineEdits.StretchMaxDenominator);
            Assert.True(RetimeMath.IsValidRate(rate));
            double error = Math.Abs(rate.ToDouble() - Math.Clamp(x, 0.1, 100));
            for (long q = Math.Max(1, rate.Den - 3); q <= Math.Min(TimelineEdits.StretchMaxDenominator, rate.Den + 3); q++)
            {
                double best = Math.Round(Math.Clamp(x, 0.1, 100) * q) / q;
                Assert.True(error <= Math.Abs(best - Math.Clamp(x, 0.1, 100)) + 1e-12, $"{material}/{duration}: {rate} pierde con {best}");
            }
        }
    }

    [Fact]
    public void FreezeFrame_splits_inserts_three_seconds_and_leaves_a_gap_in_the_audio()
    {
        var f = new TrimFixture()
            .Add("v1", "v", "m1", 10, 60, 0, link: "L")
            .Add("a1", "a", "m1", 10, 60, 0, link: "L")
            .Add("v1", "n", "m2", 0, 10, 50);
        f.Clip("v").Effects.Add(new EffectInstance { Id = "fx", Type = BuiltInEffects.Transform });
        Time at = F(20);
        Time shown = f.Clip("v").ToSourceTime(at);

        string id = TimelineEdits.FreezeFrame(f.Ctx, "v", at);

        f.AssertValid();
        Clip frozen = f.Clip(id);
        Assert.True(frozen.Speed!.Freeze);
        Assert.Equal(at, frozen.Start);
        Assert.Equal(Time.FromSeconds(3), frozen.Duration);
        Assert.Equal(shown, frozen.ToSourceTime(at + F(40)));
        Assert.Null(frozen.LinkId);
        Assert.Single(frozen.Effects);
        Assert.NotEqual("fx", frozen.Effects[0].Id);
        Time after = at + Time.FromSeconds(3);
        Assert.Equal(["v", id, "clip-1", "n"], f.Track("v1").Clips.Select(c => c.Id));
        Assert.Equal(after, f.Clip("clip-1").Start);
        Assert.Equal(F(50) + Time.FromSeconds(3), f.Clip("n").Start);
        Assert.Equal(at, f.Clip("a").End);
        Assert.Equal(after, f.Track("a1").Clips[1].Start);
    }

    [Fact]
    public void FreezeFrame_at_the_start_pushes_the_whole_clip()
    {
        var f = new TrimFixture().Add("v1", "v", "m1", 10, 60, 5);

        string id = TimelineEdits.FreezeFrame(f.Ctx, "v", F(5), F(10));

        Assert.Equal([id, "v"], f.Track("v1").Clips.Select(c => c.Id));
        Assert.Equal(F(15), f.Clip("v").Start);
        Assert.Equal(F(10), f.Clip(id).SourceIn);
        f.AssertValid();
    }

    [Fact]
    public void FreezeFrame_refuses_audio_instants_outside_and_bad_lengths()
    {
        var f = new TrimFixture().Add("v1", "v", "m1", 10, 60, 5).Add("a1", "a", "m3", 0, 10, 0);

        AssertFails(() => TimelineEdits.FreezeFrame(f.Ctx, "a", F(1)), "clip de video");
        AssertFails(() => TimelineEdits.FreezeFrame(f.Ctx, "v", F(55)), "no cae dentro");
        AssertFails(() => TimelineEdits.FreezeFrame(f.Ctx, "v", F(4)), "no cae dentro");
        AssertFails(() => TimelineEdits.FreezeFrame(f.Ctx, "v", F(6), Time.Zero), "fuera de rango");
        f.Lock("v1");
        AssertFails(() => TimelineEdits.FreezeFrame(f.Ctx, "v", F(6)), "bloqueada");
    }

    [Fact]
    public void SetCurve_covers_the_material_and_null_goes_back_to_normal_speed()
    {
        var f = new TrimFixture().Add("v1", "c", "m1", 0, 60, 0);
        var curve = new SpeedCurve { Span = F(1), Points = [new(0, 200), new(10_000, 200)] };

        TimelineEdits.SetCurve(f.Ctx, "c", curve);

        Clip clip = f.Clip("c");
        Assert.NotNull(clip.Speed!.Curve);
        Assert.Equal(F(30), clip.Speed.Curve!.Span);
        Assert.Equal(F(30), clip.Duration);
        Assert.Equal(F(60), clip.SourceOut);
        f.AssertValid();

        TimelineEdits.SetCurve(f.Ctx, "c", null);
        Assert.Null(f.Clip("c").Speed);
        Assert.Equal(F(60), f.Clip("c").Duration);

        AssertFails(() => TimelineEdits.SetCurve(f.Ctx, "c", new SpeedCurve { Span = F(1), Points = [new(0, 5), new(10_000, 100)] }), "0,1× a 10×");
    }

    [Fact]
    public void SetCurve_with_a_ramp_takes_the_duration_of_its_mean_speed()
    {
        var f = new TrimFixture().Add("v1", "c", "m1", 0, 90, 0);

        TimelineEdits.SetCurve(f.Ctx, "c", new SpeedCurve { Points = [new(0, 100), new(10_000, 200)] });

        Clip clip = f.Clip("c");
        Assert.InRange(clip.Duration.Ticks, F(59).Ticks, F(61).Ticks);
        f.AssertValid();
    }

    [Fact]
    public void SetReverse_mirrors_the_frames_and_the_keyframes_and_twice_is_identity()
    {
        var f = new TrimFixture().Add("v1", "c", "m1", 10, 50, 0, link: "L").Add("a1", "a", "m1", 10, 50, 0, link: "L");
        f.Clip("c").Effects.Add(new EffectInstance
        {
            Id = "fx",
            Type = BuiltInEffects.Transform,
            Parameters =
            {
                ["opacity"] = new Parameter
                {
                    Keyframes = [new(F(10), 0, Interpolation.EaseIn), new(F(15), 1, Interpolation.Hold), new(F(40), 0.5, Interpolation.EaseOut)],
                },
                ["scale"] = new Parameter { Value = 1.2 },
            },
        });
        string original = ProjectSerializer.Serialize(f.Project);
        Clip clip = f.Clip("c");
        double fadeAt = clip.Effects[0].Parameters["opacity"].Evaluate(clip.ToEffectTime(F(2)));

        TimelineEdits.SetReverse(f.Ctx, "c", true);

        f.AssertValid();
        Assert.True(clip.Speed!.Reverse);
        Assert.True(f.Clip("a").Speed!.Reverse);
        Assert.Equal(F(49) + F(1) - Time.FromTicks(1), clip.ToSourceTime(Time.Zero));
        List<Keyframe> mirrored = clip.Effects[0].Parameters["opacity"].Keyframes;
        Assert.Equal([F(20), F(45), F(50)], mirrored.Select(k => k.Time));
        Assert.Equal([Interpolation.Hold, Interpolation.EaseOut, Interpolation.EaseIn], mirrored.Select(k => k.Interpolation));
        Assert.Equal(0.5, mirrored[0].Value);
        Assert.Equal(fadeAt, clip.Effects[0].Parameters["opacity"].Evaluate(clip.ToEffectTime(F(2))), 3);

        TimelineEdits.SetReverse(f.Ctx, "c", true);
        TimelineEdits.SetReverse(f.Ctx, "c", false);

        Assert.Equal(original, ProjectSerializer.Serialize(f.Project));
    }

    [Fact]
    public void SetPreservePitch_changes_only_clips_with_speed()
    {
        var f = new TrimFixture().Add("v1", "c", "m1", 0, 20, 0).Add("v1", "d", "m1", 0, 20, 30);
        TimelineEdits.SetSpeed(f.Ctx, "c", Two);

        TimelineEdits.SetPreservePitch(f.Ctx, "c", false);
        TimelineEdits.SetPreservePitch(f.Ctx, "d", false);

        Assert.False(f.Clip("c").Speed!.PreservePitch);
        Assert.Null(f.Clip("d").Speed);
    }

    [Fact]
    public void Speed_commands_undo_in_one_step_and_merge_while_dragging()
    {
        var f = new TrimFixture().Add("v1", "c", "m1", 10, 50, 0).Add("v1", "n", "m2", 0, 10, 40);
        f.Clip("c").Effects.Add(new EffectInstance
        {
            Id = "fx",
            Type = BuiltInEffects.Transform,
            Parameters = { ["opacity"] = new Parameter { Keyframes = [new(F(10), 0), new(F(20), 1)] } },
        });
        string original = ProjectSerializer.Serialize(f.Project);
        var stack = new UndoStack(f.Project);
        var ids = new SequentialIdGenerator();

        stack.Execute(Edits.SetSpeed("seq", "c", new Rational(3, 2), ids: ids));
        stack.Execute(Edits.SetSpeed("seq", "c", Half, ids: ids));
        stack.Execute(Edits.RateStretch("seq", "c", TrimEdge.Out, F(3), ids: ids));
        stack.Execute(Edits.RateStretch("seq", "c", TrimEdge.Out, F(3), ids: ids));
        stack.Execute(Edits.SetReverse("seq", "c", true, ids));
        stack.Execute(Edits.SetPreservePitch("seq", "c", false, ids));
        stack.Execute(Edits.SetCurve("seq", "c", new SpeedCurve { Points = [new(0, 50), new(10_000, 300)] }, ids: ids));
        SequenceEditCommand freeze = Edits.FreezeFrame("seq", "c", F(5), ids: ids);
        stack.Execute(freeze);
        Assert.Single(freeze.CreatedIds);
        ModelValidator.ThrowIfInvalid(f.Project);

        int steps = 0;
        while (stack.Undo())
        {
            steps++;
        }

        Assert.Equal(6, steps);
        Assert.Equal(original, ProjectSerializer.Serialize(f.Project));
    }

    [Fact]
    public void A_track_reached_from_two_members_with_the_same_delta_shifts_from_the_earliest_point()
    {
        var f = new TrimFixture()
            .Add("v1", "v", "m1", 0, 30, 0, link: "L")
            .Add("a1", "a", "m1", 0, 10, 0, link: "L")
            .Add("v1", "p", "m2", 0, 10, 30, link: "P")
            .Add("a2", "q", "m2", 0, 10, 40, link: "P")
            .Add("a1", "r", "m2", 0, 10, 10, link: "R")
            .Add("a2", "s", "m3", 0, 10, 20, link: "R");

        TimelineEdits.RippleTrim(f.Ctx, "v", TrimEdge.Out, F(5), RippleScope.Track);

        Assert.Equal(F(35), f.Clip("p").Start);
        Assert.Equal(F(15), f.Clip("r").Start);
        Assert.Equal(F(25), f.Clip("s").Start);
        Assert.Equal(F(45), f.Clip("q").Start);
        f.AssertValid();
    }

    [Fact]
    public void Two_halves_longer_together_than_the_maximum_are_not_the_same_clip()
    {
        var f = new TrimFixture();
        f.Project.Media[1].Duration = Time.FromTicks(RetimeMath.MaxExtent * 4);
        long half = RetimeMath.MaxExtent / 2;
        var left = new Clip { Id = "l", MediaId = "m2", Speed = new ClipSpeed { Rate = new Rational(2, 1), Offset = Time.FromTicks(-half) } };
        ClipTimeMap.Create(Time.Zero, Time.FromTicks(half), Time.FromTicks(RetimeMath.MaxExtent), left.Speed).ApplyTo(left);
        var right = new Clip { Id = "r", MediaId = "m2", Speed = new ClipSpeed { Rate = new Rational(2, 1) } };
        ClipTimeMap.Create(left.End, Time.FromTicks(half), left.SourceOut, right.Speed).ApplyTo(right);
        f.Track("v1").Clips.AddRange([left, right]);
        f.Add("v1", "n", "m1", 0, 10, 0);
        f.Clip("n").Start = right.End;
        f.Track("v1").SortClips();
        f.Track("v1").Transitions.Add(new TransitionInstance { Id = "t", Type = BuiltInTransitions.Crossfade, From = "l", To = "n", Duration = F(1) });

        TransitionOps.Reconcile(f.Sequence);

        Assert.Empty(f.Track("v1").Transitions);
    }
}
