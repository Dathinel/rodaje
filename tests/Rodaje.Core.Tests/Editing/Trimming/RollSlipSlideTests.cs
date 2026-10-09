using Rodaje.Core.Editing;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Editing.Trimming;

public sealed class RollTests
{
    private static TrimFixture Pair() => new TrimFixture()
        .Add("v1", "c1", "m1", 0, 50, 0)
        .Add("v1", "c2", "m2", 10, 60, 50);

    [Theory]
    [InlineData(5, 55, 15)]
    [InlineData(-5, 45, 5)]
    [InlineData(-10, 40, 0)]
    [InlineData(49, 99, 59)]
    public void Mueve_el_corte_entre_los_dos_clips(long delta, long c1Out, long c2In)
    {
        TrimFixture f = Pair();

        TimelineEdits.Roll(f.Ctx, "c1", F(delta));

        f.AssertClip("c1", 0, c1Out, 0);
        f.AssertClip("c2", c2In, 60, c1Out);
        Assert.Equal(F(100), f.Clip("c2").End);
        f.AssertValid();
    }

    [Fact]
    public void Sin_clip_pegado_es_un_recorte_de_salida()
    {
        var f = new TrimFixture()
            .Add("v1", "c1", "m1", 0, 50, 0)
            .Add("v1", "c2", "m2", 10, 60, 60);

        TimelineEdits.Roll(f.Ctx, "c1", F(5));

        f.AssertClip("c1", 0, 55, 0);
        f.AssertClip("c2", 10, 60, 60);
        f.AssertValid();
    }

    [Fact]
    public void Sin_clip_pegado_no_puede_montarse_sobre_el_siguiente()
    {
        var f = new TrimFixture()
            .Add("v1", "c1", "m1", 0, 50, 0)
            .Add("v1", "c2", "m2", 10, 60, 60);

        AssertFails(() => TimelineEdits.Roll(f.Ctx, "c1", F(11)), "«c1» chocaría con el clip «c2»");

        f.AssertClip("c1", 0, 50, 0);
    }

    [Fact]
    public void Cada_enlazado_mueve_su_propio_corte()
    {
        var f = new TrimFixture()
            .Add("v1", "c1", "m1", 0, 50, 0, link: "L1")
            .Add("a1", "s1", "m1", 0, 50, 0, link: "L1")
            .Add("v1", "c2", "m2", 10, 60, 50)
            .Add("a1", "s2", "m3", 0, 10, 70);

        TimelineEdits.Roll(f.Ctx, "s1", F(5));

        f.AssertClip("c1", 0, 55, 0);
        f.AssertClip("c2", 15, 60, 55);
        f.AssertClip("s1", 0, 55, 0);
        f.AssertClip("s2", 0, 10, 70);
        f.AssertValid();
    }

    [Theory]
    [InlineData(-11, "«c2» empezaría antes del inicio del archivo")]
    [InlineData(50, "«c2» se quedaría sin duración")]
    [InlineData(-50, "«c1» se quedaría sin duración")]
    [InlineData(51, "«c1» llegaría más allá del final del archivo")]
    public void Falla_si_alguno_de_los_dos_se_sale_de_sus_limites(long delta, string message)
    {
        TrimFixture f = Pair();

        AssertFails(() => TimelineEdits.Roll(f.Ctx, "c1", F(delta)), message);

        f.AssertClip("c1", 0, 50, 0);
        f.AssertClip("c2", 10, 60, 50);
    }

    [Fact]
    public void Falla_si_la_pista_esta_bloqueada()
    {
        TrimFixture f = Pair().Lock("v1");

        AssertFails(() => TimelineEdits.Roll(f.Ctx, "c1", F(1)), "bloqueada");
    }

    [Fact]
    public void Falla_si_el_clip_no_existe()
    {
        AssertFails(() => TimelineEdits.Roll(new TrimFixture().Ctx, "nada", F(1)), "No existe el clip «nada»");
    }
}

public sealed class SlipTests
{
    [Theory]
    [InlineData(5, 15, 65)]
    [InlineData(-10, 0, 50)]
    [InlineData(40, 50, 100)]
    public void Desliza_el_contenido_sin_mover_el_clip(long delta, long newIn, long newOut)
    {
        var f = new TrimFixture()
            .Add("v1", "c0", "m2", 0, 20, 0)
            .Add("v1", "c1", "m1", 10, 60, 20)
            .Add("v1", "c2", "m2", 0, 20, 70);

        TimelineEdits.Slip(f.Ctx, "c1", F(delta));

        f.AssertClip("c1", newIn, newOut, 20);
        f.AssertClip("c0", 0, 20, 0);
        f.AssertClip("c2", 0, 20, 70);
        f.AssertValid();
    }

