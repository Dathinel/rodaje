using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Editing.Organize;

/// <summary>
/// Pegar insertando (Ctrl+Alt+V), las transiciones que viajan con lo copiado y duplicado, y los medios de lo copiado al
/// pegar en otro proyecto.
/// </summary>
public sealed class PasteInsertAndTransitionsTests
{
    [Fact]
    public void Pegar_insertando_corre_lo_que_sigue_en_vez_de_taparlo()
    {
        var f = new TrimFixture()
            .Add("v1", "x", "m1", 0, 10, 0)
            .Add("v1", "y", "m2", 0, 10, 20).Add("v1", "z", "m2", 20, 30, 40);
        ClipClipboard copied = ClipClipboard.Capture(f.Sequence, ["x"])!;

        IReadOnlyList<string> created = TimelineEdits.Paste(f.Ctx, copied, F(25), PlacementMode.Insert);

        f.AssertValid();
        string copy = Assert.Single(created);
        f.AssertClip(copy, 0, 10, 25);
        // «y» ocupaba [20, 30): se parte en el cursor y su mitad derecha se corre 10 cuadros, igual que «z».
        f.AssertClip("y", 0, 5, 20);
        Assert.Equal([F(0), F(20), F(25), F(35), F(50)], f.Track("v1").Clips.Select(c => c.Start));
        Clip right = f.Track("v1").Clips[3];
        Assert.Equal(F(5), right.SourceIn);
        Assert.Equal(F(10), right.SourceOut);
        f.AssertClip("z", 20, 30, 50);
    }

    [Fact]
    public void Pegar_insertando_abre_un_solo_espacio_en_las_pistas_de_lo_copiado_con_sus_enlazados()
    {
        var f = new TrimFixture()
            .Add("v1", "x", "m1", 0, 10, 0, "l").Add("a1", "xa", "m1", 0, 10, 0, "l")
            .Add("v1", "y", "m2", 0, 10, 30, "l2").Add("a1", "ya", "m2", 0, 10, 30, "l2")
            .Add("v2", "arriba", "m2", 0, 10, 30);
        ClipClipboard copied = ClipClipboard.Capture(f.Sequence, ["x"])!;

        IReadOnlyList<string> created = TimelineEdits.Paste(f.Ctx, copied, F(30), PlacementMode.Insert);

        f.AssertValid();
        Assert.Equal(2, created.Count);
        f.AssertClip(created[0], 0, 10, 30);
        f.AssertClip(created[1], 0, 10, 30);
        f.AssertClip("y", 0, 10, 40);
        f.AssertClip("ya", 0, 10, 40);
        // La pista de arriba no recibió nada: no se corre.
        f.AssertClip("arriba", 0, 10, 30);
    }

    [Fact]
    public void Pegar_insertando_en_una_pista_bloqueada_que_haria_falta_correr_no_cambia_nada()
    {
        var f = new TrimFixture()
            .Add("v1", "x", "m1", 0, 10, 0, "l").Add("a1", "xa", "m1", 0, 10, 0, "l")
            .Add("v1", "y", "m2", 0, 10, 20, "l2").Add("a2", "ya", "m2", 0, 10, 20, "l2")
            .Lock("a2");
        ClipClipboard copied = ClipClipboard.Capture(f.Sequence, ["x"], alone: true)!;

        AssertFails(() => TimelineEdits.Paste(f.Ctx, copied, F(20), PlacementMode.Insert), "bloqueada");

        f.AssertValid();
        Assert.Equal(["x", "y"], f.Track("v1").Clips.Select(c => c.Id));
        f.AssertClip("y", 0, 10, 20);
    }

