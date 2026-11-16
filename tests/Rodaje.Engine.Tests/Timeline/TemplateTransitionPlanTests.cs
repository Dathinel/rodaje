using Rodaje.Core.Effects;
using Rodaje.Core.Model;
using Rodaje.Engine.Tests.Support;
using Rodaje.Engine.Timeline;
using Time = Rodaje.Core.Timing.Time;

namespace Rodaje.Engine.Tests.Timeline;

/// <summary>Transición de plantilla de movimiento (D-064): el plan pide las curvas con los valores de la opción.</summary>
public sealed class TemplateTransitionPlanTests
{
    private const string Reference = "rodaje-plantillas@1.0.0/transicion-barrido";

    private static TransitionInstance Preset(string? values)
    {
        var t = new TransitionInstance { Id = "t", Type = BuiltInTransitions.Preset, From = "a", To = "b", Duration = Time.FromSeconds(1) };
        t.Options[BuiltInTransitions.PresetOptions.Preset] = Reference;
        if (values is not null)
        {
            t.Options[BuiltInEffects.CommonOptions.TemplateValues] = values;
        }

        return t;
    }

    private static TransitionCurves Wipe(double angle) =>
        new(EffectRegistry.Find(BuiltInTransitions.Wipe)!.Code, 1920, 1080, [], [])
        {
            MixParameters = new Dictionary<string, double> { [BuiltInTransitions.WipeParameters.Angle] = angle },
        };

    private static float Angle(TransitionPlan plan) =>
        plan.Values[EffectRegistry.Find(BuiltInTransitions.Wipe)!.FindParameter(BuiltInTransitions.WipeParameters.Angle)!.Slot];

    [Fact]
    public void El_angulo_expuesto_llega_al_plan_del_barrido()
    {
        var content = new FakeEffectContent();
        content.Transitions[Reference] = Wipe(0);
        content.Transitions[Reference + "|{\"angulo\":90}"] = Wipe(90);

        TransitionPlan plain = TransitionPlan.From(Preset(null), Time.Zero, Time.FromSeconds(1), 1920, 1080, new(30, 1), content);
        TransitionPlan turned = TransitionPlan.From(Preset("{\"angulo\":90}"), Time.Zero, Time.FromSeconds(1), 1920, 1080, new(30, 1), content);

        Assert.Equal(0, Angle(plain));
        Assert.Equal(90, Angle(turned));
        Assert.False(turned.FellBack);
        Assert.Equal([null, "{\"angulo\":90}"], content.TemplateValuesAsked);
    }

    [Fact]
    public void Sin_el_paquete_se_ve_como_fundido_cruzado()
    {
        TransitionPlan plan = TransitionPlan.From(Preset("{\"angulo\":90}"), Time.Zero, Time.FromSeconds(1), 1920, 1080, new(30, 1), new FakeEffectContent());

        Assert.True(plan.FellBack);
        Assert.Equal(EffectRegistry.Find(BuiltInTransitions.Crossfade)!.Code, plan.Code);
    }
}
