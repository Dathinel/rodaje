using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Offline;
using Rodaje.Engine.Tests.Offline;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Rodaje.Playback.Composition;

namespace Rodaje.Engine.Tests.Timeline;

public sealed class StillImageTests
{
    [Fact]
    public void Preview_and_offline_keep_frame_zero_while_transform_color_and_mask_keyframes_advance()
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(1, 0);
        MediaRef media = b.AddMedia("foto", Rational.Fps30, 1, audio: false, backend: backend);
        media.IsStillImage = true;
        media.Duration = Time.Zero;
        media.FrameRate = null;
        Clip clip = b.AddVideo(0, media.Id, Time.Zero, Time.FromSeconds(2), Time.FromSeconds(2));
        foreach ((string type, string parameter, double from, double to) in new[]
        {
            (BuiltInEffects.Transform, BuiltInEffects.TransformParameters.X, 0.0, 100.0),
            (BuiltInEffects.Color, BuiltInEffects.ColorParameters.Exposure, 0.0, 2.0),
            (BuiltInEffects.Mask, BuiltInEffects.MaskParameters.Opacity, 1.0, 0.0),
        })
        {
            clip.Effects.Add(new EffectInstance
            {
                Type = type,
                Parameters = new() { [parameter] = new Parameter { Keyframes = [new(Time.FromSeconds(2), from), new(Time.FromSeconds(4), to)] } },
            });
        }

        using TimelineSession preview = TimelineSessionTests.Open(b, backend);
        using OfflineTimeline offline = OfflineProjects.Open(b, backend);
        Assert.Equal(60, offline.FrameCount);
        for (int f = 0; f < 60; f++)
        {
            preview.SeekToFrame(f);
            using CompositionFrame expected = preview.Tick();
            using CompositionFrame actual = offline.Render(f, CancellationToken.None);
            LayerFrame layer = Assert.Single(actual.Layers);
            LayerFrame shown = Assert.Single(expected.Layers);
            Assert.Equal(0, layer.Frame.Index);
            Assert.Equal(0, shown.Frame.Index);
            Assert.Equal(100.0 * f / 60, layer.Transform.X, 5);
            Assert.Equal(layer.Transform, shown.Transform);
            Assert.Equal((float)(2.0 * f / 60), layer.Effects[0].Values[0], 5);
            Assert.Equal(10, layer.Effects[1].Code);
            Assert.Equal((float)(1.0 - f / 60.0), layer.Effects[1].Values[6], 5);
            Assert.Equal(layer.Effects[1].Values[6], shown.Effects[1].Values[6]);
        }

        Assert.All(backend.Videos.SelectMany(v => v.Requests), index => Assert.Equal(0, index));
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Speed_reverse_and_ramps_request_only_frame_zero_in_preview_and_offline(int mode)
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(1, 0);
        MediaRef media = b.AddMedia("foto", Rational.Fps30, 1, audio: false, backend: backend);
        media.IsStillImage = true;
        media.Duration = Time.Zero;
        media.FrameRate = null;
        Clip clip = b.AddVideo(0, media.Id, Time.Zero, Time.Zero, Time.FromSeconds(4));
        var speed = new ClipSpeed { Rate = new Rational(2, 1), Reverse = mode == 1 };
        if (mode == 2)
        {
            speed.Rate = new Rational(1, 1);
            speed.Curve = new SpeedCurve { Span = Time.FromSeconds(2), Points = [new(0, 100), new(10000, 300)] };
        }

        clip.Speed = speed;
        ClipTimeMap.Fit(Time.Zero, Time.Zero, Time.FromSeconds(4), speed).ApplyTo(clip);
        using TimelineSession preview = TimelineSessionTests.Open(b, backend);
        using OfflineTimeline offline = OfflineProjects.Open(b, backend);
        for (int f = 0; f < offline.FrameCount; f++)
        {
            preview.SeekToFrame(f);
            using CompositionFrame shown = preview.Tick();
            using CompositionFrame rendered = offline.Render(f, CancellationToken.None);
            Assert.Equal(0, Assert.Single(shown.Layers).Frame.Index);
            Assert.Equal(0, Assert.Single(rendered.Layers).Frame.Index);
        }

        Assert.All(backend.Videos.SelectMany(v => v.Requests), index => Assert.Equal(0, index));
    }
}