    [Fact]
    public void Copiar_lleva_la_transicion_entre_dos_clips_copiados_y_pegar_la_pone_entre_las_copias()
    {
        var f = new TrimFixture().Add("v1", "x", "m1", 0, 10, 0).Add("v1", "y", "m2", 0, 10, 10);
        string original = TransitionEdits.AddTransition(f.Ctx, "x", BuiltInTransitions.Dip, F(4));
        f.Track("v1").FindTransition(original)!.Parameters[BuiltInTransitions.DipParameters.Red] = 1;
        ClipClipboard copied = ClipClipboard.Capture(f.Sequence, ["x", "y"])!;

        CopiedTransition item = Assert.Single(copied.Transitions);
        Assert.Equal(("x", "y"), (item.Transition.From, item.Transition.To));

        IReadOnlyList<string> created = TimelineEdits.Paste(f.Ctx, copied, F(50));

        f.AssertValid();
        Assert.Equal(2, f.Track("v1").Transitions.Count);
        TransitionInstance pasted = f.Track("v1").TransitionFrom(created[0])!;
        Assert.NotEqual(original, pasted.Id);
        Assert.Equal(created[1], pasted.To);
        Assert.Equal(BuiltInTransitions.Dip, pasted.Type);
        Assert.Equal(F(4), pasted.Duration);
        Assert.Equal(1, pasted.Parameters[BuiltInTransitions.DipParameters.Red]);
        // Lo copiado no cambia: sigue apuntando a los clips de origen.
        Assert.Equal("x", item.Transition.From);
    }

    [Fact]
    public void Copiar_no_lleva_la_transicion_con_un_clip_que_no_se_copia()
    {
        var f = new TrimFixture().Add("v1", "x", "m1", 0, 10, 0).Add("v1", "y", "m2", 0, 10, 10);
        TransitionEdits.AddTransition(f.Ctx, "x", BuiltInTransitions.Crossfade, F(4));

        ClipClipboard copied = ClipClipboard.Capture(f.Sequence, ["x"])!;

        Assert.Empty(copied.Transitions);
        IReadOnlyList<string> created = TimelineEdits.Paste(f.Ctx, copied, F(50));
        f.AssertValid();
        Assert.Single(f.Track("v1").Transitions);
        Assert.Null(f.Track("v1").TransitionFrom(created[0]));
    }

    [Fact]
    public void Duplicar_lleva_la_transicion_y_la_acota_si_las_copias_son_mas_cortas()
    {
        var f = new TrimFixture().Add("v1", "x", "m1", 0, 10, 0).Add("v1", "y", "m2", 0, 10, 10);
        TransitionEdits.AddTransition(f.Ctx, "x", BuiltInTransitions.Crossfade, F(6));

        IReadOnlyList<string> created = TimelineEdits.DuplicateClips(f.Ctx, ["x", "y"]);

        f.AssertValid();
        Assert.Equal(2, created.Count);
        TransitionInstance duplicated = f.Track("v1").TransitionFrom(created[0])!;
        Assert.Equal(created[1], duplicated.To);
        Assert.Equal(F(6), duplicated.Duration);
        Assert.Equal(2, f.Track("v1").Transitions.Count);
    }

    [Fact]
    public void Copiar_con_ignorar_grupos_tambien_lleva_la_transicion_entre_los_elegidos()
    {
        var f = new TrimFixture()
            .Add("v1", "x", "m1", 0, 10, 0, "l").Add("a1", "xa", "m1", 0, 10, 0, "l")
            .Add("v1", "y", "m2", 0, 10, 10, "l2").Add("a1", "ya", "m2", 0, 10, 10, "l2");
        TransitionEdits.AddTransition(f.Ctx, "x", BuiltInTransitions.Crossfade, F(4));
        TransitionEdits.AddTransition(f.Ctx, "xa", BuiltInTransitions.AudioPower, F(4));

        ClipClipboard copied = ClipClipboard.Capture(f.Sequence, ["x", "y"], alone: true)!;

        CopiedTransition item = Assert.Single(copied.Transitions);
        Assert.Equal(TrackKind.Video, item.Kind);
        IReadOnlyList<string> created = TimelineEdits.Paste(f.Ctx, copied, F(50));
        f.AssertValid();
        Assert.Equal(created[1], f.Track("v1").TransitionFrom(created[0])!.To);
        Assert.Single(f.Track("a1").Transitions);
    }

