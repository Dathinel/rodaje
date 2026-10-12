using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Editing.Trimming;

/// <summary>Cortar en todas las pistas a la vez y borrar a un lado del cursor (Q/W).</summary>
public sealed class CutsAtPointTests
{
    // v1: c1 [0,50) enlazado con s1 en a1, y c2 [50,60) enlazado con s2. v2: o1 [60,70).
    private static TrimFixture Edited() => new TrimFixture()
        .Add("v1", "c1", "m1", 0, 50, 0, link: "L1")
        .Add("a1", "s1", "m1", 0, 50, 0, link: "L1")
        .Add("v1", "c2", "m2", 0, 10, 50, link: "L2")
        .Add("a1", "s2", "m2", 0, 10, 50, link: "L2")
        .Add("v2", "o1", "m2", 0, 10, 60);

    [Fact]
    public void Cortar_en_todas_corta_cada_pista_que_pasa_por_el_instante()
    {
        TrimFixture f = new TrimFixture()
            .Add("v1", "c1", "m1", 0, 50, 0)
            .Add("v2", "o1", "m2", 0, 40, 10)
            .Add("a2", "m", "m3", 0, 50, 0)
            .Add("a1", "lejos", "m2", 0, 10, 80);

        IReadOnlyList<string> ids = TimelineEdits.SplitAllTracks(f.Ctx, F(20));

        Assert.Equal(["clip-1", "clip-2", "clip-3"], ids);
        f.AssertClip("c1", 0, 20, 0);
        f.AssertClip("clip-1", 20, 50, 20);
        f.AssertClip("o1", 0, 10, 10);
        f.AssertClip("clip-2", 10, 40, 20);
        f.AssertClip("m", 0, 20, 0);
        f.AssertClip("clip-3", 20, 50, 20);
        f.AssertClip("lejos", 0, 10, 80);
        Assert.Single(f.Track("a1").Clips);
        f.AssertValid();
    }

    [Fact]
    public void Cortar_en_todas_da_a_las_piezas_derechas_de_un_enlace_un_enlace_nuevo_comun()
    {
        TrimFixture f = Edited();

        IReadOnlyList<string> ids = TimelineEdits.SplitAllTracks(f.Ctx, F(20));

        Assert.Equal(2, ids.Count);
        string? derived = f.Clip(ids[0]).LinkId;
        Assert.NotNull(derived);
        Assert.NotEqual("L1", derived);
        Assert.Equal(derived, f.Clip(ids[1]).LinkId);
        Assert.Equal("L1", f.Clip("c1").LinkId);
        Assert.Equal("L1", f.Clip("s1").LinkId);
        f.AssertValid();
    }

    [Fact]
    public void Cortar_en_todas_salta_las_pistas_bloqueadas_y_deja_entera_a_la_pareja()
    {
        TrimFixture f = Edited().Lock("a1");

        IReadOnlyList<string> ids = TimelineEdits.SplitAllTracks(f.Ctx, F(20));

        string right = Assert.Single(ids);
        f.AssertClip("c1", 0, 20, 0);
        f.AssertClip(right, 20, 50, 20);
        f.AssertClip("s1", 0, 50, 0);
        Assert.Equal("L1", f.Clip("s1").LinkId);
        Assert.Equal(2, f.Track("a1").Clips.Count);
        f.AssertValid();
    }

    [Fact]
    public void Cortar_en_todas_en_un_borde_o_en_un_hueco_falla_sin_cambiar_nada()
    {
        TrimFixture f = Edited();

        Assert.False(TimelineEdits.CanSplitAllTracks(f.Sequence, F(50)));
        Assert.False(TimelineEdits.CanSplitAllTracks(f.Sequence, F(90)));
        Assert.True(TimelineEdits.CanSplitAllTracks(f.Sequence, F(65)));
        AssertFails(() => TimelineEdits.SplitAllTracks(f.Ctx, F(50)), "nada que cortar");
        Assert.Equal(2, f.Track("v1").Clips.Count);
    }

    [Fact]
    public void Cortar_en_todas_con_todo_bloqueado_no_puede()
    {
        TrimFixture f = Edited().Lock("v1").Lock("a1");

        Assert.False(TimelineEdits.CanSplitAllTracks(f.Sequence, F(20)));
    }

