using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Caza;

/// <summary>
/// Lo que promete «Audio y video enlazados» de <c>docs/guia/edicion.md</c>. Cada prueba cita la frase. El interruptor
/// «ignorar grupos» es de la app (edita el clip solo); aquí se imita corriendo el clip a mano.
/// </summary>
public sealed class LinkedAudioVideoContractTests
{
    /// <summary>Video «v» en v1 y su audio «a» en a1, enlazados, del cuadro 0 al 50 de m1, empezando en el cuadro 20.</summary>
    private static TrimFixture Pair() => new TrimFixture()
        .Add("v1", "v", "m1", 0, 50, 20, link: "L")
        .Add("a1", "a", "m1", 0, 50, 20, link: "L");

    private static EffectInstance Effect(string id, string type) => new() { Id = id, Type = type };

    // «Un video con sonido entra como dos clips enlazados»
    [Fact]
    public void Media_with_sound_enters_as_two_linked_clips()
    {
        var f = new TrimFixture();

        IReadOnlyList<string> ids = TimelineEdits.InsertMedia(f.Ctx, "m1", F(0), F(50), "v1", F(0), PlacementMode.Strict, "a1");

        f.AssertValid();
        Assert.Equal(2, ids.Count);
        Assert.NotNull(f.Clip(ids[0]).LinkId);
        Assert.Equal(f.Clip(ids[0]).LinkId, f.Clip(ids[1]).LinkId);
        Assert.Equal(TrackKind.Audio, f.Sequence.GetClip(ids[1]).Track.Kind);
    }

    // «dos clips enlazados que se mueven [...] juntos»
    [Fact]
    public void Linked_clips_move_together()
    {
        TrimFixture f = Pair();

        TimelineEdits.Move(f.Ctx, "v", F(30), null, PlacementMode.Strict);

        f.AssertValid();
        f.AssertClip("v", 0, 50, 30);
        f.AssertClip("a", 0, 50, 30);
    }

    // «dos clips enlazados que se [...] cortan [...] juntos»
    [Fact]
    public void Linked_clips_split_together()
    {
        TrimFixture f = Pair();

        TimelineEdits.Split(f.Ctx, "v", F(40));

        f.AssertValid();
        Assert.Equal(2, f.Track("v1").Clips.Count);
        Assert.Equal(2, f.Track("a1").Clips.Count);
        Assert.Equal(F(40), f.Track("a1").Clips[1].Start);
        Assert.Equal(f.Track("v1").Clips[1].LinkId, f.Track("a1").Clips[1].LinkId);
        Assert.Empty(LinkSync.Find(f.Sequence));
    }

    // «dos clips enlazados que se [...] recortan juntos»
    [Fact]
    public void Linked_clips_trim_together()
    {
        TrimFixture f = Pair();

        TimelineEdits.Trim(f.Ctx, "a", TrimEdge.In, F(5));

        f.AssertValid();
        f.AssertClip("v", 5, 50, 25);
        f.AssertClip("a", 5, 50, 25);
    }

    // «En el menú del clip: Desenlazar / Enlazar»
    [Fact]
    public void Unlink_frees_the_clips_and_link_joins_them_again()
    {
        TrimFixture f = Pair();

        TimelineEdits.UnlinkClips(f.Ctx, "v");
        TimelineEdits.Move(f.Ctx, "v", F(60), null, PlacementMode.Strict);

        f.AssertClip("a", 0, 50, 20);
        Assert.Null(f.Clip("v").LinkId);
        Assert.Null(f.Clip("a").LinkId);

        TimelineEdits.LinkClips(f.Ctx, ["v", "a"]);
        TimelineEdits.Move(f.Ctx, "v", F(70), null, PlacementMode.Strict);

        f.AssertValid();
        f.AssertClip("a", 0, 50, 30);
    }

    // «Separar audio»
    [Fact]
    public void Detach_audio_creates_its_linked_audio_clip()
    {
        var f = new TrimFixture().Add("v1", "v", "m1", 0, 50, 20);

        string audio = TimelineEdits.DetachAudio(f.Ctx, "v");

        f.AssertValid();
        Assert.Equal(TrackKind.Audio, f.Sequence.GetClip(audio).Track.Kind);
        f.AssertClip(audio, 0, 50, 20);
        Assert.NotNull(f.Clip("v").LinkId);
        Assert.Equal(f.Clip("v").LinkId, f.Clip(audio).LinkId);
    }

