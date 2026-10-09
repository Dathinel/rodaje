using FotoToma.Core.Editing;
using FotoToma.Core.Model;
using FotoToma.Core.Timing;
using static FotoToma.Core.Tests.Editing.Trimming.TrimFixture;

namespace FotoToma.Core.Tests.Editing.Trimming;

public sealed class SplitTests
{
    [Fact]
    public void Un_cuadro_a_25_fps_son_28_224_000_ticks()
    {
        Assert.Equal(28_224_000, F(1).Ticks);
    }

    [Fact]
    public void Corta_un_clip_suelto_en_dos_piezas_exactas()
    {
        var f = new TrimFixture().Add("v1", "c1", "m1", 10, 60, 5);

        IReadOnlyList<string> ids = TimelineEdits.Split(f.Ctx, "c1", F(25));

        Assert.Equal(["clip-1"], ids);
        f.AssertClip("c1", 10, 30, 5);
        f.AssertClip("clip-1", 30, 60, 25);
        Assert.Equal(new Time(30 * 28_224_000L), f.Clip("clip-1").SourceIn);
        Assert.Equal(f.Clip("c1").End, f.Clip("clip-1").Start);
        Assert.Null(f.Clip("clip-1").LinkId);
        Assert.Equal("m1", f.Clip("clip-1").MediaId);
        Assert.Equal(["c1", "clip-1"], f.Track("v1").Clips.Select(c => c.Id));
        f.AssertValid();
    }

    [Fact]
    public void La_pieza_derecha_queda_justo_despues_aunque_haya_mas_clips()
    {
        var f = new TrimFixture()
            .Add("v1", "c0", "m2", 0, 10, 0)
            .Add("v1", "c1", "m1", 0, 50, 10)
            .Add("v1", "c2", "m2", 0, 10, 80);

        TimelineEdits.Split(f.Ctx, "c1", F(30));

        Assert.Equal(["c0", "c1", "clip-1", "c2"], f.Track("v1").Clips.Select(c => c.Id));
        f.AssertValid();
    }

    [Fact]
    public void Conserva_si_el_clip_esta_desactivado()
    {
        var f = new TrimFixture().Add("v1", "c1", "m1", 0, 50, 0);
        f.Clip("c1").Enabled = false;

        TimelineEdits.Split(f.Ctx, "c1", F(10));

        Assert.False(f.Clip("clip-1").Enabled);
        f.AssertValid();
    }

    [Fact]
    public void Corta_el_video_y_su_audio_y_las_piezas_derechas_comparten_un_enlace_nuevo()
    {
        var f = new TrimFixture()
            .Add("v1", "c1", "m1", 0, 50, 0, link: "L1")
            .Add("a1", "s1", "m1", 0, 50, 0, link: "L1");

        IReadOnlyList<string> ids = TimelineEdits.Split(f.Ctx, "c1", F(20));

        // Orden de identificadores: pieza del video, enlace derivado, pieza del audio.
        Assert.Equal(["clip-1", "clip-3"], ids);
        f.AssertClip("c1", 0, 20, 0);
        f.AssertClip("s1", 0, 20, 0);
        f.AssertClip("clip-1", 20, 50, 20);
        f.AssertClip("clip-3", 20, 50, 20);
        Assert.Equal("L1", f.Clip("c1").LinkId);
        Assert.Equal("L1", f.Clip("s1").LinkId);
        Assert.Equal("link-2", f.Clip("clip-1").LinkId);
        Assert.Equal("link-2", f.Clip("clip-3").LinkId);
        Assert.Equal(["clip-3", "clip-1"], f.Sequence.GetLinkGroup("clip-3").Select(m => m.Clip.Id));
        f.AssertValid();
    }

    [Fact]
    public void Pedido_desde_el_audio_devuelve_primero_la_pieza_del_audio()
    {
        var f = new TrimFixture()
            .Add("v1", "c1", "m1", 0, 50, 0, link: "L1")
            .Add("a1", "s1", "m1", 0, 50, 0, link: "L1");

        IReadOnlyList<string> ids = TimelineEdits.Split(f.Ctx, "s1", F(20));

        Assert.Equal(2, ids.Count);
        Assert.Equal("a1", f.Sequence.GetClip(ids[0]).Track.Id);
        Assert.Equal("v1", f.Sequence.GetClip(ids[1]).Track.Id);
        f.AssertValid();
    }

    [Fact]
    public void El_enlazado_que_no_pasa_por_el_instante_no_se_toca()
    {
        var f = new TrimFixture()
            .Add("v1", "c1", "m1", 0, 50, 0, link: "L1")
            .Add("a1", "s1", "m3", 0, 15, 0, link: "L1");

        IReadOnlyList<string> ids = TimelineEdits.Split(f.Ctx, "c1", F(20));

        Assert.Equal(["clip-1"], ids);
        f.AssertClip("s1", 0, 15, 0);
        Assert.Single(f.Track("a1").Clips);
        Assert.Equal("link-2", f.Clip("clip-1").LinkId);
        f.AssertValid();
    }

