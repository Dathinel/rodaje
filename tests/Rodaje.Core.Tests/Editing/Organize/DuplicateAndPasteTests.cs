using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Editing.Organize;

/// <summary>Duplicar clip (Ctrl+D) y Pegar en el cursor (Ctrl+Mayús+V), cada uno un paso del historial.</summary>
public sealed class DuplicateAndPasteTests
{
    [Fact]
    public void Duplicar_pone_la_copia_a_continuacion_con_el_mismo_tramo_y_otro_id()
    {
        var f = new TrimFixture().Add("v1", "x", "m1", 10, 30, 0);

        IReadOnlyList<string> created = TimelineEdits.DuplicateClips(f.Ctx, ["x"]);

        f.AssertValid();
        string copy = Assert.Single(created);
        Assert.NotEqual("x", copy);
        f.AssertClip("x", 10, 30, 0);
        f.AssertClip(copy, 10, 30, 20);
        Assert.Equal(["x", copy], f.Track("v1").Clips.Select(c => c.Id));
    }

    [Fact]
    public void Duplicar_lleva_el_audio_enlazado_con_un_enlace_nuevo_compartido()
    {
        var f = new TrimFixture()
            .Add("v1", "x", "m1", 0, 10, 0, "l").Add("a1", "xa", "m1", 0, 10, 0, "l");

        IReadOnlyList<string> created = TimelineEdits.DuplicateClips(f.Ctx, ["x"]);

        f.AssertValid();
        Assert.Equal(2, created.Count);
        Clip video = f.Clip(created[0]);
        Clip audio = f.Clip(created[1]);
        Assert.Equal(F(10), video.Start);
        Assert.Equal(F(10), audio.Start);
        Assert.Equal("v1", f.Sequence.GetClip(video.Id).Track.Id);
        Assert.Equal("a1", f.Sequence.GetClip(audio.Id).Track.Id);
        Assert.NotNull(video.LinkId);
        Assert.Equal(video.LinkId, audio.LinkId);
        Assert.NotEqual("l", video.LinkId);
        Assert.Equal("l", f.Clip("x").LinkId);
    }

    [Fact]
    public void Duplicar_varios_conserva_su_distancia_y_empieza_tras_el_ultimo()
    {
        var f = new TrimFixture().Add("v1", "x", "m1", 0, 10, 0).Add("v1", "y", "m2", 0, 10, 20);

        IReadOnlyList<string> created = TimelineEdits.DuplicateClips(f.Ctx, ["y", "x"]);

        f.AssertValid();
        Assert.Equal(2, created.Count);
        f.AssertClip(created[0], 0, 10, 30);
        f.AssertClip(created[1], 0, 10, 50);
        Assert.Equal("m2", f.Clip(created[1]).MediaId);
    }

    [Fact]
    public void Duplicar_sin_sitio_no_cambia_nada()
    {
        var f = new TrimFixture().Add("v1", "x", "m1", 0, 10, 0).Add("v1", "z", "m2", 0, 10, 15);

        AssertFails(() => TimelineEdits.DuplicateClips(f.Ctx, ["x"]), "ocupado");

        f.AssertValid();
        Assert.Equal(["x", "z"], f.Track("v1").Clips.Select(c => c.Id));
    }

    [Fact]
    public void Duplicar_solos_no_lleva_a_los_enlazados()
    {
        var f = new TrimFixture()
            .Add("v1", "x", "m1", 0, 10, 0, "l").Add("a1", "xa", "m1", 0, 10, 0, "l");

        IReadOnlyList<string> created = TimelineEdits.DuplicateClips(f.Ctx, ["x"], alone: true);

        f.AssertValid();
        string copy = Assert.Single(created);
        Assert.Null(f.Clip(copy).LinkId);
        Assert.Single(f.Track("a1").Clips);
    }

    [Fact]
    public void Duplicar_un_compuesto_se_rechaza()
    {
        var f = new TrimFixture().Add("v1", "x", "m1", 0, 10, 0);
        f.Clip("x").SequenceId = "interior";

        AssertFails(() => TimelineEdits.DuplicateClips(f.Ctx, ["x"]), "compuesto");
    }

    [Fact]
    public void Pegar_en_el_cursor_tapa_lo_que_habia_y_mantiene_pista_y_distancias()
    {
        var f = new TrimFixture()
            .Add("v1", "x", "m1", 0, 10, 0).Add("a2", "s", "m3", 0, 5, 3)
            .Add("v1", "y", "m2", 0, 10, 42);
        ClipClipboard copied = ClipClipboard.Capture(f.Sequence, ["x", "s"])!;

        IReadOnlyList<string> created = TimelineEdits.Paste(f.Ctx, copied, F(35));

        f.AssertValid();
        Assert.Equal(2, created.Count);
        f.AssertClip(created[0], 0, 10, 35);
        f.AssertClip(created[1], 0, 5, 38);
        Assert.Equal("v1", f.Sequence.GetClip(created[0]).Track.Id);
        Assert.Equal("a2", f.Sequence.GetClip(created[1]).Track.Id);
        // «y» ocupaba [42, 52): lo tapado por la copia [35, 45) desaparece y queda su resto, con el origen corrido.
        f.AssertClip("y", 3, 10, 45);
        f.AssertClip("x", 0, 10, 0);
    }

