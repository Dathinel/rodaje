using FotoToma.Core.Editing;
using static FotoToma.Core.Tests.Editing.Trimming.TrimFixture;

namespace FotoToma.Core.Tests.Editing.Trimming;

public sealed class TrimTests
{
    [Fact]
    public void Alargar_la_salida_solo_mueve_el_punto_de_salida()
    {
        var f = new TrimFixture().Add("v1", "c1", "m1", 10, 60, 0);

        TimelineEdits.Trim(f.Ctx, "c1", TrimEdge.Out, F(5));

        f.AssertClip("c1", 10, 65, 0);
        Assert.Equal(F(55), f.Clip("c1").End);
        f.AssertValid();
    }

    [Fact]
    public void Acortar_la_salida_abre_un_hueco_sin_correr_al_vecino()
    {
        var f = new TrimFixture()
            .Add("v1", "c1", "m1", 10, 60, 0)
            .Add("v1", "c2", "m2", 0, 10, 50);

        TimelineEdits.Trim(f.Ctx, "c1", TrimEdge.Out, F(-20));

        f.AssertClip("c1", 10, 40, 0);
        f.AssertClip("c2", 0, 10, 50);
        f.AssertValid();
    }

    [Fact]
    public void Recortar_la_entrada_mueve_inicio_y_punto_de_entrada()
    {
        var f = new TrimFixture().Add("v1", "c1", "m1", 10, 60, 20);

        TimelineEdits.Trim(f.Ctx, "c1", TrimEdge.In, F(5));

        f.AssertClip("c1", 15, 60, 25);
        Assert.Equal(F(70), f.Clip("c1").End);
        f.AssertValid();
    }

    [Fact]
    public void Alargar_la_entrada_hacia_atras_hasta_tocar_al_vecino()
    {
        var f = new TrimFixture()
            .Add("v1", "c0", "m2", 0, 10, 0)
            .Add("v1", "c1", "m1", 10, 60, 20);

        TimelineEdits.Trim(f.Ctx, "c1", TrimEdge.In, F(-10));

        f.AssertClip("c1", 0, 60, 10);
        f.AssertValid();
    }

    [Fact]
    public void Los_clips_enlazados_se_recortan_juntos()
    {
        var f = new TrimFixture()
            .Add("v1", "c1", "m1", 10, 60, 20, link: "L1")
            .Add("a1", "s1", "m1", 10, 60, 20, link: "L1")
            .Add("a2", "x", "m3", 0, 10, 0);

        TimelineEdits.Trim(f.Ctx, "s1", TrimEdge.In, F(3));

        f.AssertClip("c1", 13, 60, 23);
        f.AssertClip("s1", 13, 60, 23);
        f.AssertClip("x", 0, 10, 0);
        f.AssertValid();
    }

    [Fact]
    public void Delta_cero_no_cambia_nada()
    {
        var f = new TrimFixture().Add("v1", "c1", "m1", 10, 60, 20);

        TimelineEdits.Trim(f.Ctx, "c1", TrimEdge.In, FotoToma.Core.Timing.Time.Zero);

        f.AssertClip("c1", 10, 60, 20);
        f.AssertValid();
    }

    [Theory]
    [InlineData(TrimEdge.In, -11, "empezaría antes del inicio del archivo")]
    [InlineData(TrimEdge.In, 50, "se quedaría sin duración")]
    [InlineData(TrimEdge.In, 70, "se quedaría sin duración")]
    [InlineData(TrimEdge.Out, -50, "se quedaría sin duración")]
    [InlineData(TrimEdge.Out, 41, "llegaría más allá del final del archivo")]
    public void Falla_al_salirse_de_los_limites_del_archivo(TrimEdge edge, long deltaFrames, string message)
    {
        var f = new TrimFixture().Add("v1", "c1", "m1", 10, 60, 20);

        AssertFails(() => TimelineEdits.Trim(f.Ctx, "c1", edge, F(deltaFrames)), message);

        f.AssertClip("c1", 10, 60, 20);
    }

