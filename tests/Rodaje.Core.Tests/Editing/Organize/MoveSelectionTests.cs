using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Editing.Trimming;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Editing.Organize;

/// <summary>Mover una selección de varios clips como un solo movimiento: tiempo, pista y pista principal magnética.</summary>
public sealed class MoveSelectionTests
{
    [Fact]
    public void La_seleccion_cambia_de_pista_y_de_tiempo_junta_y_sus_enlazados_solo_de_tiempo()
    {
        var f = new TrimFixture()
            .Add("v1", "x", "m1", 0, 10, 0, "l").Add("a1", "xa", "m1", 0, 10, 0, "l")
            .Add("v1", "y", "m1", 0, 10, 20);

        TimelineEdits.MoveClips(f.Ctx, ["x", "y"], F(5), TrackKind.Video, 1);

        f.AssertValid();
        Assert.Equal(["x", "y"], f.Track("v2").Clips.Select(c => c.Id));
        Assert.Empty(f.Track("v1").Clips);
        f.AssertClip("x", 0, 10, 5);
        f.AssertClip("y", 0, 10, 25);
        Assert.Equal("a1", f.Sequence.GetClip("xa").Track.Id);
        f.AssertClip("xa", 0, 10, 5);
    }

    [Fact]
    public void Si_uno_choca_no_se_mueve_ninguno_y_sin_otra_pista_cae_a_solo_tiempo()
    {
        var f = new TrimFixture()
            .Add("v1", "x", "m1", 0, 10, 0).Add("v1", "y", "m1", 0, 10, 20)
            .Add("v1", "z", "m2", 0, 10, 40);

        AssertFails(() => TimelineEdits.MoveClips(f.Ctx, ["x", "y"], F(15), TrackKind.Video, 0), "ocupado");
        f.AssertClip("x", 0, 10, 0);
        f.AssertClip("y", 0, 10, 20);
        AssertFails(() => TimelineEdits.MoveClips(f.Ctx, ["x", "y"], F(2), TrackKind.Video, 5), "No hay otra pista");

        var stack = new UndoStack(f.Project);
        stack.Execute(Edits.MoveClips("seq", ["x", "y"], F(2), TrackKind.Video, 5));
        f.AssertClip("x", 0, 10, 2);
        Assert.Equal("v1", f.Sequence.GetClip("x").Track.Id);
        stack.Execute(Edits.MoveClips("seq", ["x", "y"], F(3), TrackKind.Video, 1));
        Assert.Equal(1, stack.Count);
        Assert.Equal("v2", f.Sequence.GetClip("y").Track.Id);
        stack.Undo();
        f.AssertClip("x", 0, 10, 0);
        f.AssertClip("y", 0, 10, 20);
        Assert.Equal("v1", f.Sequence.GetClip("y").Track.Id);
    }

    [Fact]
    public void Solos_no_arrastra_a_los_enlazados()
    {
        var f = new TrimFixture()
            .Add("v1", "x", "m1", 0, 10, 0, "l").Add("a1", "xa", "m1", 0, 10, 0, "l")
            .Add("v1", "y", "m1", 0, 10, 20);

        TimelineEdits.MoveClips(f.Ctx, ["x", "y"], F(4), TrackKind.Video, 0, alone: true);

        f.AssertClip("x", 0, 10, 4);
        f.AssertClip("y", 0, 10, 24);
        f.AssertClip("xa", 0, 10, 0);
    }

    [Fact]
    public void En_la_principal_los_seleccionados_se_reordenan_como_bloque_y_el_resto_los_sigue()
    {
        var f = new TrimFixture()
            .Add("v1", "a", "m1", 0, 10, 0).Add("v1", "b", "m1", 0, 10, 10).Add("v1", "c", "m1", 0, 5, 20)
            .Add("v2", "t", "m2", 0, 5, 2);

        TimelineEdits.MoveClipsOnMainTrack(f.Ctx, ["a", "b", "t"], "v1", 1);

        f.AssertValid();
        Assert.Equal(["c", "a", "b"], f.Track("v1").Clips.Select(c => c.Id));
        f.AssertClip("c", 0, 5, 0);
        f.AssertClip("a", 0, 10, 5);
        f.AssertClip("b", 0, 10, 15);
        f.AssertClip("t", 0, 5, 7);
        AssertFails(() => TimelineEdits.MoveClipsOnMainTrack(f.Ctx, ["t"], "v1", 0), "pista principal");
    }
}
