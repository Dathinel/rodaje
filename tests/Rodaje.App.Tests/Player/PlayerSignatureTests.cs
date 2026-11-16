using Rodaje.App.Views.Player;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Playback;
using Rodaje.Playback.Composition;

namespace Rodaje.App.Tests.Player;

public sealed class PlayerSignatureTests
{
    [Fact]
    public void Signature_changes_when_Next_arrives_or_changes_in_a_layer_group_or_transition()
    {
        using var a = new DecodedFrame(10, Time.Zero, Time.FromTicks(1), null, 0);
        using var b = new DecodedFrame(11, Time.Zero, Time.FromTicks(1), null, 0);
        using var c = new DecodedFrame(20, Time.Zero, Time.FromTicks(1), null, 0);
        using var d = new DecodedFrame(21, Time.Zero, Time.FromTicks(1), null, 0);

        var withoutNext = new CompositionFrame(Time.Zero, 1920, 1080,
        [
            new LayerFrame(0, "c", a, LayerTransform.Identity),
        ]);
        var withNext = new CompositionFrame(Time.Zero, 1920, 1080,
        [
            new LayerFrame(0, "c", a, LayerTransform.Identity)
            {
                Next = b,
                Fraction = 0.25f,
                Sampling = FrameSampling.Blend,
            },
        ]);
        var otherFraction = new CompositionFrame(Time.Zero, 1920, 1080,
        [
            new LayerFrame(0, "c", a, LayerTransform.Identity)
            {
                Next = b,
                Fraction = 0.5f,
                Sampling = FrameSampling.Blend,
            },
        ]);
        var otherSampling = new CompositionFrame(Time.Zero, 1920, 1080,
        [
            new LayerFrame(0, "c", a, LayerTransform.Identity)
            {
                Next = b,
                Fraction = 0.25f,
                Sampling = FrameSampling.OpticalFlow,
            },
        ]);

        Assert.NotEqual(PlayerEngine.Signature(withoutNext), PlayerEngine.Signature(withNext));
        Assert.NotEqual(PlayerEngine.Signature(withNext), PlayerEngine.Signature(otherFraction));
        Assert.NotEqual(PlayerEngine.Signature(withNext), PlayerEngine.Signature(otherSampling));

        var nestedWithoutNext = new CompositionFrame(Time.Zero, 1920, 1080,
        [
            new LayerFrame(0, "g", [new LayerFrame(0, "c", a, LayerTransform.Identity)], LayerTransform.Identity),
        ]);
        var nestedWithNext = new CompositionFrame(Time.Zero, 1920, 1080,
        [
            new LayerFrame(0, "g",
            [
                new LayerFrame(0, "c", a, LayerTransform.Identity)
                {
                    Next = b,
                    Fraction = 0.25f,
                    Sampling = FrameSampling.Blend,
                },
            ], LayerTransform.Identity),
        ]);
        Assert.NotEqual(PlayerEngine.Signature(nestedWithoutNext), PlayerEngine.Signature(nestedWithNext));

        var transitionWithoutNext = new CompositionFrame(Time.Zero, 1920, 1080,
        [
            new LayerFrame(0, "c", a, LayerTransform.Identity)
            {
                Transition = new LayerTransition(0, 0.5f, default, new LayerFrame(0, "in", c, LayerTransform.Identity)),
            },
        ]);
        var transitionWithNext = new CompositionFrame(Time.Zero, 1920, 1080,
        [
            new LayerFrame(0, "c", a, LayerTransform.Identity)
            {
                Transition = new LayerTransition(0, 0.5f, default, new LayerFrame(0, "in", c, LayerTransform.Identity)
                {
                    Next = d,
                    Fraction = 0.5f,
                    Sampling = FrameSampling.Blend,
                }),
            },
        ]);
        Assert.NotEqual(PlayerEngine.Signature(transitionWithoutNext), PlayerEngine.Signature(transitionWithNext));
    }
}
