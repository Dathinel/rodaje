using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Editing.Organize;

/// <summary>Colores, grupos, empujones cuadro a cuadro, alto de pista, huecos y sincronía de enlazados.</summary>
public sealed class OrganizeEditsTests
{
    [Fact]
    public void El_color_va_al_clip_y_sus_enlazados_y_se_quita_con_null()
    {
        var f = new TrimFixture().Add("v1", "v", "m1", 0, 50, 0, "l").Add("a1", "a", "m1", 0, 50, 0, "l");

        TimelineEdits.SetClipColor(f.Ctx, ["v"], "#e5484d");

        Assert.Equal("#E5484D", f.Clip("v").Color);
        Assert.Equal("#E5484D", f.Clip("a").Color);
        TimelineEdits.SetClipColor(f.Ctx, ["a"], null);
        Assert.Null(f.Clip("v").Color);
        AssertFails(() => TimelineEdits.SetClipColor(f.Ctx, ["v"], "rojo"), "#RRGGBB");
    }

    [Fact]
    public void Agrupar_junta_clips_de_la_misma_pista_y_los_mueve_juntos()
    {
        var f = new TrimFixture().Add("v1", "x", "m1", 0, 10, 0).Add("v1", "y", "m1", 20, 30, 20).Add("v2", "z", "m2", 0, 10, 5);

        string group = TimelineEdits.GroupClips(f.Ctx, ["x", "y"]);
        TimelineEdits.MoveClips(f.Ctx, ["x"], F(3));

        f.AssertValid();
        Assert.Equal(group, f.Clip("y").GroupId);
        f.AssertClip("x", 0, 10, 3);
        f.AssertClip("y", 20, 30, 23);
        f.AssertClip("z", 0, 10, 5);
        Assert.Equal(2, TimelineEdits.ExpandWithGroups(f.Sequence, ["y"]).Count);
    }

    [Fact]
    public void Agrupar_funde_grupos_y_desagrupar_suelta_a_todos()
    {
        var f = new TrimFixture().Add("v1", "x", "m1", 0, 10, 0).Add("v1", "y", "m1", 20, 30, 20).Add("v2", "z", "m2", 0, 10, 40);
        TimelineEdits.GroupClips(f.Ctx, ["x", "y"]);

        string merged = TimelineEdits.GroupClips(f.Ctx, ["y", "z"]);

        Assert.All(new[] { "x", "y", "z" }, id => Assert.Equal(merged, f.Clip(id).GroupId));
        TimelineEdits.UngroupClips(f.Ctx, ["z"]);
        Assert.All(new[] { "x", "y", "z" }, id => Assert.Null(f.Clip(id).GroupId));
        AssertFails(() => TimelineEdits.UngroupClips(f.Ctx, ["x"]), "grupo");
        AssertFails(() => TimelineEdits.GroupClips(f.Ctx, ["x"]), "al menos dos");
    }

    [Fact]
    public void El_empujon_no_pisa_nada_ni_pasa_del_cero_y_respeta_el_bloqueo()
    {
        var f = new TrimFixture().Add("v1", "x", "m1", 0, 10, 0).Add("v1", "y", "m1", 20, 30, 12).Add("v2", "w", "m1", 0, 5, 30).Lock("v2");

        AssertFails(() => TimelineEdits.MoveClips(f.Ctx, ["x"], F(-1)), "antes del inicio");
        AssertFails(() => TimelineEdits.MoveClips(f.Ctx, ["x"], F(3)), "ocupado");
        AssertFails(() => TimelineEdits.MoveClips(f.Ctx, ["w"], F(1)), "bloqueada");
        TimelineEdits.MoveClips(f.Ctx, ["x"], F(2));
        f.AssertClip("x", 0, 10, 2);
    }

    [Fact]
    public void Los_empujones_seguidos_se_funden_en_un_paso()
    {
        var f = new TrimFixture().Add("v1", "x", "m1", 0, 10, 0);
        var stack = new UndoStack(f.Project);

        for (int i = 0; i < 5; i++)
        {
            stack.Execute(Edits.MoveClips("seq", ["x"], F(1)));
        }

        Assert.Equal(1, stack.Count);
        f.AssertClip("x", 0, 10, 5);
        stack.Undo();
        f.AssertClip("x", 0, 10, 0);
    }

    [Fact]
    public void El_alto_de_pista_se_acota_y_vale_en_una_pista_bloqueada()
    {
        var f = new TrimFixture().Lock("v1");

        TimelineEdits.SetTrackHeight(f.Ctx, "v1", 9999);
        Assert.Equal(Track.MaxHeight, f.Track("v1").Height);
        TimelineEdits.SetTrackHeight(f.Ctx, "v1", 3);
        Assert.Equal(Track.MinHeight, f.Track("v1").Height);
        TimelineEdits.SetTrackHeight(f.Ctx, "v1", null);
        Assert.Null(f.Track("v1").Height);
    }

