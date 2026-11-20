using Rodaje.App.Services;
using Rodaje.App.Services.Inspector;
using Rodaje.App.Tests.Support;
using Rodaje.Core.Commands;
using Rodaje.Core.Model;

namespace Rodaje.App.Tests.Inspector;

public sealed class InspectorTargetsTests
{
    [Fact]
    public void LaSeleccionSeResuelve_EnSuOrden_SinLosQueYaNoEstan_YConElFiltroDeCadaPanel() => UiThread.Run(async () =>
    {
        var session = new EditorSession(new FakePlayback(), new FakeProber().Video("a.mp4", 10), new FakeDialogs(), new SequentialIdGenerator());
        await session.OpenFilesAsync([Path.GetFullPath("a.mp4")]);
        Clip video = session.MainTrack!.Clips[0];
        Clip audio = session.Sequence.AudioTracks[0].Clips[0];
        session.Select([audio.Id, "ya-no-esta", video.Id]);

        Assert.Equal([audio.Id, video.Id], InspectorTargets.Selected(session).Select(t => t.Clip.Id));
        Assert.Equal(audio.Id, InspectorTargets.First(session)?.Clip.Id);
        Assert.Equal(video.Id, InspectorTargets.First(session, t => t.Track.Kind == TrackKind.Video)?.Clip.Id);
        Assert.Null(InspectorTargets.First(session, t => t.Clip.Generator is not null));
        Assert.Empty(InspectorTargets.Selected(session, _ => false));
    });

    [Fact]
    public void ElEquivalente_EsElMismoIdAntesQueOtroDelMismoTipo()
    {
        var first = new EffectInstance { Id = "a", Type = "blur" };
        var second = new EffectInstance { Id = "b", Type = "blur" };
        var clip = new Clip { Effects = [new EffectInstance { Id = "c", Type = "glow" }, first, second] };
        var other = new Clip { Effects = [new EffectInstance { Id = "d", Type = "blur" }] };

        Assert.Same(second, InspectorTargets.Equivalent(clip, "b", "blur"));
        Assert.Same(first, InspectorTargets.Equivalent(clip, null, "blur"));
        Assert.Equal("d", InspectorTargets.Equivalent(other, "b", "blur")?.Id);
        Assert.Null(InspectorTargets.Equivalent(other, "b", "glow"));
    }

    [Fact]
    public void ElMismoPuesto_SoloEnLosClipsConTantosComoElDeAhora()
    {
        EffectInstance Mask(string id) => new() { Id = id, Type = "mask" };
        var first = new Clip { Effects = [Mask("rect1")] };
        var second = new Clip { Effects = [Mask("elipse"), new EffectInstance { Id = "g", Type = "glow" }, Mask("rect2")] };
        var third = new Clip { Effects = [Mask("otra")] };

        // Tras Añadir con cuentas distintas, la nueva del primero no es la vieja del segundo.
        Assert.Equal([(first, "rect1"), (third, "otra")], InspectorTargets.SamePlace(first, "rect1", "mask", [first, second, third]));
        Assert.Equal([(second, "rect2")], InspectorTargets.SamePlace(second, "rect2", "mask", [first, second]));
        Assert.Empty(InspectorTargets.SamePlace(first, "no-esta", "mask", [first, second]));
    }

    [Fact]
    public void VariosComandos_SonUnSoloPaso_UnoVaTalCual_YNingunoEsNulo()
    {
        var one = new SequenceEditCommand("Activar filtro", "seq", (Rodaje.Core.Editing.EditContext _) => { });
        var two = new SequenceEditCommand("Activar filtro", "seq", (Rodaje.Core.Editing.EditContext _) => { });

        Assert.Null(InspectorTargets.AsOneStep([]));
        Assert.Same(one, InspectorTargets.AsOneStep([one]));
        CompositeCommand both = Assert.IsType<CompositeCommand>(InspectorTargets.AsOneStep([one, two]));
        Assert.Equal("Activar filtro", both.Description);
        Assert.Equal(2, both.Commands.Count);
    }
}