    [Fact]
    public void Desliza_tambien_los_enlazados()
    {
        var f = new TrimFixture()
            .Add("v1", "c1", "m1", 10, 60, 20, link: "L1")
            .Add("a1", "s1", "m3", 0, 40, 20, link: "L1");

        TimelineEdits.Slip(f.Ctx, "c1", F(5));

        f.AssertClip("c1", 15, 65, 20);
        f.AssertClip("s1", 5, 45, 20);
        f.AssertValid();
    }

    [Theory]
    [InlineData(-11, "«c1» empezaría antes del inicio del archivo")]
    [InlineData(41, "«c1» llegaría más allá del final del archivo")]
    public void Falla_al_salirse_del_archivo(long delta, string message)
    {
        var f = new TrimFixture().Add("v1", "c1", "m1", 10, 60, 20);

        AssertFails(() => TimelineEdits.Slip(f.Ctx, "c1", F(delta)), message);

        f.AssertClip("c1", 10, 60, 20);
    }

    [Fact]
    public void Falla_sin_cambiar_nada_si_el_enlazado_se_sale_de_su_archivo()
    {
        var f = new TrimFixture()
            .Add("v1", "c1", "m1", 10, 60, 20, link: "L1")
            .Add("a1", "s1", "m3", 0, 40, 20, link: "L1");

        AssertFails(() => TimelineEdits.Slip(f.Ctx, "c1", F(11)), "«s1» llegaría más allá del final del archivo");

        f.AssertClip("c1", 10, 60, 20);
        f.AssertClip("s1", 0, 40, 20);
    }

    [Fact]
    public void Falla_si_la_pista_esta_bloqueada()
    {
        var f = new TrimFixture().Add("v1", "c1", "m1", 10, 60, 20).Lock("v1");

        AssertFails(() => TimelineEdits.Slip(f.Ctx, "c1", F(1)), "bloqueada");
    }

    [Fact]
    public void Falla_si_el_clip_no_existe()
    {
        AssertFails(() => TimelineEdits.Slip(new TrimFixture().Ctx, "nada", F(1)), "No existe el clip «nada»");
    }
}

public sealed class SlideTests
{
    /// <summary>p (0–50) | c (50–70) | n (70–110), los tres pegados en V1.</summary>
    private static TrimFixture Three() => new TrimFixture()
        .Add("v1", "p", "m1", 0, 50, 0)
        .Add("v1", "c", "m2", 0, 20, 50)
        .Add("v1", "n", "m1", 50, 90, 70);

    [Theory]
    [InlineData(5)]
    [InlineData(-5)]
    [InlineData(39)]
    [InlineData(-49)]
    public void Mueve_el_clip_y_ajusta_a_los_dos_vecinos(long delta)
    {
        TrimFixture f = Three();

        TimelineEdits.Slide(f.Ctx, "c", F(delta));

        f.AssertClip("p", 0, 50 + delta, 0);
        f.AssertClip("c", 0, 20, 50 + delta);
        f.AssertClip("n", 50 + delta, 90, 70 + delta);
        Assert.Equal(F(110), f.Clip("n").End);
        f.AssertValid();
    }

    [Fact]
    public void Sin_vecinos_pegados_el_clip_solo_se_mueve()
    {
        var f = new TrimFixture()
            .Add("v1", "p", "m1", 0, 40, 0)
            .Add("v1", "c", "m2", 0, 20, 50)
            .Add("v1", "x", "m2", 0, 10, 90);

        TimelineEdits.Slide(f.Ctx, "c", F(10));

        f.AssertClip("p", 0, 40, 0);
        f.AssertClip("c", 0, 20, 60);
        f.AssertClip("x", 0, 10, 90);
        f.AssertValid();
    }

    [Fact]
    public void Solo_con_vecino_anterior_lo_alarga_y_deja_el_hueco_de_despues()
    {
        var f = new TrimFixture()
            .Add("v1", "p", "m1", 0, 50, 0)
            .Add("v1", "c", "m2", 0, 20, 50)
            .Add("v1", "x", "m2", 0, 10, 80);

        TimelineEdits.Slide(f.Ctx, "c", F(5));

        f.AssertClip("p", 0, 55, 0);
        f.AssertClip("c", 0, 20, 55);
        f.AssertClip("x", 0, 10, 80);
        f.AssertValid();
    }

