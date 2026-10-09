using FotoToma.Core.Commands;
using FotoToma.Core.Editing;
using FotoToma.Core.Model;

namespace FotoToma.Core.Tests.Editing.Trimming;

/// <summary>
/// La limpieza de enlaces huérfanos nunca toca un clip de una pista bloqueada: ese clip conserva su
/// enlace (ya de un solo miembro) y la operación sigue adelante, tanto llamada directa como envuelta
/// en el comando que comprueba que las pistas bloqueadas no cambien.
/// </summary>
public sealed class LockedOrphanLinkTests
{
    private static TrimFixture RemoveScenario() => new TrimFixture()
        .Add("v1", "c1", "m1", 0, 50, 0, link: "L1")
        .Add("a1", "s1", "m1", 0, 50, 0, link: "L1")
        .Add("a2", "s2", "m3", 0, 10, 60, link: "L2")
        .Add("v1", "c2", "m2", 0, 10, 60, link: "L2")
        .Lock("a1");

    private static TrimFixture LinkScenario() => new TrimFixture()
        .Add("v1", "c1", "m1", 0, 50, 0, link: "L1")
        .Add("a1", "s1", "m1", 0, 50, 0, link: "L1")
        .Add("a2", "s2", "m3", 0, 10, 0)
        .Lock("a1");

    private static void AssertLockedPartnerUntouched(TrimFixture f)
    {
        Clip s1 = f.Clip("s1");
        Assert.Equal("L1", s1.LinkId);
        f.AssertClip("s1", 0, 50, 0);
        Assert.True(s1.Enabled);
        Assert.True(f.Track("a1").Locked);
        Assert.Single(f.Track("a1").Clips);
        Assert.Single(f.Sequence.GetLinkGroup("s1"));
        f.AssertValid();
    }

    [Fact]
    public void Borrar_una_pista_deja_intacta_a_la_pareja_en_pista_bloqueada()
    {
        TrimFixture f = RemoveScenario();

        TimelineEdits.RemoveTrack(f.Ctx, "v1");

        Assert.Null(f.Sequence.FindTrack("v1"));
        AssertLockedPartnerUntouched(f);
        Assert.Null(f.Clip("s2").LinkId);
    }

    [Fact]
    public void Borrar_una_pista_con_el_comando_pasa_la_comprobacion_de_bloqueo()
    {
        TrimFixture f = RemoveScenario();
        SequenceEditCommand command = Edits.RemoveTrack("seq", "v1", new SequentialIdGenerator());

        command.Apply(f.Project);

        Assert.Null(f.Sequence.FindTrack("v1"));
        AssertLockedPartnerUntouched(f);
        Assert.Null(f.Clip("s2").LinkId);

        command.Revert(f.Project);
        Assert.NotNull(f.Sequence.FindTrack("v1"));
        Assert.Equal("L1", f.Clip("c1").LinkId);
        Assert.Equal("L2", f.Clip("s2").LinkId);
    }

    [Fact]
    public void Enlazar_saca_un_clip_de_un_grupo_cuya_pareja_esta_en_pista_bloqueada()
    {
        TrimFixture f = LinkScenario();

        string link = TimelineEdits.LinkClips(f.Ctx, ["c1", "s2"]);

        Assert.Equal(link, f.Clip("c1").LinkId);
        Assert.Equal(link, f.Clip("s2").LinkId);
        AssertLockedPartnerUntouched(f);
    }

    [Fact]
    public void Enlazar_con_el_comando_pasa_la_comprobacion_de_bloqueo()
    {
        TrimFixture f = LinkScenario();
        SequenceEditCommand command = Edits.LinkClips("seq", ["c1", "s2"], new SequentialIdGenerator());

        command.Apply(f.Project);

        string link = Assert.Single(command.CreatedIds);
        Assert.Equal(link, f.Clip("c1").LinkId);
        Assert.Equal(link, f.Clip("s2").LinkId);
        AssertLockedPartnerUntouched(f);
    }

    [Fact]
    public void Un_clip_con_enlace_huerfano_se_edita_como_suelto_al_desbloquear()
    {
        TrimFixture f = LinkScenario();
        TimelineEdits.LinkClips(f.Ctx, ["c1", "s2"]);
        f.Track("a1").Locked = false;

        TimelineEdits.Trim(f.Ctx, "s1", TrimEdge.Out, TrimFixture.F(-10));

        f.AssertClip("s1", 0, 40, 0);
        f.AssertClip("c1", 0, 50, 0);
        f.AssertValid();
    }
}