    [Fact]
    public void Pegar_tapando_sobre_un_corte_con_transicion_la_quita_porque_el_corte_desaparece()
    {
        var f = new TrimFixture()
            .Add("v1", "x", "m1", 0, 10, 0).Add("v1", "y", "m2", 0, 10, 10)
            .Add("v2", "s", "m2", 50, 60, 0);
        TransitionEdits.AddTransition(f.Ctx, "x", BuiltInTransitions.Crossfade, F(4));
        ClipClipboard copied = ClipClipboard.Capture(f.Sequence, ["s"])!;
        f.Sequence.VideoTracks.RemoveAt(1);

        TimelineEdits.Paste(f.Ctx, copied, F(5));

        f.AssertValid();
        Assert.Empty(f.Track("v1").Transitions);
    }

    [Fact]
    public void Copiar_guarda_los_medios_y_resolver_dice_cuales_faltan_en_otro_proyecto()
    {
        var origin = new TrimFixture().Add("v1", "x", "m1", 0, 10, 0).Add("v1", "y", "m2", 0, 10, 20);
        origin.Project.GetMedia("m1").Path = @"C:\Videos\uno.mp4";
        origin.Project.GetMedia("m2").Path = @"C:\Videos\dos.mp4";
        ClipClipboard copied = ClipClipboard.Capture(origin.Sequence, ["x", "y"], project: origin.Project)!;

        Assert.Equal(["m1", "m2"], copied.Media.Select(m => m.Id).Order());
        Assert.NotSame(origin.Project.GetMedia("m1"), copied.Media[0]);

        // Otro proyecto: «uno.mp4» está con otro identificador, «dos.mp4» no está y «m1» es un archivo distinto.
        var other = new TrimFixture();
        other.Project.Id = "other-project";
        other.Project.GetMedia("m1").Path = @"C:\Otros\otro.mp4";
        other.Project.GetMedia("m3").Path = @"c:\videos\UNO.mp4";
        other.Project.Media.RemoveAll(m => m.Id == "m2");

        MediaResolution resolution = copied.ResolveMedia(other.Project);

        Assert.False(resolution.IsComplete);
        Assert.Equal(new KeyValuePair<string, string>("m1", "m3"), Assert.Single(resolution.Renamed));
        MediaRef toImport = Assert.Single(resolution.ToImport);
        Assert.Equal(@"C:\Videos\dos.mp4", toImport.Path);
        Assert.Equal("m2", toImport.Id);
    }

    [Fact]
    public void ResolveMedia_UsesIdentityAfterRelinkingInTheSourceProject()
    {
        var f = new TrimFixture().Add("v1", "x", "m1", 0, 10, 0);
        f.Project.GetMedia("m1").Path = @"C:\Videos\old.mp4";
        ClipClipboard copied = ClipClipboard.Capture(f.Sequence, ["x"], project: f.Project)!;
        f.Project.GetMedia("m1").Path = @"C:\Moved\new.mp4";

        MediaResolution resolution = copied.ResolveMedia(f.Project);

        Assert.True(resolution.IsComplete);
        Assert.Empty(resolution.Renamed);
        IReadOnlyList<string> created = TimelineEdits.Paste(f.Ctx, copied, F(20));
        Assert.Equal("m1", f.Clip(Assert.Single(created)).MediaId);
        f.AssertValid();
        Assert.Equal(@"C:\Videos\old.mp4", Assert.Single(copied.Media).Path);
    }

    [Fact]
    public void Resolver_esta_completo_cuando_los_medios_estan_con_su_identificador_y_su_archivo()
    {
        var f = new TrimFixture().Add("v1", "x", "m1", 0, 10, 0);
        f.AddTitle("v2", "t", 0, 50, 0);
        f.Project.GetMedia("m1").Path = @"C:\Videos\uno.mp4";
        ClipClipboard copied = ClipClipboard.Capture(f.Sequence, ["x", "t"], project: f.Project)!;

        MediaResolution resolution = copied.ResolveMedia(f.Project);

        Assert.True(resolution.IsComplete);
        Assert.Empty(resolution.Renamed);
        Assert.Same(copied, copied.WithMediaRenamed(resolution.Renamed));
    }

