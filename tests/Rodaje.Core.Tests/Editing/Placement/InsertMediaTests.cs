using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Editing.Placement.PlacementBuilder;

namespace Rodaje.Core.Tests.Editing.Placement;

public class InsertMediaTests
{
    private static void Fails(PlacementBuilder b, Action action, string expectedMessagePart)
    {
        string before = b.Snapshot();
        EditException ex = Assert.Throws<EditException>(action);
        Assert.Contains(expectedMessagePart, ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, b.Snapshot());
    }

    [Fact]
    public void Creates_clip_with_exact_fields()
    {
        var b = new PlacementBuilder();

        IReadOnlyList<string> ids = TimelineEdits.InsertMedia(b.Context(), "av", F(10), F(30), "v1", F(5), PlacementMode.Strict);

        Assert.Equal(["clip-1"], ids);
        Clip clip = b.Clip("clip-1");
        Assert.Equal("av", clip.MediaId);
        Assert.Equal(282_240_000L, clip.SourceIn.Ticks);
        Assert.Equal(846_720_000L, clip.SourceOut.Ticks);
        Assert.Equal(141_120_000L, clip.Start.Ticks);
        Assert.Null(clip.LinkId);
        Assert.True(clip.Enabled);
        Assert.Empty(clip.Effects);
        b.AssertValid();
    }

    [Fact]
    public void Creates_linked_audio_clip()
    {
        var b = new PlacementBuilder();

        IReadOnlyList<string> ids = TimelineEdits.InsertMedia(b.Context(), "av", F(0), F(20), "v1", F(10), PlacementMode.Strict, "a2");

        Assert.Equal(["clip-1", "clip-2"], ids);
        Assert.Equal("clip-1@10[0,20)", b.Layout("v1"));
        Assert.Equal("clip-2@10[0,20)", b.Layout("a2"));
        Assert.Equal("link-3", b.Clip("clip-1").LinkId);
        Assert.Equal("link-3", b.Clip("clip-2").LinkId);
        b.AssertValid();
    }

    [Fact]
    public void Primary_on_audio_track_with_linked_video()
    {
        var b = new PlacementBuilder();

        IReadOnlyList<string> ids = TimelineEdits.InsertMedia(b.Context(), "av", F(0), F(20), "a1", F(0), PlacementMode.Strict, "v2");

        Assert.Equal(["clip-1", "clip-2"], ids);
        Assert.Equal("clip-1@0[0,20)", b.Layout("a1"));
        Assert.Equal("clip-2@0[0,20)", b.Layout("v2"));
        b.AssertValid();
    }

    [Fact]
    public void Audio_only_media_on_audio_track()
    {
        var b = new PlacementBuilder();

        TimelineEdits.InsertMedia(b.Context(), "aud", F(0), F(80), "a1", F(0), PlacementMode.Strict);

        Assert.Equal("clip-1@0[0,80)", b.Layout("a1"));
        b.AssertValid();
    }