    [Fact]
    public void Falla_si_el_clip_quedaria_antes_del_inicio_de_la_linea_de_tiempo()
    {
        var f = new TrimFixture().Add("v1", "c1", "m1", 10, 60, 2);

        AssertFails(() => TimelineEdits.Trim(f.Ctx, "c1", TrimEdge.In, F(-5)), "antes del inicio de la línea de tiempo");

        f.AssertClip("c1", 10, 60, 2);
    }

    [Fact]
    public void Falla_al_chocar_con_el_vecino_de_la_derecha()
    {
        var f = new TrimFixture()
            .Add("v1", "c1", "m1", 10, 60, 0)
            .Add("v1", "c2", "m2", 0, 10, 55);

        AssertFails(() => TimelineEdits.Trim(f.Ctx, "c1", TrimEdge.Out, F(6)), "«c1» chocaría con el clip «c2»");

        f.AssertClip("c1", 10, 60, 0);
    }

    [Fact]
    public void Falla_al_chocar_con_el_vecino_de_la_izquierda()
    {
        var f = new TrimFixture()
            .Add("v1", "c0", "m2", 0, 10, 0)
            .Add("v1", "c1", "m1", 20, 60, 20);

        AssertFails(() => TimelineEdits.Trim(f.Ctx, "c1", TrimEdge.In, F(-11)), "chocaría con el clip «c0»");
    }

    [Fact]
    public void Falla_si_el_enlazado_choca_aunque_el_clip_pedido_quepa()
    {
        var f = new TrimFixture()
            .Add("v1", "c1", "m1", 10, 60, 0, link: "L1")
            .Add("a1", "s1", "m1", 10, 60, 0, link: "L1")
            .Add("a1", "s2", "m3", 0, 10, 52);

        AssertFails(() => TimelineEdits.Trim(f.Ctx, "c1", TrimEdge.Out, F(5)), "«s1» chocaría con el clip «s2»");

        f.AssertClip("c1", 10, 60, 0);
        f.AssertClip("s1", 10, 60, 0);
    }

    [Fact]
    public void Falla_si_la_pista_esta_bloqueada()
    {
        var f = new TrimFixture().Add("v1", "c1", "m1", 10, 60, 0).Lock("v1");

        AssertFails(() => TimelineEdits.Trim(f.Ctx, "c1", TrimEdge.Out, F(1)), "«V1» está bloqueada");
    }

    [Fact]
    public void Falla_si_la_pista_del_enlazado_esta_bloqueada()
    {
        var f = new TrimFixture()
            .Add("v1", "c1", "m1", 10, 60, 0, link: "L1")
            .Add("a1", "s1", "m1", 10, 60, 0, link: "L1")
            .Lock("a1");

        AssertFails(() => TimelineEdits.Trim(f.Ctx, "c1", TrimEdge.Out, F(1)), "«A1» está bloqueada");

        f.AssertClip("c1", 10, 60, 0);
    }

    [Fact]
    public void Falla_si_el_clip_no_existe()
    {
        var f = new TrimFixture();

        AssertFails(() => TimelineEdits.Trim(f.Ctx, "nada", TrimEdge.Out, F(1)), "No existe el clip «nada»");
        AssertFails(() => TimelineEdits.Trim(f.Ctx, null!, TrimEdge.Out, F(1)), "No existe el clip");
    }

    [Fact]
    public void Falla_si_el_medio_ya_no_esta_en_el_proyecto()
    {
        var f = new TrimFixture().Add("v1", "c1", "m1", 10, 60, 0);
        f.Project.Media.RemoveAll(m => m.Id == "m1");

        AssertFails(() => TimelineEdits.Trim(f.Ctx, "c1", TrimEdge.Out, F(1)), "usa un medio que ya no está");
    }
}

public sealed class RippleTrimTests
{
    private static TrimFixture ThreeTracks() => new TrimFixture()
        .Add("v1", "c1", "m1", 0, 50, 0)
        .Add("v1", "c2", "m2", 0, 10, 50)
        .Add("v1", "c3", "m2", 20, 30, 70)
        .Add("v2", "o1", "m2", 0, 10, 0)
        .Add("v2", "o2", "m2", 0, 10, 60)
        .Add("a1", "s1", "m3", 0, 10, 50);