    // «Copiar atributos y Pegar atributos (todo, efectos, transformación, velocidad o volumen)»
    [Fact]
    public void Paste_attributes_offers_all_effects_transform_speed_or_volume()
    {
        Assert.Equal(
            ClipAttributeKinds.Effects | ClipAttributeKinds.Transform | ClipAttributeKinds.Speed | ClipAttributeKinds.Volume
            | ClipAttributeKinds.Text | ClipAttributeKinds.Shadow,
            ClipAttributeKinds.All);

        TrimFixture f = Pair().Add("v1", "tv", "m2", 0, 50, 100, link: "T").Add("a1", "ta", "m2", 0, 50, 100, link: "T");
        f.Clip("v").Effects.Add(Effect("fx-c", BuiltInEffects.Color));
        f.Clip("v").Effects.Add(Effect("fx-t", BuiltInEffects.Transform));
        f.Clip("a").Effects.Add(Effect("fx-a", BuiltInEffects.Audio));
        TimelineEdits.SetSpeed(f.Ctx, "v", new Rational(2, 1));
        ClipAttributes copied = ClipAttributes.Capture(f.Sequence, "v");
        Assert.Equal(ClipAttributeKinds.Effects | ClipAttributeKinds.Transform | ClipAttributeKinds.Speed | ClipAttributeKinds.Volume, copied.Available);

        ClipAttributeEdits.Paste(f.Ctx, ["tv"], copied, ClipAttributeKinds.Effects);
        Assert.Equal([BuiltInEffects.Color], f.Clip("tv").Effects.Select(e => e.Type));

        ClipAttributeEdits.Paste(f.Ctx, ["tv"], copied, ClipAttributeKinds.Transform);
        Assert.Equal([BuiltInEffects.Color, BuiltInEffects.Transform], f.Clip("tv").Effects.Select(e => e.Type));

        ClipAttributeEdits.Paste(f.Ctx, ["tv"], copied, ClipAttributeKinds.Volume);
        Assert.Equal([BuiltInEffects.Audio], f.Clip("ta").Effects.Select(e => e.Type));

        ClipAttributeEdits.Paste(f.Ctx, ["tv"], copied, ClipAttributeKinds.Speed);
        f.AssertValid();
        Assert.Equal(new Rational(2, 1), f.Clip("tv").Speed!.Rate);
        Assert.Equal(new Rational(2, 1), f.Clip("ta").Speed!.Rate);
    }

    // «Si un clip enlazado queda corrido respecto de su pareja del mismo medio (por moverlo con ignorar grupos [...]),
    // muestra a la derecha un aviso rojo con los cuadros: «+3», «−12».»
    [Fact]
    public void A_clip_moved_alone_reports_its_signed_offset_in_frames()
    {
        TrimFixture f = Pair();

        f.Clip("a").Start = F(17);
        SyncOffset ahead = Assert.Single(LinkSync.Find(f.Sequence));
        Assert.Equal("a", ahead.ClipId);
        Assert.Equal("v", ahead.PartnerId);
        Assert.Equal(3, ahead.Frames(Rational.Fps25));

        f.Clip("a").Start = F(32);
        Assert.Equal(-12, Assert.Single(LinkSync.Find(f.Sequence)).Frames(Rational.Fps25));
    }

    // «o porque su pista estaba bloqueada al correr las demás»
    [Fact]
    public void Rippling_all_tracks_past_a_locked_track_leaves_the_pair_offset()
    {
        TrimFixture f = Pair().Add("v1", "x", "m2", 0, 10, 0).Lock("a1");

        TimelineEdits.RippleDelete(f.Ctx, ["x"], RippleScope.AllTracks);

        f.AssertClip("v", 0, 50, 10);
        f.AssertClip("a", 0, 50, 20);
        SyncOffset offset = Assert.Single(LinkSync.Find(f.Sequence));
        Assert.Equal("a", offset.ClipId);
        Assert.Equal(-10, offset.Frames(Rational.Fps25));
    }

    // «Volver a sincronizar › «Mover a su lugar»»
    [Fact]
    public void Resync_move_puts_the_clip_back_in_place()
    {
        TrimFixture f = Pair();
        f.Clip("a").Start = F(17);

        LinkSync.Resync(f.Ctx, "a", ResyncMode.Move);

        f.AssertValid();
        f.AssertClip("a", 0, 50, 20);
        Assert.Empty(LinkSync.Find(f.Sequence));
    }