    [Fact]
    public void Cortar_en_todas_se_deshace_en_un_paso_y_rehace_con_los_mismos_ids()
    {
        TrimFixture f = Edited();
        var history = new UndoStack(f.Project);
        SequenceEditCommand command = Edits.SplitAllTracks("seq", F(20), new SequentialIdGenerator());

        history.Execute(command);
        IReadOnlyList<string> created = command.CreatedIds;
        Assert.Equal(2, created.Count);
        Assert.Equal(1, history.Count);

        history.Undo();
        Assert.Equal(2, f.Track("v1").Clips.Count);
        f.AssertClip("c1", 0, 50, 0);
        f.AssertClip("s1", 0, 50, 0);

        history.Redo();
        Assert.Equal(created, command.CreatedIds);
        f.AssertClip(created[0], 20, 50, 20);
        f.AssertValid();
    }

    [Fact]
    public void Borrar_a_la_izquierda_sin_ripple_recorta_el_inicio_y_deja_el_hueco()
    {
        TrimFixture f = Edited();

        TimelineEdits.DeleteToPoint(f.Ctx, ["c1"], F(20), TrimEdge.In, ripple: false, RippleScope.Track);

        f.AssertClip("c1", 20, 50, 20);
        f.AssertClip("s1", 20, 50, 20);
        f.AssertClip("c2", 0, 10, 50);
        f.AssertClip("o1", 0, 10, 60);
        f.AssertValid();
    }

    [Fact]
    public void Borrar_a_la_derecha_sin_ripple_recorta_el_final_y_deja_el_hueco()
    {
        TrimFixture f = Edited();

        TimelineEdits.DeleteToPoint(f.Ctx, ["c1"], F(20), TrimEdge.Out, ripple: false, RippleScope.Track);

        f.AssertClip("c1", 0, 20, 0);
        f.AssertClip("s1", 0, 20, 0);
        f.AssertClip("c2", 0, 10, 50);
        f.AssertValid();
    }

    [Fact]
    public void Borrar_a_la_izquierda_con_ripple_de_pista_deja_el_clip_donde_empezaba_y_corre_lo_que_sigue()
    {
        TrimFixture f = Edited();

        TimelineEdits.DeleteToPoint(f.Ctx, ["c1"], F(20), TrimEdge.In, ripple: true, RippleScope.Track);

        f.AssertClip("c1", 20, 50, 0);
        f.AssertClip("s1", 20, 50, 0);
        f.AssertClip("c2", 0, 10, 30);
        f.AssertClip("s2", 0, 10, 30);
        f.AssertClip("o1", 0, 10, 60);
        f.AssertValid();
    }

    [Fact]
    public void Borrar_a_la_derecha_con_ripple_de_pista_corre_lo_que_sigue()
    {
        TrimFixture f = Edited();

        TimelineEdits.DeleteToPoint(f.Ctx, ["c1"], F(20), TrimEdge.Out, ripple: true, RippleScope.Track);

        f.AssertClip("c1", 0, 20, 0);
        f.AssertClip("s1", 0, 20, 0);
        f.AssertClip("c2", 0, 10, 20);
        f.AssertClip("s2", 0, 10, 20);
        f.AssertClip("o1", 0, 10, 60);
        f.AssertValid();
    }

    [Fact]
    public void Con_ripple_de_pista_un_enlazado_de_otro_largo_cierra_solo_su_tramo()
    {
        TrimFixture f = new TrimFixture()
            .Add("v1", "c1", "m1", 0, 50, 0, link: "L1")
            .Add("a1", "s1", "m1", 0, 30, 10, link: "L1")
            .Add("v1", "c2", "m2", 0, 10, 50)
            .Add("a1", "s2", "m3", 0, 10, 40);

        TimelineEdits.DeleteToPoint(f.Ctx, ["c1"], F(20), TrimEdge.In, ripple: true, RippleScope.Track);

        f.AssertClip("c1", 20, 50, 0);
        f.AssertClip("s1", 10, 30, 10);
        f.AssertClip("c2", 0, 10, 30);
        f.AssertClip("s2", 0, 10, 30);
        f.AssertValid();
    }