    [Fact]
    public void Alargar_la_salida_corre_lo_que_sigue_en_la_misma_pista()
    {
        TrimFixture f = ThreeTracks();

        TimelineEdits.RippleTrim(f.Ctx, "c1", TrimEdge.Out, F(10), RippleScope.Track);

        f.AssertClip("c1", 0, 60, 0);
        f.AssertClip("c2", 0, 10, 60);
        f.AssertClip("c3", 20, 30, 80);
        f.AssertClip("o1", 0, 10, 0);
        f.AssertClip("o2", 0, 10, 60);
        f.AssertClip("s1", 0, 10, 50);
        f.AssertValid();
    }

    [Fact]
    public void Con_todas_las_pistas_corre_tambien_las_demas()
    {
        TrimFixture f = ThreeTracks();

        TimelineEdits.RippleTrim(f.Ctx, "c1", TrimEdge.Out, F(10), RippleScope.AllTracks);

        f.AssertClip("c1", 0, 60, 0);
        f.AssertClip("c2", 0, 10, 60);
        f.AssertClip("c3", 20, 30, 80);
        f.AssertClip("o1", 0, 10, 0);
        f.AssertClip("o2", 0, 10, 70);
        f.AssertClip("s1", 0, 10, 60);
        f.AssertValid();
    }

    [Fact]
    public void Con_todas_las_pistas_se_salta_las_bloqueadas()
    {
        TrimFixture f = ThreeTracks().Lock("v2");

        TimelineEdits.RippleTrim(f.Ctx, "c1", TrimEdge.Out, F(10), RippleScope.AllTracks);

        f.AssertClip("o2", 0, 10, 60);
        f.AssertClip("s1", 0, 10, 60);
        f.AssertClip("c2", 0, 10, 60);
        f.AssertValid();
    }

    [Fact]
    public void Acortar_la_salida_cierra_el_hueco()
    {
        TrimFixture f = ThreeTracks();

        TimelineEdits.RippleTrim(f.Ctx, "c1", TrimEdge.Out, F(-20), RippleScope.Track);

        f.AssertClip("c1", 0, 30, 0);
        f.AssertClip("c2", 0, 10, 30);
        f.AssertClip("c3", 20, 30, 50);
        f.AssertValid();
    }

    [Fact]
    public void Recortar_la_entrada_deja_el_inicio_y_corre_lo_que_sigue_hacia_atras()
    {
        TrimFixture f = ThreeTracks();

        TimelineEdits.RippleTrim(f.Ctx, "c1", TrimEdge.In, F(10), RippleScope.Track);

        f.AssertClip("c1", 10, 50, 0);
        Assert.Equal(F(40), f.Clip("c1").End);
        f.AssertClip("c2", 0, 10, 40);
        f.AssertClip("c3", 20, 30, 60);
        f.AssertValid();
    }

    [Fact]
    public void Alargar_la_entrada_hacia_atras_empuja_lo_que_sigue()
    {
        var f = new TrimFixture()
            .Add("v1", "c1", "m1", 10, 50, 0)
            .Add("v1", "c2", "m2", 0, 10, 40);

        TimelineEdits.RippleTrim(f.Ctx, "c1", TrimEdge.In, F(-10), RippleScope.Track);

        f.AssertClip("c1", 0, 50, 0);
        f.AssertClip("c2", 0, 10, 50);
        f.AssertValid();
    }

    [Fact]
    public void Lo_que_esta_antes_del_clip_no_se_mueve()
    {
        var f = new TrimFixture()
            .Add("v1", "c0", "m2", 0, 10, 0)
            .Add("v1", "c1", "m1", 0, 50, 10)
            .Add("v2", "o1", "m2", 0, 30, 20);

        TimelineEdits.RippleTrim(f.Ctx, "c1", TrimEdge.Out, F(5), RippleScope.AllTracks);

        f.AssertClip("c0", 0, 10, 0);
        f.AssertClip("o1", 0, 30, 20);
        f.AssertClip("c1", 0, 55, 10);
        f.AssertValid();
    }

