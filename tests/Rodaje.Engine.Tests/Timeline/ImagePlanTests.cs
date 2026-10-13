using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Timeline;
using Rodaje.Playback.Composition;
using P = Rodaje.Core.Model.BuiltInEffects.CropParameters;

namespace Rodaje.Engine.Tests.Timeline;

public sealed class ImagePlanTests
{
    [Fact]
    public void Crop_travels_in_the_transform_with_its_keyframes_and_reveals_below()
    {
        var crop = new EffectInstance { Id = "c", Type = BuiltInEffects.Crop };
        crop.Parameters[P.Left] = new Parameter { Keyframes = [new(Time.Zero, 0), new(Time.FromSeconds(2), 0.4)] };
        crop.Parameters[P.Right] = new Parameter { Value = 0.1 };
        EffectPlan plan = EffectPlan.From([crop])!;

        LayerTransform t = EffectPlan.Transform(plan, Time.FromSeconds(1));

        Assert.Equal(0.2, t.Crop!.Value.Left, 9);
        Assert.Equal(0.1, t.Crop!.Value.Right, 9);
        Assert.Equal(1, t.Scale);
        Assert.True(plan.MayRevealBelow);
        Assert.Equal(0, plan.EffectCount);
    }

    [Fact]
    public void Corner_pin_travels_in_the_transform_and_a_track_is_not_drawn()
    {
        var pin = new EffectInstance { Id = "p", Type = BuiltInEffects.CornerPin };
        pin.Parameters[BuiltInEffects.CornerPinParameters.TopLeftX] = new Parameter { Keyframes = [new(Time.Zero, 0), new(Time.FromSeconds(2), 40)] };
        pin.Parameters[BuiltInEffects.CornerPinParameters.BottomRightY] = new Parameter { Value = -12 };
        var track = new EffectInstance { Id = "t", Type = BuiltInEffects.Track };
        EffectPlan plan = EffectPlan.From([track, pin])!;

        LayerTransform t = EffectPlan.Transform(plan, Time.FromSeconds(1));

        Assert.Equal(20, t.Pin!.Value.TopLeftX, 9);
        Assert.Equal(-12, t.Pin!.Value.BottomRightY, 9);
        Assert.Equal(1, t.Scale);
        Assert.True(plan.MayRevealBelow);
        Assert.Equal(0, plan.EffectCount);
        Assert.Null(EffectPlan.From([track]));
    }

    [Fact]
    public void Curves_travel_as_a_resource_key_without_asking_for_a_lut()
    {
        var curves = new EffectInstance { Id = "k", Type = BuiltInEffects.Curves };
        curves.Options[BuiltInEffects.CurvesOptions.Red] = "0,0.1;1,0.9";
        var key = new EffectInstance { Id = "g", Type = BuiltInEffects.ChromaKey };
        EffectPlan plan = EffectPlan.From([curves, key])!;
        var effects = new LayerEffect[2];

        plan.Evaluate(Time.Zero, effects, null);

        Assert.Equal(14, effects[0].Code);
        Assert.Equal("|0,0.1;1,0.9|||||", effects[0].Resource?.Reference);
        Assert.Equal(12, effects[1].Code);
        Assert.Equal(0.7f, effects[1].Values[1]);
        Assert.True(plan.MayRevealBelow);
    }

    [Fact]
    public void Grain_gets_the_source_time_and_choices_follow_the_parameters()
    {
        var grain = new EffectInstance { Id = "g", Type = BuiltInEffects.Grain };
        grain.Options[BuiltInEffects.RegionOptions.Region] = "outside";
        var blur = new EffectInstance { Id = "b", Type = BuiltInEffects.Blur };
        blur.Options[BuiltInEffects.BlurOptions.Kind] = "radial";
        var mask = new EffectInstance { Id = "m", Type = BuiltInEffects.Mask };
        mask.Options[BuiltInEffects.MaskOptions.Use] = "effects";
        EffectPlan plan = EffectPlan.From([grain, blur, mask])!;
        var effects = new LayerEffect[3];

        plan.Evaluate(Time.FromSeconds(2.5), effects, null);

        EffectDefinition definition = EffectRegistry.Find(BuiltInEffects.Grain)!;
        Assert.Equal(5, definition.TimeSlot);
        Assert.Equal(2, effects[0].Values[4]);
        Assert.Equal(2.5f, effects[0].Values[definition.TimeSlot]);
        Assert.Equal(3, effects[1].Values[4]);
        Assert.Equal(0, effects[1].Values[5]);
        Assert.Equal(1, effects[2].Values[MaskGeometry.UseSlot]);
    }

    [Fact]
    public void Hue_curves_alone_send_a_resource()
    {
        var curves = new EffectInstance { Id = "k", Type = BuiltInEffects.Curves };
        curves.Options[BuiltInEffects.CurvesOptions.HueSaturation] = "0.3,0.2";
        EffectPlan plan = EffectPlan.From([curves])!;
        var effects = new LayerEffect[1];
        plan.Evaluate(Time.Zero, effects, null);
        Assert.Equal("|||||0.3,0.2|", effects[0].Resource?.Reference);
    }

    [Fact]
    public void Identity_curves_send_no_resource()
    {
        EffectPlan plan = EffectPlan.From([new EffectInstance { Id = "k", Type = BuiltInEffects.Curves }])!;
        var effects = new LayerEffect[1];
        plan.Evaluate(Time.Zero, effects, null);
        Assert.Null(effects[0].Resource);
        Assert.False(plan.MayRevealBelow);
    }
}
