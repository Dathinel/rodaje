using Rodaje.App.Services.Audio;
using Rodaje.App.Services.Inspector;
using Rodaje.Core.Model;

namespace Rodaje.App.Tests.Inspector;

/// <summary>
/// Tarea 112: lo que deciden Máscaras y Dinámica al despachar la elección de un desplegable, sacado de la vista a funciones
/// puras (<see cref="DeferredChoices.MaskTarget"/> y <see cref="DeferredChoices.DynamicsStillApplies"/>) para probar la
/// condición completa y no solo el <c>record struct</c>.
/// </summary>
public class DeferredDecisionTests
{
    private static Clip ClipWithMask(string clipId, string maskId)
    {
        var mask = new EffectInstance { Id = maskId, Type = BuiltInEffects.Mask };
        var blur = new EffectInstance { Id = "fx1", Type = "rodaje.blur" };
        return new Clip { Id = clipId, Effects = [blur, mask] };
    }

    [Fact]
    public void Mascara_UnEfecto_SeGuardaComoDestinoSinUso()
    {
        var picked = new DeferredChoice<string>("m1", "c1");
        Assert.Equal(new MaskTargetEdit("fx1", ""), DeferredChoices.MaskTarget(picked, "fx1", "m1", "c1", ClipWithMask("c1", "m1")));
    }

    [Fact]
    public void Mascara_LaCapa_QuitaDestinoYUso()
    {
        var picked = new DeferredChoice<string>("m1", "c1");
        Assert.Equal(new MaskTargetEdit("", ""), DeferredChoices.MaskTarget(picked, "", "m1", "c1", ClipWithMask("c1", "m1")));
    }

    [Fact]
    public void Mascara_LaZonaDeLosEfectos_EsElUsoEffectsSinDestino()
    {
        var picked = new DeferredChoice<string>("m1", "c1");
        Assert.Equal(new MaskTargetEdit("", "effects"),
            DeferredChoices.MaskTarget(picked, MaskTargetChoices.EffectsZone, "m1", "c1", ClipWithMask("c1", "m1")));
    }

    [Fact]
    public void Mascara_YaNoVale_NoSeAplicaYSeRefresca()
    {
        var picked = new DeferredChoice<string>("m1", "c1");

        // Otra máscara u otro clip en el panel, sin clip, o la máscara quitada del clip: nulo (la vista solo refresca).
        Assert.Null(DeferredChoices.MaskTarget(picked, "fx1", "m2", "c1", ClipWithMask("c1", "m2")));
        Assert.Null(DeferredChoices.MaskTarget(picked, "fx1", "m1", "c2", ClipWithMask("c2", "m1")));
        Assert.Null(DeferredChoices.MaskTarget(picked, "fx1", "m1", "c1", null));
        Assert.Null(DeferredChoices.MaskTarget(picked, "fx1", "m1", "c1", new Clip { Id = "c1" }));
        Assert.Null(DeferredChoices.MaskTarget(picked, MaskTargetChoices.EffectsZone, "m1", "c1", ClipWithMask("c9", "m1")));
    }

    [Fact]
    public void Dinamica_MismoDuenoClipYEditor_SeAplica()
    {
        var owner = new DynamicsOwner(DynamicsOwnerKind.Track, "t1");
        var picked = new DeferredChoice<DynamicsOwner>(owner, "a1");
        Assert.True(DeferredChoices.DynamicsStillApplies(picked, new DynamicsOwner(DynamicsOwnerKind.Track, "t1"), "a1",
            new DynamicsOwner(DynamicsOwnerKind.Track, "t1")));
    }

    [Fact]
    public void Dinamica_ElEditorApuntaAOtroDuenoONoHay_NoSeAplica()
    {
        // El panel sigue con el mismo dueño y clip, pero el editor del compresor ya apunta a otro (o no hay): la condición que
        // la vista comprobaba aparte y que no estaba en DeferredChoices.
        var owner = new DynamicsOwner(DynamicsOwnerKind.Track, "t1");
        var picked = new DeferredChoice<DynamicsOwner>(owner, "a1");
        Assert.False(DeferredChoices.DynamicsStillApplies(picked, owner, "a1", new DynamicsOwner(DynamicsOwnerKind.Track, "t2")));
        Assert.False(DeferredChoices.DynamicsStillApplies(picked, owner, "a1", new DynamicsOwner(DynamicsOwnerKind.Bus, "t1")));
        Assert.False(DeferredChoices.DynamicsStillApplies(picked, owner, "a1", null));
    }

    [Fact]
    public void Dinamica_CambioElDuenoOElClip_NoSeAplicaAunqueElEditorCoincida()
    {
        var owner = new DynamicsOwner(DynamicsOwnerKind.Track, "t1");
        var picked = new DeferredChoice<DynamicsOwner>(owner, "a1");
        Assert.False(DeferredChoices.DynamicsStillApplies(picked, new DynamicsOwner(DynamicsOwnerKind.Track, "t2"), "a1", owner));
        Assert.False(DeferredChoices.DynamicsStillApplies(picked, owner, "a2", owner));
        Assert.False(DeferredChoices.DynamicsStillApplies(picked, null, "a1", owner));
    }
}
