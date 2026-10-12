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
    public void Curves_travel_as_a_resource_key_without_asking_for_a_lut()
    {
        var curves = new EffectInstance { Id = "k", Type = BuiltInEffects.Curves };
        curves.Options[BuiltInEffects.CurvesOptions.Red] = "0,0.1;1,0.9";
        var key = new EffectInstance { Id = "g", Type = BuiltInEffects.ChromaKey };
        EffectPlan plan = EffectPlan.From([curves, key])!;
        var effects = new LayerEffect[2];

        plan.Evaluate(Time.Zero, effects, null);

        Assert.Equal(14, effects[0].Code);
        Assert.Equal("|0,0.1;1,0.9||", effects[0].Resource?.Reference);
        Assert.Equal(12, effects[1].Code);
        Assert.Equal(0.7f, effects[1].Values[1]);
        Assert.True(plan.MayRevealBelow);
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