    [Fact]
    public void Sin_proyecto_al_copiar_un_medio_que_falta_se_pide_sin_ruta()
    {
        var origin = new TrimFixture().Add("v1", "x", "m1", 0, 10, 0);
        ClipClipboard copied = ClipClipboard.Capture(origin.Sequence, ["x"])!;
        var other = new TrimFixture();
        other.Project.Media.RemoveAll(m => m.Id == "m1");

        MediaResolution resolution = copied.ResolveMedia(other.Project);

        Assert.Empty(copied.Media);
        MediaRef toImport = Assert.Single(resolution.ToImport);
        Assert.Equal("m1", toImport.Id);
        Assert.Equal("", toImport.Path);
    }

    [Fact]
    public void Renombrar_los_medios_deja_pegar_en_el_otro_proyecto_sin_tocar_lo_copiado()
    {
        var origin = new TrimFixture().Add("v1", "x", "m1", 0, 10, 0, "l").Add("a1", "xa", "m1", 0, 10, 0, "l");
        origin.Project.GetMedia("m1").Path = @"C:\Videos\uno.mp4";
        ClipClipboard copied = ClipClipboard.Capture(origin.Sequence, ["x"], project: origin.Project)!;
        var other = new TrimFixture();
        other.Project.Media.RemoveAll(m => m.Id == "m1");
        other.Project.Media.Add(new MediaRef { Id = "nuevo", Path = @"C:\Videos\uno.mp4", Duration = F(100), HasVideo = true, HasAudio = true });

        ClipClipboard renamed = copied.WithMediaRenamed(copied.ResolveMedia(other.Project).Renamed);
        IReadOnlyList<string> created = TimelineEdits.Paste(other.Ctx, renamed, F(0));

        other.AssertValid();
        Assert.All(created, id => Assert.Equal("nuevo", other.Clip(id).MediaId));
        Assert.All(copied.Items, i => Assert.Equal("m1", i.Clip.MediaId));
        Assert.Equal("nuevo", Assert.Single(renamed.Media).Id);
        Assert.Equal(copied.Transitions, renamed.Transitions);
    }

    [Fact]
    public void El_comando_de_pegar_insertando_es_un_paso_que_se_deshace_entero()
    {
        var f = new TrimFixture().Add("v1", "x", "m1", 0, 10, 0).Add("v1", "y", "m2", 0, 10, 10);
        TransitionEdits.AddTransition(f.Ctx, "x", BuiltInTransitions.Crossfade, F(4));
        var stack = new UndoStack(f.Project);
        ClipClipboard copied = ClipClipboard.Capture(f.Sequence, ["x", "y"])!;

        var z = new TrimFixture().Add("v1", "z", "m2", 50, 60, 20);
        f.Track("v1").Clips.Add(z.Clip("z"));
        SequenceEditCommand paste = Edits.PasteClips("seq", copied, F(20), mode: PlacementMode.Insert);
        stack.Execute(paste);

        Assert.Equal("Pegar insertando", paste.Description);
        Assert.Equal(2, paste.CreatedIds.Count);
        Assert.Equal(["x", "y", paste.CreatedIds[0], paste.CreatedIds[1], "z"], f.Track("v1").Clips.Select(c => c.Id));
        Assert.Equal([F(0), F(10), F(20), F(30), F(40)], f.Track("v1").Clips.Select(c => c.Start));
        Assert.Equal(2, f.Track("v1").Transitions.Count);
        f.AssertValid();

        stack.Undo();
        Assert.Equal(["x", "y", "z"], f.Track("v1").Clips.Select(c => c.Id));
        Assert.Equal(F(20), f.Clip("z").Start);
        Assert.Single(f.Track("v1").Transitions);
        Assert.Throws<ArgumentOutOfRangeException>(() => Edits.PasteClips("seq", copied, F(5), mode: PlacementMode.Strict));
    }
}
