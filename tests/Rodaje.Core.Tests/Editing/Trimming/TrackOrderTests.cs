using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Editing.Trimming;

public sealed class TrackOrderTests
{
    private static string[] VideoIds(TrimFixture f) => [.. f.Sequence.VideoTracks.Select(t => t.Id)];

    private static string[] AudioIds(TrimFixture f) => [.. f.Sequence.AudioTracks.Select(t => t.Id)];

    private static TrimFixture ThreeVideoTracks()
    {
        var f = new TrimFixture();
        f.Sequence.VideoTracks.Add(new Track { Id = "v3", Kind = TrackKind.Video, Name = "V3" });
        return f;
    }

    /// <summary>v1 (principal) con c1 y c2 enlazados a s1 y s2 en a1, uno detrás del otro.</summary>
    private static TrimFixture MainWithSound() => new TrimFixture()
        .Add("v1", "c1", "m1", 0, 50, 0, link: "L1")
        .Add("a1", "s1", "m1", 0, 50, 0, link: "L1")
        .Add("v1", "c2", "m2", 0, 50, 50, link: "L2")
        .Add("a1", "s2", "m2", 0, 50, 50, link: "L2");

    [Fact]
    public void Eliminar_una_pista_con_clips_se_deshace_y_rehace_en_un_paso()
    {
        TrimFixture f = new TrimFixture()
            .Add("v2", "c1", "m1", 0, 50, 0, link: "L1")
            .Add("a1", "s1", "m1", 0, 50, 0, link: "L1")
            .Add("v2", "c2", "m2", 0, 20, 60);
        SequenceEditCommand command = Edits.DeleteTrack("seq", "v2", new SequentialIdGenerator());

        command.Apply(f.Project);
        Assert.Equal(["v1"], VideoIds(f));
        Assert.Null(f.Sequence.FindClip("c1"));
        Assert.Null(f.Clip("s1").LinkId);
        f.AssertValid();

        command.Revert(f.Project);
        Assert.Equal(["v1", "v2"], VideoIds(f));
        Assert.Equal(["c1", "c2"], f.Track("v2").Clips.Select(c => c.Id));
        Assert.Equal(["c1", "s1"], f.Sequence.GetLinkGroup("c1").Select(g => g.Clip.Id));
        f.AssertValid();

        command.Apply(f.Project);
        Assert.Equal(["v1"], VideoIds(f));
        Assert.Null(f.Clip("s1").LinkId);
    }

    [Fact]
    public void La_pista_principal_no_se_elimina()
    {
        TrimFixture f = new TrimFixture().Add("v1", "c1", "m1", 0, 50, 0);

        AssertFails(() => TimelineEdits.DeleteTrack(f.Ctx, "v1"), "La pista principal «V1»");

        Assert.Equal(["v1", "v2"], VideoIds(f));
        Assert.Single(f.Track("v1").Clips);
    }

    [Fact]
    public void Eliminar_una_pista_de_audio_suelta_los_compresores_que_la_escuchaban()
    {
        var f = new TrimFixture();
        var compressor = new EffectInstance
        {
            Id = "comp",
            Type = BuiltInEffects.Compressor,
            Options = new() { [BuiltInEffects.CompressorOptions.Sidechain] = "a2" },
        };
        f.Track("a1").Effects.Add(compressor);

        TimelineEdits.DeleteTrack(f.Ctx, "a2");

        Assert.Equal(["a1"], AudioIds(f));
        Assert.False(compressor.Options.ContainsKey(BuiltInEffects.CompressorOptions.Sidechain));
        f.AssertValid();
    }

    [Fact]
    public void Eliminar_una_pista_bloqueada_falla()
    {
        TrimFixture f = new TrimFixture().Lock("a2");

        AssertFails(() => TimelineEdits.DeleteTrack(f.Ctx, "a2"), "bloqueada");
    }

