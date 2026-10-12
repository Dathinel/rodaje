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
        Assert.Equal(2, e.Values[MaskGeometry.ShapeSlot]);
        Assert.Equal(1, e.Values[MaskGeometry.InvertSlot]);
        Assert.Equal((int)MaskMode.Intersect, e.Values[MaskGeometry.ModeSlot]);
        Assert.Equal(0, e.Values[MaskGeometry.TargetSlot]);
        Assert.Equal("0,-0.5;0.5,0.5;-0.5,0.5", e.Resource?.Reference);
        Assert.True(plan.MayRevealBelow);
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
        Assert.Equal(3, effects[0].Values[MaskGeometry.ShapeSlot]);
        Assert.Equal("file:p.medios/fondo.mkv", effects[0].Resource?.Reference);
    }

    [Fact]
    public void A_bezier_with_shape_keys_sends_the_keys_and_the_source_seconds()
    {
        var mask = new EffectInstance { Id = "m", Type = BuiltInEffects.Mask };
        mask.Options[O.Shape] = "bezier";
        mask.Options[O.Mode] = "subtract";
        mask.Options[O.Points] = "0,0;1,0;1,1";
        mask.Options[O.ShapeKeys] = MaskShapeTrack.Format([(Time.FromSeconds(1), MaskPath.Rectangle()), (Time.FromSeconds(2), MaskPath.Ellipse())]);
        EffectPlan plan = EffectPlan.From([mask])!;
        var effects = new LayerEffect[1];

        plan.Evaluate(Time.FromSeconds(3.25), effects, null);

        Assert.Equal(mask.Options[O.ShapeKeys], effects[0].Resource?.Reference);
        Assert.Equal(3.25f, effects[0].Values[MaskGeometry.MatteTimeSlot], 4);
        Assert.Equal((int)MaskMode.Subtract, effects[0].Values[MaskGeometry.ModeSlot]);
    }

    [Fact]
    public void A_mask_with_a_target_points_at_its_effect_and_does_not_reveal_below()
    {
        var blur = new EffectInstance { Id = "b", Type = BuiltInEffects.Blur };
        var hidden = new EffectInstance { Id = "c", Type = BuiltInEffects.Color, Enabled = false };
        var zone = new EffectInstance { Id = "m1", Type = BuiltInEffects.Mask };
        zone.Options[O.Target] = "b";
        var dangling = new EffectInstance { Id = "m2", Type = BuiltInEffects.Mask };
        dangling.Options[O.Target] = "c";
        var onMask = new EffectInstance { Id = "m3", Type = BuiltInEffects.Mask };
        onMask.Options[O.Target] = "m1";
        EffectPlan plan = EffectPlan.From([zone, hidden, blur, dangling, onMask])!;
        var effects = new LayerEffect[plan.EffectCount];

        plan.Evaluate(Time.Zero, effects, null);

        // La lista de la capa: m1, b, m2, m3 (el color apagado no entra). La zona apunta a b (índice 1, guardado como 2).
        Assert.Equal(4, effects.Length);
        Assert.Equal(2, effects[0].Values[MaskGeometry.TargetSlot]);
        Assert.Equal(MaskGeometry.NoTarget, effects[2].Values[MaskGeometry.TargetSlot]);
        Assert.Equal(MaskGeometry.NoTarget, effects[3].Values[MaskGeometry.TargetSlot]);
        Assert.False(plan.MayRevealBelow);
    }
}
