using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Rodaje.Playback.Composition;
using static Rodaje.Engine.Tests.Support.Seconds;
using St = Rodaje.Core.Model.BuiltInEffects;

namespace Rodaje.Engine.Tests.Timeline;

public sealed class TimelineStabilizeTests
{
    private static readonly Rational Fps30 = Rational.Fps30;

    /// <summary>Una corrección por cuadro que dice de qué cuadro es: X = índice / 1000.</summary>
    private static StabilizationTrack Track(long first, int count) =>
        new(first, Enumerable.Range(0, count).Select(i => new LayerStabilization(1.1, 0, (first + i) / 1000.0, 0)));

    private static (TimelineSession Session, FakeEffectContent Content) Open(Action<EffectInstance>? configure = null, bool addTrack = true,
        params EffectInstance[] extra)
    {
        var backend = new FakeTimelineBackend();
        var content = new FakeEffectContent();
        if (addTrack)
        {
            content.Stabilizations["clave"] = Track(30, 200);
        }

        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Fps30, 3000, backend: backend);
        Clip clip = b.AddVideo(0, "m", S(10), S(2), S(4));
        EffectInstance stabilize = TimelineEffectsTests.Effect(St.Stabilize, (St.StabilizeParameters.Intensity, 0.7));
        stabilize.Options[St.StabilizeOptions.Analysis] = "clave";
        configure?.Invoke(stabilize);
        clip.Effects.AddRange(extra);
        clip.Effects.Add(stabilize);
        return (TimelineSessionTests.Open(b, backend, new TimelineSessionOptions { Content = content }), content);
    }

    [Fact]
    public void The_layer_carries_the_correction_of_the_frame_it_shows()
    {
        (TimelineSession session, FakeEffectContent content) = Open();
        using (session)
        {
            session.Seek(S(11));
            using CompositionFrame frame = session.Tick();

            LayerFrame layer = Assert.Single(frame.Layers);
            // 11 s de la secuencia = 3 s del origen = cuadro 90.
            Assert.Equal(90, layer.Frame.Index);
            Assert.Equal(new LayerStabilization(1.1, 0, 0.090, 0), layer.Transform.Stabilization);
            Assert.True(layer.IsPlain);
            Assert.Equal(("clave", 0.7, true), Assert.Single(content.StabilizationRequests.Distinct()));
        }
    }

    [Fact]
    public void Crop_off_and_the_stack_order_reach_the_content()
    {
        (TimelineSession session, FakeEffectContent content) = Open(
            e => e.Options[St.StabilizeOptions.Crop] = "off",
            extra: [TimelineEffectsTests.Effect(St.Transform, (St.TransformParameters.Scale, 0.5))]);
        using (session)
        {
            session.Seek(S(10));
            using CompositionFrame frame = session.Tick();

            LayerFrame layer = Assert.Single(frame.Layers);
            Assert.Equal(0.5, layer.Transform.Scale);
            Assert.Equal(0.060, layer.Transform.Stabilization!.Value.X, 9);
            Assert.Contains(("clave", 0.7, false), content.StabilizationRequests);
        }
    }

    [Theory]
    [InlineData(false, false, 0.7)]
    [InlineData(true, true, 0.7)]
    [InlineData(true, false, 0.0)]
    public void Without_analysis_disabled_or_at_zero_intensity_nothing_is_corrected(bool hasTrack, bool disabled, double intensity)
    {
        (TimelineSession session, _) = Open(e =>
        {
            e.Enabled = !disabled;
            e.Parameters[St.StabilizeParameters.Intensity] = new Parameter { Value = intensity };
        }, hasTrack);
        using (session)
        {
            session.Seek(S(11));
            using CompositionFrame frame = session.Tick();

            Assert.Null(Assert.Single(frame.Layers).Transform.Stabilization);
        }
    }

    [Fact]
    public void Frames_outside_the_analysis_take_the_nearest_edge()
    {
        StabilizationTrack track = Track(30, 10);
        Assert.Equal(0.030, track.At(0).X, 9);
        Assert.Equal(0.039, track.At(1000).X, 9);
        Assert.Equal(0.035, track.At(35).X, 9);
    }
}