    [Fact]
    public void El_enlazado_que_termina_justo_en_el_corte_no_se_toca()
    {
        var f = new TrimFixture()
            .Add("v1", "c1", "m1", 0, 50, 0, link: "L1")
            .Add("a1", "s1", "m3", 0, 20, 0, link: "L1");

        TimelineEdits.Split(f.Ctx, "c1", F(20));

        Assert.Single(f.Track("a1").Clips);
        f.AssertValid();
    }

    [Fact]
    public void Copia_los_efectos_con_identificadores_nuevos_y_los_keyframes_intactos()
    {
        var f = new TrimFixture().Add("v1", "c1", "m1", 10, 60, 0, link: "L1");
        var opacity = new Parameter
        {
            Value = 1,
            Keyframes = [new Keyframe(F(10), 0), new Keyframe(F(40), 1, Interpolation.EaseIn)],
        };
        f.Clip("c1").Effects.Add(new EffectInstance
        {
            Id = "e1",
            Type = "fototoma.opacity",
            Parameters = new() { ["opacity"] = opacity },
        });
        f.Clip("c1").Effects.Add(new EffectInstance { Id = "e2", Type = "fototoma.blur", Enabled = false });

        IReadOnlyList<string> ids = TimelineEdits.Split(f.Ctx, "c1", F(20));

        Clip right = f.Clip(ids[0]);
        Assert.Equal("clip-1", right.Id);
        Assert.Equal("link-2", right.LinkId);
        Assert.Equal(["fx-3", "fx-4"], right.Effects.Select(e => e.Id));
        Assert.Equal(["fototoma.opacity", "fototoma.blur"], right.Effects.Select(e => e.Type));
        Assert.False(right.Effects[1].Enabled);
        Assert.Equal(opacity.Keyframes, right.Effects[0].Parameters["opacity"].Keyframes);
        Assert.Equal(["e1", "e2"], f.Clip("c1").Effects.Select(e => e.Id));

        // Copia profunda: tocar la pieza derecha no cambia la izquierda.
        right.Effects[0].Parameters["opacity"].Keyframes.Clear();
        right.Effects[0].Parameters["opacity"].Value = 5;
        Assert.Equal(2, opacity.Keyframes.Count);
        Assert.Equal(1, opacity.Value);
        f.AssertValid();
    }

    [Theory]
    [InlineData(5)]
    [InlineData(55)]
    [InlineData(0)]
    [InlineData(100)]
    public void Falla_si_el_instante_no_cae_estrictamente_dentro_del_clip(long frame)
    {
        var f = new TrimFixture().Add("v1", "c1", "m1", 0, 50, 5);

        AssertFails(() => TimelineEdits.Split(f.Ctx, "c1", F(frame)), "no cae dentro del clip «c1»");

        Assert.Single(f.Track("v1").Clips);
        f.AssertClip("c1", 0, 50, 5);
    }

    [Fact]
    public void Falla_si_el_clip_no_existe()
    {
        var f = new TrimFixture();

        AssertFails(() => TimelineEdits.Split(f.Ctx, "nada", F(1)), "No existe el clip «nada»");
    }

    [Fact]
    public void Falla_si_la_pista_esta_bloqueada()
    {
        var f = new TrimFixture().Add("v1", "c1", "m1", 0, 50, 0).Lock("v1");

        AssertFails(() => TimelineEdits.Split(f.Ctx, "c1", F(10)), "bloqueada");

        Assert.Single(f.Track("v1").Clips);
    }

    [Fact]
    public void Falla_sin_cambiar_nada_si_la_pista_del_enlazado_esta_bloqueada()
    {
        var f = new TrimFixture()
            .Add("v1", "c1", "m1", 0, 50, 0, link: "L1")
            .Add("a1", "s1", "m1", 0, 50, 0, link: "L1")
            .Lock("a1");

        AssertFails(() => TimelineEdits.Split(f.Ctx, "c1", F(10)), "«A1» está bloqueada");

        f.AssertClip("c1", 0, 50, 0);
        Assert.Single(f.Track("v1").Clips);
    }

    [Fact]
    public void Un_enlazado_bloqueado_que_no_se_corta_no_impide_el_corte()
    {
        var f = new TrimFixture()
            .Add("v1", "c1", "m1", 0, 50, 0, link: "L1")
            .Add("a1", "s1", "m3", 0, 5, 0, link: "L1")
            .Lock("a1");

        TimelineEdits.Split(f.Ctx, "c1", F(10));

        Assert.Equal(2, f.Track("v1").Clips.Count);
        f.AssertValid();
    }

    [Fact]
    public void Sin_contexto_lanza_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => TimelineEdits.Split(null!, "c1", F(1)));
    }
}