    [Fact]
    public void Pegar_dos_clips_que_caen_en_la_misma_pista_se_rechaza_sin_perder_ninguno()
    {
        var f = new TrimFixture().Add("v1", "x", "m1", 0, 10, 0).Add("v2", "y", "m2", 0, 10, 5);
        ClipClipboard copied = ClipClipboard.Capture(f.Sequence, ["x", "y"])!;
        f.Sequence.VideoTracks.RemoveAt(1);

        AssertFails(() => TimelineEdits.Paste(f.Ctx, copied, F(50)), "misma pista");

        f.AssertValid();
        Assert.Equal(["x"], f.Track("v1").Clips.Select(c => c.Id));
    }

    [Fact]
    public void Pegar_renueva_enlaces_grupos_y_efectos_sin_tocar_lo_copiado()
    {
        var f = new TrimFixture()
            .Add("v1", "x", "m1", 0, 10, 0, "l").Add("a1", "xa", "m1", 0, 10, 0, "l");
        f.Clip("x").GroupId = "g";
        f.Clip("xa").GroupId = "g";
        f.Clip("x").Effects.Add(new EffectInstance { Id = "fx-viejo", Type = BuiltInEffects.Transform });
        ClipClipboard copied = ClipClipboard.Capture(f.Sequence, ["x"])!;

        IReadOnlyList<string> created = TimelineEdits.Paste(f.Ctx, copied, F(50));

        f.AssertValid();
        Clip video = f.Clip(created[0]);
        Clip audio = f.Clip(created[1]);
        Assert.Equal(video.LinkId, audio.LinkId);
        Assert.NotEqual("l", video.LinkId);
        Assert.Equal(video.GroupId, audio.GroupId);
        Assert.NotEqual("g", video.GroupId);
        Assert.NotEqual("fx-viejo", Assert.Single(video.Effects).Id);
        Assert.Equal("fx-viejo", Assert.Single(copied.Items[0].Clip.Effects).Id);
        Assert.Equal("fx-viejo", Assert.Single(f.Clip("x").Effects).Id);
    }

    [Fact]
    public void Pegar_en_un_proyecto_con_menos_pistas_usa_la_ultima_de_su_tipo()
    {
        var origin = new TrimFixture().Add("v2", "x", "m1", 0, 10, 0);
        ClipClipboard copied = ClipClipboard.Capture(origin.Sequence, ["x"])!;
        var f = new TrimFixture();
        f.Sequence.VideoTracks.RemoveAt(1);

        IReadOnlyList<string> created = TimelineEdits.Paste(f.Ctx, copied, F(0));

        f.AssertValid();
        Assert.Equal("v1", f.Sequence.GetClip(created[0]).Track.Id);
    }

    [Fact]
    public void Pegar_sin_el_medio_o_en_pista_bloqueada_se_rechaza()
    {
        var origin = new TrimFixture().Add("v1", "x", "m1", 0, 10, 0);
        ClipClipboard copied = ClipClipboard.Capture(origin.Sequence, ["x"])!;

        var sinMedio = new TrimFixture();
        sinMedio.Project.Media.RemoveAll(m => m.Id == "m1");
        AssertFails(() => TimelineEdits.Paste(sinMedio.Ctx, copied, F(0)), "medio");

        var bloqueada = new TrimFixture().Lock("v1");
        AssertFails(() => TimelineEdits.Paste(bloqueada.Ctx, copied, F(0)), "bloqueada");
        Assert.Empty(bloqueada.Track("v1").Clips);
    }

    [Fact]
    public void Capture_devuelve_null_sin_seleccion_y_ordena_por_tiempo()
    {
        var f = new TrimFixture().Add("v1", "x", "m1", 0, 10, 20).Add("v1", "y", "m2", 0, 10, 0);

        Assert.Null(ClipClipboard.Capture(f.Sequence, []));
        Assert.Null(ClipClipboard.Capture(f.Sequence, ["no-existe"]));
        ClipClipboard copied = ClipClipboard.Capture(f.Sequence, ["x", "y"])!;

        Assert.Equal(["y", "x"], copied.Items.Select(i => i.Clip.Id));
        Assert.Equal(F(0), copied.Origin);
        Assert.Equal(F(30), copied.Length);
    }

    [Fact]
    public void Los_comandos_son_un_paso_que_se_deshace_entero()
    {
        var f = new TrimFixture()
            .Add("v1", "x", "m1", 0, 10, 0, "l").Add("a1", "xa", "m1", 0, 10, 0, "l");
        var stack = new UndoStack(f.Project);

        SequenceEditCommand duplicate = Edits.DuplicateClips("seq", ["x"]);
        stack.Execute(duplicate);
        Assert.Equal("Duplicar clip", duplicate.Description);
        Assert.Equal(2, duplicate.CreatedIds.Count);
        Assert.Equal(2, f.Track("v1").Clips.Count);

        ClipClipboard copied = ClipClipboard.Capture(f.Sequence, ["x"])!;
        SequenceEditCommand paste = Edits.PasteClips("seq", copied, F(40));
        stack.Execute(paste);
        Assert.Equal("Pegar en el cursor", paste.Description);
        Assert.Equal(3, f.Track("v1").Clips.Count);
        Assert.Equal(3, f.Track("a1").Clips.Count);

        stack.Undo();
        Assert.Equal(2, f.Track("v1").Clips.Count);
        stack.Undo();
        Assert.Equal(["x"], f.Track("v1").Clips.Select(c => c.Id));
        Assert.Equal(["xa"], f.Track("a1").Clips.Select(c => c.Id));
        f.AssertValid();
    }
}