    [Fact]
    public void Reordenar_una_pista_de_video_se_deshace_y_rehace()
    {
        TrimFixture f = ThreeVideoTracks().Add("v3", "c1", "m1", 0, 50, 0, link: "L1").Add("a1", "s1", "m1", 0, 50, 0, link: "L1");
        SequenceEditCommand command = Edits.ReorderTrack("seq", "v3", 1, new SequentialIdGenerator());

        command.Apply(f.Project);
        Assert.Equal(["v1", "v3", "v2"], VideoIds(f));
        Assert.Equal(["c1", "s1"], f.Sequence.GetLinkGroup("c1").Select(g => g.Clip.Id));
        f.AssertValid();

        command.Revert(f.Project);
        Assert.Equal(["v1", "v2", "v3"], VideoIds(f));

        command.Apply(f.Project);
        Assert.Equal(["v1", "v3", "v2"], VideoIds(f));
    }

    [Fact]
    public void La_principal_no_se_mueve_ni_otra_ocupa_su_lugar()
    {
        TrimFixture f = ThreeVideoTracks();

        AssertFails(() => TimelineEdits.ReorderTrack(f.Ctx, "v1", 2), "La pista principal «V1» no se mueve");
        AssertFails(() => TimelineEdits.ReorderTrack(f.Ctx, "v3", 0), "debajo de la principal");
        AssertFails(() => TimelineEdits.StepTrack(f.Ctx, "v2", up: false), "lo más abajo");

        Assert.Equal(["v1", "v2", "v3"], VideoIds(f));
    }

    [Fact]
    public void Subir_y_bajar_siguen_el_orden_de_la_linea_de_tiempo()
    {
        TrimFixture f = ThreeVideoTracks();

        TimelineEdits.StepTrack(f.Ctx, "v2", up: true);
        Assert.Equal(["v1", "v3", "v2"], VideoIds(f));
        TimelineEdits.StepTrack(f.Ctx, "v2", up: false);
        Assert.Equal(["v1", "v2", "v3"], VideoIds(f));

        // En audio, subir es acercarse al video: la primera de la lista es la de arriba.
        TimelineEdits.StepTrack(f.Ctx, "a2", up: true);
        Assert.Equal(["a2", "a1"], AudioIds(f));
        AssertFails(() => TimelineEdits.StepTrack(f.Ctx, "a2", up: true), "lo más arriba");
        f.AssertValid();
    }

    [Theory]
    [InlineData("v1", true, false)]
    [InlineData("v1", false, false)]
    [InlineData("v2", true, true)]
    [InlineData("v2", false, false)]
    [InlineData("v3", true, false)]
    [InlineData("v3", false, true)]
    [InlineData("a1", true, false)]
    [InlineData("a1", false, true)]
    [InlineData("a2", true, true)]
    [InlineData("a2", false, false)]
    public void Se_sabe_si_una_pista_puede_subir_o_bajar(string trackId, bool up, bool expected)
    {
        TrimFixture f = ThreeVideoTracks();

        Assert.Equal(expected, TimelineEdits.CanStepTrack(f.Sequence, trackId, up));
    }

    [Fact]
    public void Una_pista_bloqueada_no_se_reordena()
    {
        TrimFixture f = ThreeVideoTracks().Lock("v3");

        AssertFails(() => TimelineEdits.StepTrack(f.Ctx, "v3", up: false), "bloqueada");
    }

    [Fact]
    public void El_sonido_va_a_la_primera_pista_de_audio_libre_en_el_tramo()
    {
        TrimFixture f = new TrimFixture().Add("a1", "x", "m3", 0, 50, 0).Add("a2", "y", "m3", 0, 10, 60);

        Assert.Equal("a2", TimelineEdits.FreeAudioTrack(f.Sequence, F(10), F(40))?.Id);
        Assert.Equal("a1", TimelineEdits.FreeAudioTrack(f.Sequence, F(50), F(60))?.Id);
        Assert.Null(TimelineEdits.FreeAudioTrack(f.Sequence, F(40), F(65)));

        f.Lock("a2");
        Assert.Null(TimelineEdits.FreeAudioTrack(f.Sequence, F(10), F(40)));
    }

    [Fact]
    public void Sin_pista_de_audio_libre_se_crea_una_al_final_del_audio()
    {
        TrimFixture f = new TrimFixture().Add("a1", "x", "m3", 0, 50, 0).Add("a2", "y", "m3", 0, 50, 0);

        Track created = TimelineEdits.LinkedAudioTrack(f.Ctx, F(10), F(40));

        Assert.Equal(["a1", "a2", created.Id], AudioIds(f));
        Assert.Equal("Audio 3", created.Name);
        Assert.Equal(TrackKind.Audio, created.Kind);
        f.AssertValid();
    }