    [Fact]
    public void El_grupo_enlazado_se_recorta_junto_y_cada_vecino_se_corre_una_sola_vez()
    {
        var f = new TrimFixture()
            .Add("v1", "c1", "m1", 0, 50, 0, link: "L1")
            .Add("a1", "s1", "m1", 0, 50, 0, link: "L1")
            .Add("v1", "c2", "m2", 0, 10, 50)
            .Add("a1", "s2", "m3", 0, 10, 50)
            .Add("a2", "x", "m3", 0, 10, 60);

        TimelineEdits.RippleTrim(f.Ctx, "s1", TrimEdge.Out, F(-10), RippleScope.Track);

        f.AssertClip("c1", 0, 40, 0);
        f.AssertClip("s1", 0, 40, 0);
        f.AssertClip("c2", 0, 10, 40);
        f.AssertClip("s2", 0, 10, 40);
        f.AssertClip("x", 0, 10, 60);
        f.AssertValid();
    }

    [Theory]
    [InlineData(TrimEdge.In, -1, "empezaría antes del inicio del archivo")]
    [InlineData(TrimEdge.In, 50, "se quedaría sin duración")]
    [InlineData(TrimEdge.Out, -50, "se quedaría sin duración")]
    [InlineData(TrimEdge.Out, 51, "llegaría más allá del final del archivo")]
    public void Falla_al_salirse_de_los_limites_sin_cambiar_nada(TrimEdge edge, long deltaFrames, string message)
    {
        TrimFixture f = ThreeTracks();

        AssertFails(() => TimelineEdits.RippleTrim(f.Ctx, "c1", edge, F(deltaFrames), RippleScope.AllTracks), message);

        f.AssertClip("c1", 0, 50, 0);
        f.AssertClip("c2", 0, 10, 50);
        f.AssertClip("o2", 0, 10, 60);
    }

    [Fact]
    public void Falla_si_la_pista_editada_esta_bloqueada()
    {
        TrimFixture f = ThreeTracks().Lock("v1");

        AssertFails(() => TimelineEdits.RippleTrim(f.Ctx, "c1", TrimEdge.Out, F(5), RippleScope.Track), "«V1» está bloqueada");

        f.AssertClip("c1", 0, 50, 0);
    }

    [Fact]
    public void Falla_si_al_correr_todas_las_pistas_un_clip_cae_sobre_otro_que_no_se_corre()
    {
        // En V2, «largo» empieza antes del corte y termina después; «tras» va pegado a él.
        var f = new TrimFixture()
            .Add("v1", "c1", "m1", 0, 50, 0)
            .Add("v2", "largo", "m2", 0, 30, 40)
            .Add("v2", "tras", "m2", 0, 10, 70);

        AssertFails(
            () => TimelineEdits.RippleTrim(f.Ctx, "c1", TrimEdge.In, F(10), RippleScope.AllTracks),
            "«largo» chocaría con el clip «tras»");
    }

    [Fact]
    public void Falla_si_un_enlazado_mas_largo_choca_con_un_vecino_que_no_se_corre()
    {
        // El audio enlazado termina antes que el video, así que su vecino empieza antes del punto
        // desde el que se corre y no se mueve.
        var f = new TrimFixture()
            .Add("v1", "c1", "m1", 0, 50, 0, link: "L1")
            .Add("a1", "s1", "m1", 0, 40, 0, link: "L1")
            .Add("a1", "s2", "m3", 0, 10, 40);

        AssertFails(
            () => TimelineEdits.RippleTrim(f.Ctx, "c1", TrimEdge.Out, F(5), RippleScope.Track),
            "«s1» chocaría con el clip «s2»");
    }

    [Fact]
    public void Falla_si_el_clip_no_existe()
    {
        var f = new TrimFixture();

        AssertFails(() => TimelineEdits.RippleTrim(f.Ctx, "nada", TrimEdge.In, F(1), RippleScope.Track), "No existe el clip «nada»");
    }
}
