using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Editing.Trimming;

public sealed class TrackEditsTests
{
    private static string[] VideoIds(TrimFixture f) => [.. f.Sequence.VideoTracks.Select(t => t.Id)];

    private static string[] AudioIds(TrimFixture f) => [.. f.Sequence.AudioTracks.Select(t => t.Id)];

    [Theory]
    [InlineData(0, new[] { "track-1", "v1", "v2" })]
    [InlineData(1, new[] { "v1", "track-1", "v2" })]
    [InlineData(2, new[] { "v1", "v2", "track-1" })]
    public void Agrega_una_pista_de_video_en_la_posicion_pedida(int index, string[] expected)
    {
        var f = new TrimFixture();

        string id = TimelineEdits.AddTrack(f.Ctx, TrackKind.Video, index, "V3");

        Assert.Equal("track-1", id);
        Assert.Equal(expected, VideoIds(f));
        Track track = f.Track(id);
        Assert.Equal(TrackKind.Video, track.Kind);
        Assert.Equal("V3", track.Name);
        Assert.Empty(track.Clips);
        Assert.Equal(["a1", "a2"], AudioIds(f));
        f.AssertValid();
    }

    [Fact]
    public void Agrega_una_pista_de_audio_en_su_propia_lista()
    {
        var f = new TrimFixture();

        string id = TimelineEdits.AddTrack(f.Ctx, TrackKind.Audio, 2, "A3");

        Assert.Equal(["a1", "a2", id], AudioIds(f));
        Assert.Equal(TrackKind.Audio, f.Track(id).Kind);
        Assert.Equal(["v1", "v2"], VideoIds(f));
        f.AssertValid();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void Agregar_falla_fuera_de_rango(int index)
    {
        var f = new TrimFixture();

        AssertFails(() => TimelineEdits.AddTrack(f.Ctx, TrackKind.Video, index, "V3"), "entre la 0 y la 2");

        Assert.Equal(2, f.Sequence.VideoTracks.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Agregar_falla_sin_nombre(string? name)
    {
        var f = new TrimFixture();

        AssertFails(() => TimelineEdits.AddTrack(f.Ctx, TrackKind.Audio, 0, name!), "necesita un nombre");
    }

    [Fact]
    public void Agregar_falla_con_un_tipo_de_pista_desconocido()
    {
        var f = new TrimFixture();

        AssertFails(() => TimelineEdits.AddTrack(f.Ctx, (TrackKind)99, 0, "X"), "Tipo de pista desconocido");
    }

    [Fact]
    public void Agregar_sin_contexto_lanza_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => TimelineEdits.AddTrack(null!, TrackKind.Video, 0, "V3"));
    }

    [Fact]
    public void Borrar_quita_la_pista_con_sus_clips_y_suelta_a_las_parejas_solas()
    {
        var f = new TrimFixture()
            .Add("v1", "c1", "m1", 0, 50, 0, link: "L1")
            .Add("a1", "s1", "m1", 0, 50, 0, link: "L1")
            .Add("v1", "c2", "m2", 0, 10, 60, link: "L2")
            .Add("a1", "s2", "m2", 0, 10, 60, link: "L2")
            .Add("a2", "t2", "m3", 0, 10, 60, link: "L2")
            .Add("a2", "x", "m3", 0, 10, 0);

        TimelineEdits.RemoveTrack(f.Ctx, "v1");

        Assert.Equal(["v2"], VideoIds(f));
        Assert.Null(f.Sequence.FindClip("c1"));
        Assert.Null(f.Clip("s1").LinkId);
        Assert.Equal("L2", f.Clip("s2").LinkId);
        Assert.Equal("L2", f.Clip("t2").LinkId);
        Assert.Null(f.Clip("x").LinkId);
        f.AssertValid();
    }

    [Fact]
    public void Borrar_una_pista_de_audio()
    {
        var f = new TrimFixture();

        TimelineEdits.RemoveTrack(f.Ctx, "a2");

        Assert.Equal(["a1"], AudioIds(f));
        f.AssertValid();
    }

    [Fact]
    public void Borrar_falla_si_esta_bloqueada_o_no_existe()
    {
        var f = new TrimFixture().Lock("v2");

        AssertFails(() => TimelineEdits.RemoveTrack(f.Ctx, "v2"), "«V2» está bloqueada");
        AssertFails(() => TimelineEdits.RemoveTrack(f.Ctx, "nada"), "No existe la pista «nada»");
        AssertFails(() => TimelineEdits.RemoveTrack(f.Ctx, null!), "No existe la pista");
        Assert.Equal(2, f.Sequence.VideoTracks.Count);
    }

    [Fact]
    public void Borrar_sin_contexto_lanza_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => TimelineEdits.RemoveTrack(null!, "v1"));
    }

    [Fact]
    public void Mover_cambia_el_orden_dentro_de_su_tipo()
    {
        var f = new TrimFixture();
        TimelineEdits.AddTrack(f.Ctx, TrackKind.Video, 2, "V3");

        TimelineEdits.MoveTrack(f.Ctx, "v1", 2);
        Assert.Equal(["v2", "track-1", "v1"], VideoIds(f));

        TimelineEdits.MoveTrack(f.Ctx, "v1", 0);
        Assert.Equal(["v1", "v2", "track-1"], VideoIds(f));

        TimelineEdits.MoveTrack(f.Ctx, "a2", 0);
        Assert.Equal(["a2", "a1"], AudioIds(f));
        f.AssertValid();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void Mover_falla_fuera_de_rango(int index)
    {
        var f = new TrimFixture();

        AssertFails(() => TimelineEdits.MoveTrack(f.Ctx, "v1", index), "entre la 0 y la 1");

        Assert.Equal(["v1", "v2"], VideoIds(f));
    }

    [Fact]
    public void Mover_falla_si_esta_bloqueada_o_no_existe()
    {
        var f = new TrimFixture().Lock("v1");

        AssertFails(() => TimelineEdits.MoveTrack(f.Ctx, "v1", 1), "bloqueada");
        AssertFails(() => TimelineEdits.MoveTrack(f.Ctx, "nada", 0), "No existe la pista «nada»");
    }

    [Fact]
    public void Cambia_silencio_visibilidad_y_nombre()
    {
        var f = new TrimFixture();

        TimelineEdits.SetTrackState(f.Ctx, "a1", muted: true, hidden: true, name: "Voz");

        Track track = f.Track("a1");
        Assert.True(track.Muted);
        Assert.True(track.Hidden);
        Assert.Equal("Voz", track.Name);
        Assert.False(track.Locked);

        TimelineEdits.SetTrackState(f.Ctx, "a1", muted: false);
        Assert.False(track.Muted);
        Assert.True(track.Hidden);
        Assert.Equal("Voz", track.Name);
        f.AssertValid();
    }

    [Fact]
    public void Bloquear_y_desbloquear_siempre_se_puede()
    {
        var f = new TrimFixture();

        TimelineEdits.SetTrackState(f.Ctx, "v1", locked: true);
        Assert.True(f.Track("v1").Locked);

        TimelineEdits.SetTrackState(f.Ctx, "v1", locked: true);
        Assert.True(f.Track("v1").Locked);

        TimelineEdits.SetTrackState(f.Ctx, "v1", locked: false);
        Assert.False(f.Track("v1").Locked);
        f.AssertValid();
    }

    [Fact]
    public void Una_pista_bloqueada_no_se_renombra_pero_se_oculta_y_silencia()
    {
        var f = new TrimFixture().Lock("v1");

        AssertFails(() => TimelineEdits.SetTrackState(f.Ctx, "v1", name: "Otra"), "bloqueada");
        AssertFails(() => TimelineEdits.SetTrackState(f.Ctx, "v1", locked: true, name: "Otra"), "bloqueada");
        TimelineEdits.SetTrackState(f.Ctx, "v1", muted: true);
        TimelineEdits.SetTrackState(f.Ctx, "v1", hidden: true);

        Track track = f.Track("v1");
        Assert.True(track.Muted);
        Assert.True(track.Hidden);
        Assert.Equal("V1", track.Name);
        Assert.True(track.Locked);
    }

    [Fact]
    public void En_una_pista_bloqueada_repetir_los_mismos_valores_no_es_un_cambio()
    {
        var f = new TrimFixture().Lock("v1");

        TimelineEdits.SetTrackState(f.Ctx, "v1", muted: false, hidden: false, name: "V1");

        Assert.True(f.Track("v1").Locked);
        f.AssertValid();
    }

    [Fact]
    public void Desbloquear_y_cambiar_en_la_misma_llamada_se_puede()
    {
        var f = new TrimFixture().Lock("v1");

        TimelineEdits.SetTrackState(f.Ctx, "v1", locked: false, name: "Fondo", muted: true);

        Track track = f.Track("v1");
        Assert.False(track.Locked);
        Assert.True(track.Muted);
        Assert.Equal("Fondo", track.Name);
        f.AssertValid();
    }

    [Fact]
    public void Cambiar_y_bloquear_una_pista_libre_en_la_misma_llamada_se_puede()
    {
        var f = new TrimFixture();

        TimelineEdits.SetTrackState(f.Ctx, "v2", locked: true, hidden: true);

        Assert.True(f.Track("v2").Locked);
        Assert.True(f.Track("v2").Hidden);
        f.AssertValid();
    }

    [Fact]
    public void Cambiar_estado_falla_con_nombre_vacio_o_pista_desconocida()
    {
        var f = new TrimFixture();

        AssertFails(() => TimelineEdits.SetTrackState(f.Ctx, "v1", name: " "), "necesita un nombre");
        AssertFails(() => TimelineEdits.SetTrackState(f.Ctx, "nada", muted: true), "No existe la pista «nada»");
        Assert.Equal("V1", f.Track("v1").Name);
    }

    [Fact]
    public void Activar_y_desactivar_afecta_a_todo_el_grupo()
    {
        var f = new TrimFixture()
            .Add("v1", "c1", "m1", 0, 50, 0, link: "L1")
            .Add("a1", "s1", "m1", 0, 50, 0, link: "L1")
            .Add("v2", "x", "m2", 0, 10, 0);

        TimelineEdits.SetClipEnabled(f.Ctx, "s1", false);

        Assert.False(f.Clip("c1").Enabled);
        Assert.False(f.Clip("s1").Enabled);
        Assert.True(f.Clip("x").Enabled);

        TimelineEdits.SetClipEnabled(f.Ctx, "c1", true);
        Assert.True(f.Clip("c1").Enabled);
        Assert.True(f.Clip("s1").Enabled);
        f.AssertValid();
    }

    [Fact]
    public void Activar_falla_si_alguna_pista_del_grupo_esta_bloqueada_o_el_clip_no_existe()
    {
        var f = new TrimFixture()
            .Add("v1", "c1", "m1", 0, 50, 0, link: "L1")
            .Add("a1", "s1", "m1", 0, 50, 0, link: "L1")
            .Lock("a1");

        AssertFails(() => TimelineEdits.SetClipEnabled(f.Ctx, "c1", false), "«A1» está bloqueada");
        Assert.True(f.Clip("c1").Enabled);
        AssertFails(() => TimelineEdits.SetClipEnabled(f.Ctx, "nada", false), "No existe el clip «nada»");
    }

    [Fact]
    public void Enlaza_clips_de_pistas_distintas()
    {
        var f = new TrimFixture()
            .Add("v1", "c1", "m1", 0, 50, 0)
            .Add("a1", "s1", "m1", 0, 50, 0)
            .Add("a2", "t1", "m3", 0, 10, 0);

        string link = TimelineEdits.LinkClips(f.Ctx, ["c1", "s1", "t1", "c1"]);

        Assert.Equal("link-1", link);
        Assert.Equal(["c1", "s1", "t1"], f.Sequence.GetLinkGroup("c1").Select(m => m.Clip.Id));
        Assert.All(f.Sequence.GetLinkGroup("c1"), m => Assert.Equal(link, m.Clip.LinkId));
        f.AssertValid();
    }

    [Fact]
    public void Enlazar_saca_a_los_clips_de_su_enlace_anterior_y_suelta_a_las_parejas_solas()
    {
        var f = new TrimFixture()
            .Add("v1", "c1", "m1", 0, 50, 0, link: "L1")
            .Add("a1", "s1", "m1", 0, 50, 0, link: "L1")
            .Add("v2", "c2", "m2", 0, 50, 0, link: "L2")
            .Add("a2", "s2", "m2", 0, 50, 0, link: "L2")
            .Add("a1", "s3", "m2", 60, 70, 60, link: "L2");

        string link = TimelineEdits.LinkClips(f.Ctx, ["c1", "s2"]);

        Assert.Equal(link, f.Clip("c1").LinkId);
        Assert.Equal(link, f.Clip("s2").LinkId);
        Assert.Null(f.Clip("s1").LinkId);
        Assert.Equal("L2", f.Clip("c2").LinkId);
        Assert.Equal("L2", f.Clip("s3").LinkId);
        f.AssertValid();
    }

    [Fact]
    public void Enlazar_falla_con_menos_de_dos_clips_distintos()
    {
        var f = new TrimFixture().Add("v1", "c1", "m1", 0, 50, 0);

        AssertFails(() => TimelineEdits.LinkClips(f.Ctx, ["c1"]), "al menos dos clips");
        AssertFails(() => TimelineEdits.LinkClips(f.Ctx, ["c1", "c1"]), "al menos dos clips");
        AssertFails(() => TimelineEdits.LinkClips(f.Ctx, []), "al menos dos clips");
    }

    [Fact]
    public void Enlazar_falla_con_dos_clips_de_la_misma_pista()
    {
        var f = new TrimFixture()
            .Add("v1", "c1", "m1", 0, 50, 0)
            .Add("v1", "c2", "m2", 0, 10, 50);

        AssertFails(() => TimelineEdits.LinkClips(f.Ctx, ["c1", "c2"]), "misma pista «V1»");

        Assert.Null(f.Clip("c1").LinkId);
    }

    [Fact]
    public void Enlazar_falla_con_un_clip_desconocido_o_una_pista_bloqueada()
    {
        var f = new TrimFixture()
            .Add("v1", "c1", "m1", 0, 50, 0)
            .Add("a1", "s1", "m1", 0, 50, 0)
            .Lock("a1");

        AssertFails(() => TimelineEdits.LinkClips(f.Ctx, ["c1", "nada"]), "No existe el clip «nada»");
        AssertFails(() => TimelineEdits.LinkClips(f.Ctx, ["c1", "s1"]), "«A1» está bloqueada");
        Assert.Null(f.Clip("c1").LinkId);
    }

    [Fact]
    public void Enlazar_sin_argumentos_lanza_ArgumentNullException()
    {
        var f = new TrimFixture();

        Assert.Throws<ArgumentNullException>(() => TimelineEdits.LinkClips(null!, ["a", "b"]));
        Assert.Throws<ArgumentNullException>(() => TimelineEdits.LinkClips(f.Ctx, null!));
    }

    [Fact]
    public void Desenlazar_suelta_a_todo_el_grupo()
    {
        var f = new TrimFixture()
            .Add("v1", "c1", "m1", 0, 50, 0, link: "L1")
            .Add("a1", "s1", "m1", 0, 50, 0, link: "L1")
            .Add("a2", "t1", "m3", 0, 10, 0, link: "L1")
            .Add("v2", "o", "m2", 0, 10, 0, link: "L2")
            .Add("a2", "p", "m2", 20, 30, 20, link: "L2");

        TimelineEdits.UnlinkClips(f.Ctx, "s1");

        Assert.Null(f.Clip("c1").LinkId);
        Assert.Null(f.Clip("s1").LinkId);
        Assert.Null(f.Clip("t1").LinkId);
        Assert.Equal("L2", f.Clip("o").LinkId);
        f.AssertValid();
    }

    [Fact]
    public void Desenlazar_falla_si_no_esta_enlazado_bloqueado_o_no_existe()
    {
        var f = new TrimFixture()
            .Add("v1", "c1", "m1", 0, 50, 0, link: "L1")
            .Add("a1", "s1", "m1", 0, 50, 0, link: "L1")
            .Add("v2", "suelto", "m2", 0, 10, 0)
            .Lock("a1");

        AssertFails(() => TimelineEdits.UnlinkClips(f.Ctx, "suelto"), "no está enlazado");
        AssertFails(() => TimelineEdits.UnlinkClips(f.Ctx, "c1"), "«A1» está bloqueada");
        AssertFails(() => TimelineEdits.UnlinkClips(f.Ctx, "nada"), "No existe el clip «nada»");
        Assert.Equal("L1", f.Clip("c1").LinkId);
    }
}