    [Fact]
    public void Un_video_soltado_encima_lleva_su_sonido_a_una_pista_nueva_si_no_cabe()
    {
        TrimFixture f = new TrimFixture()
            .Add("v1", "c1", "m1", 0, 50, 0)
            .Add("a1", "x", "m3", 0, 50, 0)
            .Add("a2", "y", "m3", 0, 50, 0);
        SequenceEditCommand command = Edits.InsertMediaWithLinkedAudio("seq", "m2", F(0), F(20), "v2", F(10), PlacementMode.Overwrite, new SequentialIdGenerator());

        command.Apply(f.Project);
        Assert.Equal(3, f.Sequence.AudioTracks.Count);
        (Track video, Clip clip) = f.Sequence.GetClip(command.CreatedIds[0]);
        (Track audio, Clip sound) = f.Sequence.GetClip(command.CreatedIds[1]);
        Assert.Equal("v2", video.Id);
        Assert.Same(f.Sequence.AudioTracks[2], audio);
        Assert.Equal(clip.LinkId, sound.LinkId);
        Assert.Equal(F(10), sound.Start);
        f.AssertValid();

        command.Revert(f.Project);
        Assert.Equal(["a1", "a2"], AudioIds(f));
        Assert.Empty(f.Track("v2").Clips);
    }

    [Fact]
    public void Mover_un_video_a_una_pista_superior_lleva_su_sonido_a_la_primera_libre()
    {
        TrimFixture f = MainWithSound();

        TimelineEdits.Move(f.Ctx, "c2", F(0), "v2", PlacementMode.Strict, relocateLinkedAudio: true);

        f.AssertClip("c2", 0, 50, 0);
        Assert.Equal("v2", f.Sequence.GetClip("c2").Track.Id);
        Assert.Equal("a2", f.Sequence.GetClip("s2").Track.Id);
        f.AssertClip("s2", 0, 50, 0);
        Assert.Equal(["c2", "s2"], f.Sequence.GetLinkGroup("c2").Select(g => g.Clip.Id));
        Assert.Equal(["s1"], f.Track("a1").Clips.Select(c => c.Id));
        f.AssertValid();
    }

    [Fact]
    public void Sin_reubicar_el_sonido_el_mismo_movimiento_choca()
    {
        TrimFixture f = MainWithSound();

        Assert.Throws<EditException>(() => TimelineEdits.Move(f.Ctx, "c2", F(0), "v2", PlacementMode.Strict));
    }

    [Fact]
    public void Mover_encima_sin_audio_libre_crea_pista_y_se_deshace_en_un_paso()
    {
        TrimFixture f = MainWithSound().Add("a2", "musica", "m3", 0, 50, 0);
        SequenceEditCommand command = Edits.Move("seq", "c2", F(0), "v2", PlacementMode.Strict, new SequentialIdGenerator(), relocateLinkedAudio: true);

        command.Apply(f.Project);
        Assert.Equal(3, f.Sequence.AudioTracks.Count);
        Assert.Same(f.Sequence.AudioTracks[2], f.Sequence.GetClip("s2").Track);
        f.AssertValid();

        command.Revert(f.Project);
        Assert.Equal(["a1", "a2"], AudioIds(f));
        Assert.Equal("v1", f.Sequence.GetClip("c2").Track.Id);
        Assert.Equal("a1", f.Sequence.GetClip("s2").Track.Id);
        f.AssertClip("s2", 0, 50, 50);

        command.Apply(f.Project);
        Assert.Equal(3, f.Sequence.AudioTracks.Count);
        Assert.Equal("v2", f.Sequence.GetClip("c2").Track.Id);
    }

    [Fact]
    public void Si_el_sonido_cabe_en_su_pista_no_se_mueve_de_ella()
    {
        TrimFixture f = MainWithSound();

        TimelineEdits.Move(f.Ctx, "c2", F(50), "v2", PlacementMode.Strict, relocateLinkedAudio: true);

        Assert.Equal("v2", f.Sequence.GetClip("c2").Track.Id);
        Assert.Equal("a1", f.Sequence.GetClip("s2").Track.Id);
        Assert.Equal(2, f.Sequence.AudioTracks.Count);
    }
}