    // «Volver a sincronizar › [...] «Deslizar su contenido»»
    [Fact]
    public void Resync_slip_keeps_the_clip_in_place_with_its_content_in_sync()
    {
        var f = new TrimFixture()
            .Add("v1", "v", "m1", 10, 60, 20, link: "L")
            .Add("a1", "a", "m1", 10, 60, 23, link: "L");

        LinkSync.Resync(f.Ctx, "a", ResyncMode.Slip);

        f.AssertValid();
        f.AssertClip("a", 13, 63, 23);
        Assert.Empty(LinkSync.Find(f.Sequence));
    }

    // Regresión: invertir, recortar el inicio y quitar la reversa deja una velocidad a 1× guardada (con su Offset). Ese
    // clip se mide como uno sin velocidad y muestra el aviso, así que «Volver a sincronizar» tiene que poder con él.
    [Theory]
    [InlineData(ResyncMode.Move)]
    [InlineData(ResyncMode.Slip)]
    public void Resync_works_on_a_clip_whose_speed_is_back_at_normal(ResyncMode mode)
    {
        TrimFixture f = Pair();
        TimelineEdits.SetReverse(f.Ctx, "v", true);
        TimelineEdits.Trim(f.Ctx, "v", TrimEdge.In, F(3));
        TimelineEdits.SetReverse(f.Ctx, "v", false);
        Assert.True(f.Clip("a").Speed is { IsIdentity: true });
        f.Clip("a").Start += F(3);
        Assert.Equal(-3, Assert.Single(LinkSync.Find(f.Sequence)).Frames(Rational.Fps25));

        LinkSync.Resync(f.Ctx, "a", mode);

        f.AssertValid();
        Assert.Empty(LinkSync.Find(f.Sequence));
    }

    // «Insertar [...] nunca desincroniza[n].»
    [Fact]
    public void Inserting_in_the_middle_of_a_linked_pair_keeps_it_in_sync()
    {
        TrimFixture f = Pair();

        TimelineEdits.InsertMedia(f.Ctx, "m2", F(0), F(10), "v1", F(40), PlacementMode.Insert);

        f.AssertValid();
        Assert.Empty(LinkSync.Find(f.Sequence));
        Assert.Equal(F(80), f.Track("a1").End);
    }

    // «[...] mover [...] nunca desincroniza[n].»
    [Fact]
    public void Moving_with_insert_keeps_every_pair_in_sync()
    {
        TrimFixture f = Pair().Add("v1", "x", "m2", 0, 10, 100);

        TimelineEdits.Move(f.Ctx, "x", F(40), null, PlacementMode.Insert);

        f.AssertValid();
        Assert.Empty(LinkSync.Find(f.Sequence));
    }

    // «[...] cortar [...] nunca desincroniza[n].»
    [Fact]
    public void Ripple_deleting_on_one_track_keeps_every_pair_in_sync()
    {
        TrimFixture f = Pair().Add("v1", "x", "m2", 0, 10, 0);

        TimelineEdits.RippleDelete(f.Ctx, ["x"], RippleScope.Track);

        f.AssertValid();
        f.AssertClip("v", 0, 50, 10);
        f.AssertClip("a", 0, 50, 10);
        Assert.Empty(LinkSync.Find(f.Sequence));
    }

    // «[...] y cerrar huecos nunca desincronizan.»
    [Fact]
    public void Closing_a_gap_on_one_track_keeps_the_pair_in_sync()
    {
        TrimFixture f = Pair();

        TimelineEdits.CloseGap(f.Ctx, "v1", F(5), RippleScope.Track);

        f.AssertValid();
        f.AssertClip("v", 0, 50, 0);
        f.AssertClip("a", 0, 50, 0);
        Assert.Empty(LinkSync.Find(f.Sequence));
    }

    // «**1** a **9** cortan al ángulo de ese número desde el cursor hasta el siguiente corte [...]; el sonido sigue siendo
    // el de la primera.»
    [Fact]
    public void Switching_angle_cuts_from_the_cursor_to_the_next_cut_and_keeps_the_first_sound()
    {
        TrimFixture f = Pair();
        MulticamAngle[] angles = [new("m1", Time.Zero), new("m2", F(4))];

        string right = MulticamEdits.SwitchAngle(f.Ctx, angles, "v1", F(30), 1);

        f.AssertValid();
        f.AssertClip("v", 0, 10, 20);
        Clip switched = f.Clip(right);
        Assert.Equal("m2", switched.MediaId);
        Assert.Equal(F(30), switched.Start);
        Assert.Equal(F(70), switched.End);
        Assert.Equal(F(6), switched.SourceIn);
        Assert.All(f.Track("a1").Clips, c => Assert.Equal("m1", c.MediaId));
        Assert.Equal(F(70), f.Track("a1").End);
    }
}
