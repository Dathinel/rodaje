using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Timeline;
using Rodaje.Playback.Composition;
using M = Rodaje.Core.Model.BuiltInEffects.MaskParameters;
using O = Rodaje.Core.Model.BuiltInEffects.MaskOptions;

namespace Rodaje.Engine.Tests.Timeline;

public sealed class MaskPlanTests
{
    [Fact]
    public void A_mask_travels_with_its_keyframes_choices_and_path()
    {
        var mask = new EffectInstance { Id = "m", Type = BuiltInEffects.Mask };
        mask.Options[O.Shape] = "path";
        mask.Options[O.Invert] = "on";
        mask.Options[O.Path] = "0,-0.5;0.5,0.5;-0.5,0.5";
        mask.Parameters[M.X] = new Parameter { Keyframes = [new(Time.Zero, 0.2), new(Time.FromSeconds(2), 0.6)] };
        EffectPlan plan = EffectPlan.From([mask])!;
        var effects = new LayerEffect[1];

        plan.Evaluate(Time.FromSeconds(1), effects, null);

        LayerEffect e = effects[0];
        Assert.Equal(10, e.Code);
        Assert.Equal(0.4f, e.Values[0], 5);
        Assert.Equal(0.5f, e.Values[1]);
        Assert.Equal(2, e.Values[7]);
        Assert.Equal(1, e.Values[8]);
        Assert.Equal("0,-0.5;0.5,0.5;-0.5,0.5", e.Resource?.Reference);
    }

    [Fact]
    public void An_image_mask_carries_the_seconds_into_its_gray_video()
    {
        var mask = new EffectInstance { Id = "m", Type = BuiltInEffects.Mask };
        mask.Options[O.Shape] = "image";
        mask.Options[O.Matte] = "file:p.medios/fondo.mkv";
        mask.Options[O.MatteStart] = "1.5";
        EffectPlan plan = EffectPlan.From([mask])!;
        var effects = new LayerEffect[1];

        plan.Evaluate(Time.FromSeconds(4), effects, null);

        Assert.Equal(2.5f, effects[0].Values[MaskGeometry.MatteTimeSlot], 4);
        Assert.Equal(3, effects[0].Values[7]);
        Assert.Equal("file:p.medios/fondo.mkv", effects[0].Resource?.Reference);
    }
}
