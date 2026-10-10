using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Rodaje.Playback.Composition;
using static Rodaje.Engine.Tests.Support.Seconds;

namespace Rodaje.Engine.Tests.Timeline;

public sealed class TimelinePlanTests
{
    private static readonly Rational Fps30 = Rational.Fps30;

    [Fact]
    public void A_cut_belongs_to_the_clip_that_starts_there()
    {
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Rational.Fps30, 3000);
        b.AddVideo(0, "m", Time.Zero, Time.Zero, S(2), "a");
        b.AddVideo(0, "m", S(2), S(10), S(2), "b");
        VideoLayerPlan layer = b.Plan().Layers[0];

        Assert.Equal("a", layer.ClipAt(S(2) - Time.FromTicks(1))?.ClipId);
        Assert.Equal("b", layer.ClipAt(S(2))?.ClipId);
        Assert.Null(layer.ClipAt(S(4)));
        Assert.Equal("b", layer.NextAfter(S(1.5))?.ClipId);
        Assert.Null(layer.NextAfter(S(2)));
    }

    [Fact]
    public void Exact_cut_frames_map_to_the_last_and_first_source_frames()
    {
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Rational.Fps30, 3000);
        b.AddVideo(0, "m", Time.Zero, F(100, Fps30), F(60, Fps30), "a");
        b.AddVideo(0, "m", F(60, Fps30), F(500, Fps30), F(60, Fps30), "b");
        VideoLayerPlan layer = b.Plan().Layers[0];

        VideoClipPlan a = layer.ClipAt(F(59, Fps30))!;
        VideoClipPlan c = layer.ClipAt(F(60, Fps30))!;
        Assert.Equal(159, a.FrameAt(F(59, Fps30)));
        Assert.Equal(159, a.LastFrame);
        Assert.Equal(500, c.FrameAt(F(60, Fps30)));
        Assert.Equal(500, c.FirstFrame);
    }

    [Theory]
    [InlineData(24, 1, 5, 4)]
    [InlineData(24, 1, 6, 4)]
    [InlineData(24, 1, 30, 24)]
    [InlineData(25, 1, 6, 5)]
    [InlineData(25, 1, 7, 5)]
    [InlineData(30000, 1001, 1, 0)]
    [InlineData(30000, 1001, 30, 29)]
    [InlineData(30000, 1001, 31, 30)]
    [InlineData(30000, 1001, 1001, 1000)]
    [InlineData(60, 1, 7, 14)]
    public void Mixed_rate_sources_show_the_frame_that_contains_the_source_time(long num, long den, long sequenceFrame, long expected)
    {
        var rate = new Rational(num, den);
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", rate, 100_000);
        b.AddVideo(0, "m", Time.Zero, Time.Zero, S(60));
        VideoClipPlan clip = b.Plan().Layers[0].Clips[0];

        Assert.Equal(expected, clip.FrameAt(F(sequenceFrame, Fps30)));
    }

    [Fact]
    public void Mixed_rates_with_an_offset_source_in_agree_with_exact_arithmetic()
    {
        foreach (Rational rate in new[] { Rational.Fps24, Rational.Fps25, Rational.Ntsc30, Rational.Ntsc24 })
        {
            var b = new TimelineBuilder(1, 0);
            b.AddMedia("m", rate, 100_000);
            Time sourceIn = S(3.217);
            Time start = F(45, Fps30);
            b.AddVideo(0, "m", start, sourceIn, S(20));
            VideoClipPlan clip = b.Plan().Layers[0].Clips[0];
            for (long f = 45; f < 45 + 600; f++)
            {
                Int128 sourceTicks = Time.FromFrames(f, Fps30).Ticks - start.Ticks + sourceIn.Ticks;
                long expected = (long)(sourceTicks * rate.Num / ((Int128)Time.TicksPerSecond * rate.Den));
                Assert.Equal(expected, clip.FrameAt(F(f, Fps30)));
            }
        }
    }

    [Fact]
    public void Hidden_tracks_disabled_clips_and_media_without_video_are_left_out()
    {
        var b = new TimelineBuilder(3, 0);
        b.AddMedia("m", Rational.Fps30, 3000);
        b.AddMedia("solo-audio", Rational.Fps30, 3000, video: false);
        b.AddVideo(0, "m", Time.Zero, Time.Zero, S(2), "visible");
        b.AddVideo(0, "solo-audio", S(2), Time.Zero, S(2), "sin-video");
        b.AddVideo(0, "falta", S(4), Time.Zero, S(2), "sin-medio");
        b.AddVideo(1, "m", Time.Zero, Time.Zero, S(2), "oculta");
        b.Sequence.VideoTracks[1].Hidden = true;
        b.AddVideo(2, "m", Time.Zero, Time.Zero, S(2), "apagado").Enabled = false;

        TimelinePlan plan = b.Plan();

        Assert.Equal(new[] { 0, 2 }, plan.Layers.Select(l => l.TrackIndex));
        Assert.Equal(new[] { "visible" }, plan.Layers[0].Clips.Select(c => c.ClipId));
        Assert.Empty(plan.Layers[1].Clips);
        Assert.Equal(S(6), plan.Duration);
    }

    [Fact]
    public void Sequence_frame_count_and_frame_start_are_clamped_to_the_last_frame()
    {
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Rational.Fps30, 3000);
        b.AddVideo(0, "m", Time.Zero, Time.Zero, S(2) + Time.FromTicks(5));
        TimelinePlan plan = b.Plan();

        Assert.Equal(61, plan.FrameCount);
        Assert.Equal(F(60, Fps30), plan.FrameStart(S(100)));
        Assert.Equal(Time.Zero, plan.FrameStart(-S(1)));
        Assert.Equal(F(30, Fps30), plan.FrameStart(F(30, Fps30) + Time.FromTicks(7)));
    }

    [Fact]
    public void Without_a_transform_effect_the_layer_fills_the_frame()
    {
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Rational.Fps30, 3000);
        b.AddVideo(0, "m", Time.Zero, Time.Zero, S(2));
        VideoClipPlan clip = b.Plan().Layers[0].Clips[0];

        Assert.Equal(LayerTransform.Identity, TransformPlan.Evaluate(clip.Transform, S(1)));
    }

    [Fact]
    public void A_disabled_transform_is_ignored_and_the_first_enabled_one_is_used()
    {
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Rational.Fps30, 3000);
        Clip clip = b.AddVideo(0, "m", Time.Zero, Time.Zero, S(2));
        clip.Effects.Add(Transform(enabled: false, (BuiltInEffects.TransformParameters.X, 50)));
        clip.Effects.Add(new EffectInstance { Id = "otro", Type = "paquete.brillo" });
        clip.Effects.Add(Transform(enabled: true, (BuiltInEffects.TransformParameters.Scale, 0.5)));
        clip.Effects.Add(Transform(enabled: true, (BuiltInEffects.TransformParameters.Y, 99)));

        LayerTransform t = TransformPlan.Evaluate(b.Plan().Layers[0].Clips[0].Transform, Time.Zero);

        Assert.Equal(new LayerTransform(0, 0, 0.5, 0, 1), t);
    }

    [Fact]
    public void Transform_keyframes_are_evaluated_at_source_time()
    {
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Rational.Fps30, 3000);
        Clip clip = b.AddVideo(0, "m", S(5), S(1), S(4));
        EffectInstance effect = Transform(enabled: true, (BuiltInEffects.TransformParameters.Rotation, 45), (BuiltInEffects.TransformParameters.Opacity, 3));
        effect.Parameters[BuiltInEffects.TransformParameters.X] = new Parameter
        {
            Keyframes = [new Keyframe(S(1), 0), new Keyframe(S(2), 100)],
        };
        effect.Parameters[BuiltInEffects.TransformParameters.Y] = new Parameter
        {
            Keyframes = [new Keyframe(S(1), -10, Interpolation.Hold), new Keyframe(S(3), 10)],
        };
        clip.Effects.Add(effect);
        VideoClipPlan planned = b.Plan().Layers[0].Clips[0];

        LayerTransform at = TransformPlan.Evaluate(planned.Transform, planned.ToSource(S(5.5)));
        LayerTransform later = TransformPlan.Evaluate(planned.Transform, planned.ToSource(S(7.5)));

        Assert.Equal(50, at.X, 9);
        Assert.Equal(-10, at.Y);
        Assert.Equal(1, at.Scale);
        Assert.Equal(45, at.RotationDegrees);
        Assert.Equal(1, at.Opacity);
        Assert.Equal(100, later.X);
        Assert.Equal(10, later.Y);
    }

    [Fact]
    public void The_plan_is_a_snapshot_that_later_model_edits_do_not_touch()
    {
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Rational.Fps30, 3000);
        Clip clip = b.AddVideo(0, "m", Time.Zero, Time.Zero, S(2));
        clip.Effects.Add(Transform(enabled: true, (BuiltInEffects.TransformParameters.X, 10)));
        TimelinePlan plan = b.Plan();

        clip.Effects[0].Parameters[BuiltInEffects.TransformParameters.X].Value = 999;
        clip.Start = S(50);

        Assert.Equal(10, TransformPlan.Evaluate(plan.Layers[0].Clips[0].Transform, Time.Zero).X);
        Assert.Equal(Time.Zero, plan.Layers[0].Clips[0].Start);
    }

    [Fact]
    public void Audio_plan_skips_muted_tracks_disabled_clips_and_media_without_audio()
    {
        var b = new TimelineBuilder(0, 3);
        b.AddMedia("con", Rational.Fps30, 3000);
        b.AddMedia("mudo", Rational.Fps30, 3000, audio: false);
        b.AddAudio(0, "con", S(1), S(2), S(3), "ok");
        b.AddAudio(0, "mudo", S(5), Time.Zero, S(1), "sin-audio");
        b.AddAudio(1, "con", Time.Zero, Time.Zero, S(1), "pista-muda");
        b.Sequence.AudioTracks[1].Muted = true;
        b.AddAudio(2, "con", Time.Zero, Time.Zero, S(1), "apagado").Enabled = false;

        AudioPlan audio = b.Plan().Audio;

        Assert.Equal(new[] { "a0", "a2" }, audio.Tracks.Select(t => t.TrackId));
        AudioClipPlan clip = Assert.Single(audio.Tracks[0].Clips);
        Assert.Equal(new AudioClipPlan("ok", new SourceKey("con", TimelineBuilder.PathOf("con")), 48_000, 4 * 48_000, 2 * 48_000), clip);
        Assert.Empty(audio.Tracks[1].Clips);
    }

    [Fact]
    public void Relative_media_paths_resolve_against_the_project_directory()
    {
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Rational.Fps30, 3000).Path = @"medios\toma.mp4";
        b.AddVideo(0, "m", Time.Zero, Time.Zero, S(1));

        Assert.Equal(@"C:\proyecto\medios\toma.mp4", b.Plan().Layers[0].Clips[0].Source.Path);
    }

    private static EffectInstance Transform(bool enabled, params (string Name, double Value)[] values) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Type = BuiltInEffects.Transform,
        Enabled = enabled,
        Parameters = values.ToDictionary(v => v.Name, v => new Parameter { Value = v.Value }),
    };
}
