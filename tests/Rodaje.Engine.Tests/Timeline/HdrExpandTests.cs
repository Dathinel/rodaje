using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Rodaje.Playback.Composition;
using static Rodaje.Engine.Tests.Support.Seconds;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.Engine.Tests.Timeline;

/// <summary>«Expandir a HDR» (fase C, parte 3): la opción enciende el pico y el pico sale de «Brillo de referencia HDR», acotado a 203–1000.</summary>
public sealed class HdrExpandTests
{
    [Theory]
    [InlineData("on", 600, 600)]
    [InlineData("on", 0, 1000)]
    [InlineData("on", 5000, 1000)]
    [InlineData("on", 100, 203)]
    [InlineData("off", 600, 0)]
    [InlineData(null, 600, 0)]
    public void The_option_sets_the_peak_from_the_reference_nits(string? option, double reference, double expected)
    {
        var backend = new FakeTimelineBackend();
        var b = new TimelineBuilder(1, 0);
        b.AddMedia("m", Rational.Fps30, 3000, backend: backend);
        Clip clip = b.AddVideo(0, "m", Time.Zero, Time.Zero, S(4));
        EffectInstance transform = TimelineEffectsTests.Effect(BuiltInEffects.Transform, (T.HdrReferenceNits, reference));
        if (option is not null)
        {
            transform.Options[BuiltInEffects.TransformOptions.HdrExpand] = option;
        }

        clip.Effects.Add(transform);
        using TimelineSession session = TimelineSessionTests.Open(b, backend);

        session.Seek(S(1));
        using CompositionFrame frame = session.Tick();
        LayerFrame layer = Assert.Single(frame.Layers);
        Assert.Equal(reference, layer.Transform.HdrReferenceNits);
        Assert.Equal(expected, layer.Transform.HdrExpandNits);
    }
}