    [Fact]
    public void Overwrite_mode_trims_what_is_below_on_both_tracks()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "bv", 0, 0, 40, link: "B");
        b.Add("a1", "ba", 0, 0, 40, link: "B");

        TimelineEdits.InsertMedia(b.Context(), "av", F(50), F(60), "v1", F(10), PlacementMode.Overwrite, "a1");

        // clip-1 y clip-2 son los nuevos, link-3 su enlace; clip-4/link-5 y clip-6 las mitades derechas.
        Assert.Equal("bv@0[0,10) clip-1@10[50,60) clip-4@20[20,40)", b.Layout("v1"));
        Assert.Equal("ba@0[0,10) clip-2@10[50,60) clip-6@20[20,40)", b.Layout("a1"));
        Assert.Equal("link-5", b.Clip("clip-4").LinkId);
        Assert.Equal("link-5", b.Clip("clip-6").LinkId);
        b.AssertValid();
    }

    [Fact]
    public void Insert_mode_pushes_right()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 0, 0, 20);
        b.Add("v1", "c2", 20, 0, 10);

        TimelineEdits.InsertMedia(b.Context(), "vid", F(0), F(5), "v1", F(20), PlacementMode.Insert);

        Assert.Equal("c1@0[0,20) clip-1@20[0,5) c2@25[0,10)", b.Layout("v1"));
        b.AssertValid();
    }

    [Fact]
    public void Strict_mode_occupied_names_clip()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 0, 0, 20);

        Fails(b, () => TimelineEdits.InsertMedia(b.Context(), "vid", F(0), F(5), "v1", F(19), PlacementMode.Strict),
            "El lugar está ocupado por el clip «c1» en la pista «Video 1».");
    }

    [Fact]
    public void Strict_mode_occupied_on_linked_track_names_clip()
    {
        var b = new PlacementBuilder();
        b.Add("a1", "music", 0, 0, 20, media: "aud");

        Fails(b, () => TimelineEdits.InsertMedia(b.Context(), "av", F(0), F(5), "v1", F(0), PlacementMode.Strict, "a1"),
            "El lugar está ocupado por el clip «music» en la pista «Audio 1».");
    }

    [Fact]
    public void Unknown_media_fails()
    {
        var b = new PlacementBuilder();
        Fails(b, () => TimelineEdits.InsertMedia(b.Context(), "zzz", F(0), F(5), "v1", F(0), PlacementMode.Strict),
            "No existe el medio «zzz» en el proyecto.");
    }

    [Fact]
    public void Unknown_track_fails()
    {
        var b = new PlacementBuilder();
        Fails(b, () => TimelineEdits.InsertMedia(b.Context(), "av", F(0), F(5), "v7", F(0), PlacementMode.Strict),
            "No existe la pista «v7» en la secuencia.");
    }

    [Fact]
    public void Unknown_linked_track_fails()
    {
        var b = new PlacementBuilder();
        Fails(b, () => TimelineEdits.InsertMedia(b.Context(), "av", F(0), F(5), "v1", F(0), PlacementMode.Strict, "a9"),
            "No existe la pista «a9» en la secuencia.");
    }

    [Fact]
    public void Audio_only_media_on_video_track_fails()
    {
        var b = new PlacementBuilder();
        Fails(b, () => TimelineEdits.InsertMedia(b.Context(), "aud", F(0), F(5), "v1", F(0), PlacementMode.Strict),
            "«Música» no tiene imagen, así que no puede ir en la pista de video «Video 1».");
    }

    [Fact]
    public void Video_only_media_on_audio_track_fails()
    {
        var b = new PlacementBuilder();
        Fails(b, () => TimelineEdits.InsertMedia(b.Context(), "vid", F(0), F(5), "a1", F(0), PlacementMode.Strict),
            "«Imagen» no tiene sonido, así que no puede ir en la pista de audio «Audio 1».");
    }

    [Fact]
    public void Linked_track_of_same_kind_fails()
    {
        var b = new PlacementBuilder();
        Fails(b, () => TimelineEdits.InsertMedia(b.Context(), "av", F(0), F(5), "v1", F(0), PlacementMode.Strict, "v2"),
            "La pista enlazada «Video 2» tiene que ser de audio, porque la principal es de video.");
        Fails(b, () => TimelineEdits.InsertMedia(b.Context(), "av", F(0), F(5), "a1", F(0), PlacementMode.Strict, "a2"),
            "La pista enlazada «Audio 2» tiene que ser de video, porque la principal es de audio.");
    }

    [Fact]
    public void Linked_track_without_matching_stream_fails()
    {
        var b = new PlacementBuilder();
        Fails(b, () => TimelineEdits.InsertMedia(b.Context(), "vid", F(0), F(5), "v1", F(0), PlacementMode.Strict, "a1"),
            "«Imagen» no tiene sonido");
    }

    [Fact]
    public void Negative_source_in_fails()
    {
        var b = new PlacementBuilder();
        Fails(b, () => TimelineEdits.InsertMedia(b.Context(), "av", -F(1), F(5), "v1", F(0), PlacementMode.Strict),
            "El punto de entrada no puede ser negativo.");
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(6, 5)]
    public void Out_not_after_in_fails(long inF, long outF)
    {
        var b = new PlacementBuilder();
        Fails(b, () => TimelineEdits.InsertMedia(b.Context(), "av", F(inF), F(outF), "v1", F(0), PlacementMode.Strict),
            "El punto de salida tiene que ir después del punto de entrada.");
    }

    [Fact]
    public void Out_past_media_end_fails()
    {
        var b = new PlacementBuilder();
        Fails(b, () => TimelineEdits.InsertMedia(b.Context(), "vid", F(0), F(50) + Time.FromTicks(1), "v1", F(0), PlacementMode.Strict),
            "pasa el final de «Imagen», que dura 00:00:02.000.");
    }

    [Fact]
    public void Out_exactly_at_media_end_is_allowed()
    {
        var b = new PlacementBuilder();
        TimelineEdits.InsertMedia(b.Context(), "vid", F(0), F(50), "v1", F(0), PlacementMode.Strict);
        Assert.Equal("clip-1@0[0,50)", b.Layout("v1"));
        b.AssertValid();
    }

    [Fact]
    public void Negative_position_fails()
    {
        var b = new PlacementBuilder();
        Fails(b, () => TimelineEdits.InsertMedia(b.Context(), "av", F(0), F(5), "v1", -F(1), PlacementMode.Strict),
            "El clip no puede empezar antes del inicio de la línea de tiempo.");
    }

    [Fact]
    public void Locked_track_fails()
    {
        var b = new PlacementBuilder();
        b.Track("v1").Locked = true;
        Fails(b, () => TimelineEdits.InsertMedia(b.Context(), "av", F(0), F(5), "v1", F(0), PlacementMode.Strict),
            "La pista «Video 1» está bloqueada.");
    }

    [Fact]
    public void Locked_linked_track_fails()
    {
        var b = new PlacementBuilder();
        b.Track("a1").Locked = true;
        Fails(b, () => TimelineEdits.InsertMedia(b.Context(), "av", F(0), F(5), "v1", F(0), PlacementMode.Strict, "a1"),
            "La pista «Audio 1» está bloqueada.");
    }

    [Fact]
    public void Append_on_empty_track_starts_at_zero()
    {
        var b = new PlacementBuilder();

        IReadOnlyList<string> ids = TimelineEdits.AppendMedia(b.Context(), "av", F(0), F(10), "v1");

        Assert.Equal(["clip-1"], ids);
        Assert.Equal("clip-1@0[0,10)", b.Layout("v1"));
        b.AssertValid();
    }

    [Fact]
    public void Append_goes_after_last_clip()
    {
        var b = new PlacementBuilder();
        b.Add("v1", "c1", 0, 0, 10);
        b.Add("v1", "c2", 30, 0, 7);
        b.Add("v2", "other", 0, 0, 90);

        TimelineEdits.AppendMedia(b.Context(), "av", F(20), F(25), "v1");

        Assert.Equal("c1@0[0,10) c2@30[0,7) clip-1@37[20,25)", b.Layout("v1"));
        b.AssertValid();
    }

    [Theory]
    [InlineData(40, 25, 40)]
    [InlineData(10, 25, 25)]
    public void Append_with_link_uses_furthest_end(long videoEnd, long audioEnd, long expectedStart)
    {
        var b = new PlacementBuilder();
        b.Add("v1", "v", 0, 0, videoEnd);
        b.Add("a1", "a", 0, 0, audioEnd);

        IReadOnlyList<string> ids = TimelineEdits.AppendMedia(b.Context(), "av", F(0), F(10), "v1", "a1");

        Assert.Equal(["clip-1", "clip-2"], ids);
        Assert.Equal(expectedStart * FrameTicks, b.Clip("clip-1").Start.Ticks);
        Assert.Equal(expectedStart * FrameTicks, b.Clip("clip-2").Start.Ticks);
        Assert.Equal("link-3", b.Clip("clip-2").LinkId);
        b.AssertValid();
    }

    [Fact]
    public void Append_validates_like_insert()
    {
        var b = new PlacementBuilder();
        Fails(b, () => TimelineEdits.AppendMedia(b.Context(), "aud", F(0), F(10), "v1"), "no tiene imagen");
        Fails(b, () => TimelineEdits.AppendMedia(b.Context(), "av", F(0), F(10), "v1", "a5"), "No existe la pista «a5»");
    }

    [Fact]
    public void Null_context_throws()
    {
        Assert.Throws<ArgumentNullException>(() => TimelineEdits.InsertMedia(null!, "av", F(0), F(1), "v1", F(0), PlacementMode.Strict));
        Assert.Throws<ArgumentNullException>(() => TimelineEdits.AppendMedia(null!, "av", F(0), F(1), "v1"));
    }
}