    [Fact]
    public void Solo_con_vecino_siguiente_lo_acorta_desde_la_entrada()
    {
        var f = new TrimFixture()
            .Add("v1", "c", "m2", 0, 20, 50)
            .Add("v1", "n", "m1", 50, 90, 70);

        TimelineEdits.Slide(f.Ctx, "c", F(-10));

        f.AssertClip("c", 0, 20, 40);
        f.AssertClip("n", 40, 90, 60);
        f.AssertValid();
    }

    [Fact]
    public void Los_enlazados_se_deslizan_juntos_cada_uno_con_sus_vecinos()
    {
        var f = new TrimFixture()
            .Add("v1", "p", "m1", 0, 50, 0)
            .Add("v1", "c", "m2", 0, 20, 50, link: "L1")
            .Add("a1", "s", "m2", 0, 20, 50, link: "L1")
            .Add("a1", "sn", "m3", 10, 40, 70);

        TimelineEdits.Slide(f.Ctx, "s", F(5));

        f.AssertClip("p", 0, 55, 0);
        f.AssertClip("c", 0, 20, 55);
        f.AssertClip("s", 0, 20, 55);
        f.AssertClip("sn", 15, 40, 75);
        f.AssertValid();
    }

    [Theory]
    [InlineData(40, "«n» se quedaría sin duración")]
    [InlineData(-50, "«p» se quedaría sin duración")]
    public void Falla_si_un_vecino_se_sale_de_sus_limites(long delta, string message)
    {
        TrimFixture f = Three();

        AssertFails(() => TimelineEdits.Slide(f.Ctx, "c", F(delta)), message);

        f.AssertClip("p", 0, 50, 0);
        f.AssertClip("c", 0, 20, 50);
        f.AssertClip("n", 50, 90, 70);
    }

    [Fact]
    public void Falla_si_el_vecino_anterior_llegaria_mas_alla_de_su_archivo()
    {
        var f = new TrimFixture()
            .Add("v1", "p", "m1", 60, 100, 0)
            .Add("v1", "c", "m2", 0, 20, 40)
            .Add("v1", "n", "m2", 0, 50, 60);

        AssertFails(() => TimelineEdits.Slide(f.Ctx, "c", F(1)), "«p» llegaría más allá del final del archivo");

        f.AssertClip("p", 60, 100, 0);
    }

    [Fact]
    public void Falla_si_el_vecino_siguiente_empezaria_antes_del_inicio_del_archivo()
    {
        var f = new TrimFixture()
            .Add("v1", "c", "m2", 0, 20, 50)
            .Add("v1", "n", "m1", 3, 43, 70);

        AssertFails(() => TimelineEdits.Slide(f.Ctx, "c", F(-5)), "«n» empezaría antes del inicio del archivo");
    }

    [Fact]
    public void Falla_si_sin_vecinos_pegados_se_monta_sobre_otro_clip()
    {
        var f = new TrimFixture()
            .Add("v1", "c", "m2", 0, 20, 50)
            .Add("v1", "x", "m2", 0, 10, 75);

        AssertFails(() => TimelineEdits.Slide(f.Ctx, "c", F(10)), "«c» chocaría con el clip «x»");

        f.AssertClip("c", 0, 20, 50);
    }

    [Fact]
    public void Falla_si_quedaria_antes_del_inicio_de_la_linea_de_tiempo()
    {
        var f = new TrimFixture().Add("v1", "c", "m2", 0, 20, 5);

        AssertFails(() => TimelineEdits.Slide(f.Ctx, "c", F(-6)), "antes del inicio de la línea de tiempo");
    }

    [Fact]
    public void Falla_si_la_pista_esta_bloqueada()
    {
        TrimFixture f = Three().Lock("v1");

        AssertFails(() => TimelineEdits.Slide(f.Ctx, "c", F(1)), "bloqueada");
    }

    [Fact]
    public void Falla_si_el_clip_no_existe()
    {
        AssertFails(() => TimelineEdits.Slide(new TrimFixture().Ctx, "nada", F(1)), "No existe el clip «nada»");
    }
}