    [Fact]
    public void Con_todas_las_pistas_se_corren_todas_y_los_marcadores_si_el_modo_lo_pide()
    {
        TrimFixture f = Edited();
        f.Sequence.Markers.Add(new Marker { Id = "mk", Time = F(65) });

        TimelineEdits.DeleteToPoint(f.Ctx, ["c1"], F(20), TrimEdge.Out, ripple: true, RippleScope.AllTracksAndMarkers);

        f.AssertClip("c1", 0, 20, 0);
        f.AssertClip("c2", 0, 10, 20);
        f.AssertClip("s2", 0, 10, 20);
        f.AssertClip("o1", 0, 10, 30);
        Assert.Equal(F(35), f.Sequence.Markers[0].Time);
        f.AssertValid();
    }

    [Fact]
    public void Con_todas_las_pistas_la_misma_seleccion_quita_la_union_de_los_tramos()
    {
        TrimFixture f = new TrimFixture()
            .Add("v1", "c1", "m1", 0, 50, 0)
            .Add("v2", "o1", "m2", 0, 40, 10)
            .Add("v1", "c2", "m2", 0, 10, 60);

        TimelineEdits.DeleteToPoint(f.Ctx, ["c1", "o1"], F(30), TrimEdge.In, ripple: true, RippleScope.AllTracks);

        f.AssertClip("c1", 30, 50, 0);
        f.AssertClip("o1", 20, 40, 0);
        f.AssertClip("c2", 0, 10, 30);
        f.AssertValid();
    }

    [Fact]
    public void Con_todas_las_pistas_falla_si_otro_clip_atraviesa_el_tramo()
    {
        TrimFixture f = Edited().Add("v2", "largo", "m2", 0, 40, 5);

        AssertFails(
            () => TimelineEdits.DeleteToPoint(f.Ctx, ["c1"], F(20), TrimEdge.In, ripple: true, RippleScope.AllTracks),
            "atraviesa");
    }

    [Fact]
    public void Solo_se_tocan_los_clips_que_pasan_por_el_instante()
    {
        TrimFixture f = Edited();

        TimelineEdits.DeleteToPoint(f.Ctx, ["c1", "c2"], F(20), TrimEdge.Out, ripple: false, RippleScope.Track);

        f.AssertClip("c1", 0, 20, 0);
        f.AssertClip("c2", 0, 10, 50);
        f.AssertValid();
    }

    [Fact]
    public void Falla_si_ningun_clip_pasa_por_el_instante_o_la_pareja_esta_bloqueada()
    {
        TrimFixture f = Edited();
        AssertFails(
            () => TimelineEdits.DeleteToPoint(f.Ctx, ["c2"], F(20), TrimEdge.In, ripple: false, RippleScope.Track),
            "nada que borrar");
        AssertFails(
            () => TimelineEdits.DeleteToPoint(f.Ctx, ["c1"], F(50), TrimEdge.Out, ripple: false, RippleScope.Track),
            "nada que borrar");

        f.Lock("a1");
        AssertFails(
            () => TimelineEdits.DeleteToPoint(f.Ctx, ["c1"], F(20), TrimEdge.In, ripple: true, RippleScope.Track),
            "bloqueada");
    }

    [Fact]
    public void Borrar_a_un_lado_se_deshace_en_un_paso()
    {
        TrimFixture f = Edited();
        var history = new UndoStack(f.Project);

        history.Execute(Edits.DeleteToPoint("seq", ["c1"], F(20), TrimEdge.In, ripple: true, RippleScope.AllTracks, new SequentialIdGenerator()));
        Assert.Equal("Borrar a la izquierda del cursor", history.UndoDescription);
        f.AssertClip("o1", 0, 10, 40);

        history.Undo();
        f.AssertClip("c1", 0, 50, 0);
        f.AssertClip("s1", 0, 50, 0);
        f.AssertClip("c2", 0, 10, 50);
        f.AssertClip("o1", 0, 10, 60);
        f.AssertValid();
    }
}
