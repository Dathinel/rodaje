using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using Rodaje.Engine.Timeline;
using Rodaje.Playback.Composition;
using T = Rodaje.Core.Model.BuiltInEffects.TransformParameters;

namespace Rodaje.Engine.Tests.Timeline;

public sealed class TransformKeyframeHoldTests
{
    [Fact]
    public void La_escala_mantiene_el_ultimo_keyframe_hasta_el_final()
    {
        var effect = new EffectInstance { Id = "e1", Type = BuiltInEffects.Transform };
        effect.Parameters[T.Scale] = new Parameter
        {
            Value = 1,
            Keyframes = [new Keyframe(Time.Zero, 1), new Keyframe(Time.FromSeconds(3.056), 0.5)],
        };
        EffectPlan? plan = EffectPlan.From([effect]);
        Assert.NotNull(plan);
        Assert.Equal(1, EffectPlan.Transform(plan, Time.Zero).Scale);
        Assert.Equal(1 - (0.5 * 2 / 3.056), EffectPlan.Transform(plan, Time.FromSeconds(2)).Scale, 3);
        foreach (double seconds in new[] { 3.056, 4.278, 5.5 })
        {
            LayerTransform transform = EffectPlan.Transform(plan, Time.FromSeconds(seconds));
            Assert.Equal(0.5, transform.Scale, 6);
            Assert.Equal(1, transform.Opacity);
        }
    }
}