    [Fact]
    public void Color_grupo_y_alto_se_guardan_y_se_leen()
    {
        var f = new TrimFixture().Add("v1", "x", "m1", 0, 10, 0).Add("v1", "y", "m1", 20, 30, 20);
        TimelineEdits.GroupClips(f.Ctx, ["x", "y"]);
        TimelineEdits.SetClipColor(f.Ctx, ["x"], "#3E9BFF");
        TimelineEdits.SetTrackHeight(f.Ctx, "v1", 96);

        Project read = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(f.Project));

        Sequence sequence = read.GetSequence("seq");
        Assert.Equal(Project.CurrentSchemaVersion, read.SchemaVersion);
        Assert.Equal("#3E9BFF", sequence.GetClip("x").Clip.Color);
        Assert.Equal(f.Clip("x").GroupId, sequence.GetClip("y").Clip.GroupId);
        Assert.Equal(96, sequence.GetTrack("v1").Height);
        Assert.Null(sequence.GetTrack("v2").Height);
    }

    [Fact]
    public void Los_huecos_se_encuentran_por_pista_y_se_cierran_todos()
    {
        var f = new TrimFixture().Add("v1", "x", "m1", 0, 10, 5).Add("v1", "y", "m1", 20, 30, 20).Add("v1", "z", "m1", 40, 50, 40);

        IReadOnlyList<TimelineGap> gaps = TimelineEdits.FindGaps(f.Sequence, ["v1"]);
        Assert.Equal([(F(0), F(5)), (F(15), F(20)), (F(30), F(40))], gaps.Select(g => (g.Start, g.End)));
        Assert.Equal(F(15), TimelineEdits.NextGap(f.Sequence, F(5), forward: true)!.Value.Start);
        Assert.Equal(F(0), TimelineEdits.NextGap(f.Sequence, F(5), forward: false)!.Value.Start);

        (int closed, int skipped) = TimelineEdits.CloseAllGaps(f.Ctx, ["v1"], RippleScope.Track);

        f.AssertValid();
        Assert.Equal((3, 0), (closed, skipped));
        f.AssertClip("x", 0, 10, 0);
        f.AssertClip("y", 20, 30, 10);
        f.AssertClip("z", 40, 50, 20);
        AssertFails(() => TimelineEdits.CloseAllGaps(f.Ctx, ["v1"], RippleScope.Track), "No hay huecos");
    }

    [Fact]
    public void Cerrar_todos_deja_los_huecos_que_romperian_la_sincronia()
    {
        // El audio enlazado de «y» correría con él, pero «w» atraviesa en a1 el tramo del primer hueco.
        var f = new TrimFixture()
            .Add("v1", "x", "m1", 0, 10, 0).Add("v1", "y", "m1", 20, 30, 20, "l").Add("a1", "ay", "m1", 20, 30, 20, "l")
            .Add("a1", "w", "m3", 0, 5, 12).Add("v1", "z", "m2", 0, 10, 40);

        (int closed, int skipped) = TimelineEdits.CloseAllGaps(f.Ctx, ["v1"], RippleScope.Track);

        f.AssertValid();
        Assert.Equal((1, 1), (closed, skipped));
        f.AssertClip("y", 20, 30, 20);
        f.AssertClip("ay", 20, 30, 20);
        f.AssertClip("z", 0, 10, 30);
        f.AssertClip("w", 0, 5, 12);
    }

    [Fact]
    public void Un_desfase_entre_enlazados_se_mide_en_cuadros_y_se_corrige()
    {
        var f = new TrimFixture().Add("v1", "v", "m1", 10, 60, 0, "l").Add("a1", "a", "m1", 10, 60, 0, "l");
        Assert.Empty(LinkSync.Find(f.Sequence));

        // Como «ignorar grupos»: el audio se corre 3 cuadros sin su video.
        f.Clip("a").Start = F(3);
        SyncOffset sync = Assert.Single(LinkSync.Find(f.Sequence));
        Assert.Equal(("a", "v"), (sync.ClipId, sync.PartnerId));
        Assert.Equal(-3, sync.Frames(Rational.Fps25));

        LinkSync.Resync(f.Ctx, "a", ResyncMode.Move);
        f.AssertClip("a", 10, 60, 0);
        Assert.Empty(LinkSync.Find(f.Sequence));
        AssertFails(() => LinkSync.Resync(f.Ctx, "a", ResyncMode.Move), "ya está sincronizado");
    }

    [Fact]
    public void Deslizar_a_su_lugar_deja_el_clip_quieto_con_el_material_sincronizado()
    {
        var f = new TrimFixture().Add("v1", "v", "m1", 10, 60, 0, "l").Add("a1", "a", "m1", 14, 64, 0, "l");

        SyncOffset sync = Assert.Single(LinkSync.Find(f.Sequence));
        Assert.Equal(4, sync.Frames(Rational.Fps25));
        LinkSync.Resync(f.Ctx, "a", ResyncMode.Slip);

        f.AssertClip("a", 10, 60, 0);
        Assert.Empty(LinkSync.Find(f.Sequence));
    }

    [Fact]
    public void Medios_distintos_no_cuentan_como_desfase()
    {
        var f = new TrimFixture().Add("v1", "v", "m1", 0, 50, 0, "l").Add("a1", "a", "m3", 3, 40, 2, "l");

        Assert.Empty(LinkSync.Find(f.Sequence));
    }
}
