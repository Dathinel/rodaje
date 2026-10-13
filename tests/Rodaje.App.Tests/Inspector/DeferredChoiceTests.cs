using Rodaje.App.Services;
using Rodaje.App.Services.Audio;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Model;

namespace Rodaje.App.Tests.Inspector;

/// <summary>
/// Tarea 102 (revisión de la 99): Máscaras, Dinámica y Texto aplican la elección de un desplegable después de su evento
/// (editar dentro del <c>SelectionChanged</c> rehace el desplegable y cierra la app desde WinUI). Aquí, sin ventana, la
/// comprobación que hace cada panel al despachar: si entre la elección y el despacho cambió la máscara, el compresor, el
/// editor o el clip, la elección no se aplica a otro dueño.
/// </summary>
public class DeferredChoiceTests
{
    private static Clip ClipWithMask(string clipId, string maskId, string? target = null)
    {
        var mask = new EffectInstance { Id = maskId, Type = BuiltInEffects.Mask };
        if (target is not null)
        {
            mask.Options[BuiltInEffects.MaskOptions.Target] = target;
        }

        return new Clip { Id = clipId, Effects = [mask] };
    }

    [Fact]
    public void Mascara_MismaMascaraYClip_SeAplica()
    {
        Clip clip = ClipWithMask("c1", "m1");
        var picked = new DeferredChoice<string>("m1", "c1");
        Assert.True(DeferredChoices.MaskStillApplies(picked, "m1", "c1", clip));
    }

    [Fact]
    public void Mascara_OtraMascaraOtroClipOQuitada_NoSeAplica()
    {
        var picked = new DeferredChoice<string>("m1", "c1");

        // Se eligió otra máscara de la lista antes del despacho.
        Clip both = ClipWithMask("c1", "m1");
        both.Effects.Add(new EffectInstance { Id = "m2", Type = BuiltInEffects.Mask });
        Assert.False(DeferredChoices.MaskStillApplies(picked, "m2", "c1", both));

        // Se seleccionó otro clip (con una máscara del mismo id).
        Assert.False(DeferredChoices.MaskStillApplies(picked, "m1", "c2", ClipWithMask("c2", "m1")));

        // El panel quedó sin clip.
        Assert.False(DeferredChoices.MaskStillApplies(picked, "m1", null, null));

        // Se deshizo la máscara: el clip ya no la tiene.
        Assert.False(DeferredChoices.MaskStillApplies(picked, "m1", "c1", new Clip { Id = "c1" }));

        // Un efecto que no es máscara con el mismo id no cuenta.
        var notMask = new Clip { Id = "c1", Effects = [new EffectInstance { Id = "m1", Type = "blur" }] };
        Assert.False(DeferredChoices.MaskStillApplies(picked, "m1", "c1", notMask));

        // El clip que se mira no es el del panel.
        Assert.False(DeferredChoices.MaskStillApplies(picked, "m1", "c1", ClipWithMask("c9", "m1")));
    }

    [Fact]
    public void Mascara_DestinoQueYaNoEsta_ElegirOtroAcortaLaLista()
    {
        // El caso de la revisión: con «Un efecto que ya no está», elegir otro destino acorta la lista al refrescar. Si el
        // refresco fuera dentro del SelectionChanged, el desplegable se rehace ahí: por eso la edición va después del evento.
        ChoiceList<string> shown = Targets(("La capa (recorta el clip)", ""), ("Zona de los efectos", "zona"), ("Un efecto que ya no está", "fx-borrado"));
        ChoiceList<string> next = Targets(("La capa (recorta el clip)", ""), ("Zona de los efectos", "zona"));
        Assert.True(shown.TryGet(1, out string choice));
        Assert.Equal("zona", choice);
        Assert.Equal(ComboUpdate.Rebuild, ComboChoices.Update(shown, next, isDropDownOpen: false));
        Assert.Equal(ComboUpdate.Keep, ComboChoices.Update(shown, next, isDropDownOpen: true));
    }

    private static ChoiceList<string> Targets(params (string Label, string Value)[] items)
    {
        var list = new ChoiceList<string>();
        foreach ((string label, string value) in items)
        {
            list.Add(label, value);
        }

        return list;
    }

    [Fact]
    public void Dinamica_MismoDuenoYClip_SeAplica()
    {
        var owner = new DynamicsOwner(DynamicsOwnerKind.Clip, "a1");
        var picked = new DeferredChoice<DynamicsOwner>(owner, "a1");
        Assert.True(picked.StillApplies(new DynamicsOwner(DynamicsOwnerKind.Clip, "a1"), "a1"));

        // Una pista sin clip seleccionado.
        var track = new DeferredChoice<DynamicsOwner>(new DynamicsOwner(DynamicsOwnerKind.Track, "t1"), null);
        Assert.True(track.StillApplies(new DynamicsOwner(DynamicsOwnerKind.Track, "t1"), null));
    }

    [Fact]
    public void Dinamica_CambioElDuenoOElClip_NoSeAplica()
    {
        // El preajuste o el sidechain se eligieron para la pista 1; antes del despacho el panel pasó a otro dueño o clip.
        var picked = new DeferredChoice<DynamicsOwner>(new DynamicsOwner(DynamicsOwnerKind.Track, "t1"), "a1");
        Assert.False(picked.StillApplies(new DynamicsOwner(DynamicsOwnerKind.Track, "t2"), "a1"));
        Assert.False(picked.StillApplies(new DynamicsOwner(DynamicsOwnerKind.Bus, "t1"), "a1"));
        Assert.False(picked.StillApplies(new DynamicsOwner(DynamicsOwnerKind.Clip, "a2"), "a2"));
        Assert.False(picked.StillApplies(new DynamicsOwner(DynamicsOwnerKind.Track, "t1"), "a2"));
        Assert.False(picked.StillApplies(new DynamicsOwner(DynamicsOwnerKind.Track, "t1"), null));
        Assert.False(picked.StillApplies(null, "a1"));
    }

    [Fact]
    public void Texto_SoloConElMismoEditorYTitulo() => UiThread.Run(async () =>
    {
        var prober = new FakeProber().Video("a.mp4", 10);
        var session = new EditorSession(new FakePlayback(), prober, new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([Path.GetFullPath("a.mp4")]);
        Assert.True(session.AddTitleAtPlayhead());
        var editor = new TitleEditor(session);
        string titleId = editor.Target()!.Id;
        var picked = new DeferredChoice<TitleEditor>(editor, titleId);

        Assert.True(DeferredChoices.TitleStillApplies(picked, editor, titleId));

        // El panel cambió de editor (otra sesión adjunta) o de título.
        Assert.False(DeferredChoices.TitleStillApplies(picked, new TitleEditor(session), titleId));
        Assert.False(DeferredChoices.TitleStillApplies(picked, null, titleId));
        Assert.False(DeferredChoices.TitleStillApplies(picked, editor, "otro"));

        // Sin título al elegir no hay a quién aplicar.
        Assert.False(DeferredChoices.TitleStillApplies(new DeferredChoice<TitleEditor>(editor, null), editor, null));

        // La selección cambió y el panel aún no se refrescó: el editor ya no apunta a ese título aunque el panel lo crea.
        session.Select([]);
        Assert.Null(editor.Target());
        Assert.False(DeferredChoices.TitleStillApplies(picked, editor, titleId));
    });
}
