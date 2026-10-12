using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Tests.Model;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Editing;

/// <summary>Crear títulos, editar su texto y estilo, convertir sus animaciones y las pistas de subtítulos.</summary>
public class TitleEditsTests
{
    private static ClipGenerator NewTitle(string text = "Hola") => new() { Text = text, Style = ModelSamples.FullStyle() };

    [Fact]
    public void InsertTitle_GoesToTheHighestFreeTrack_NeverTheMainOne()
    {
        TrimFixture f = new TrimFixture().Add("v1", "c", "m1", 0, 50, 0);
        IReadOnlyList<string> created = TitleEdits.InsertTitle(f.Ctx, F(10), F(20), NewTitle(), "Texto");

        string id = Assert.Single(created);
        Assert.Same(f.Track("v2"), f.Sequence.GetClip(id).Track);
        f.AssertClip(id, 0, 20, 10);
        Assert.Equal("", f.Clip(id).MediaId);
        f.AssertValid();
    }

    [Fact]
    public void InsertTitle_WithNoRoom_AddsATrackOnTop()
    {
        TrimFixture f = new TrimFixture().AddTitle("v2", "t", 0, 50, 0);
        IReadOnlyList<string> created = TitleEdits.InsertTitle(f.Ctx, F(10), F(20), NewTitle(), "Texto");

        Assert.Equal(2, created.Count);
        Assert.Equal(3, f.Sequence.VideoTracks.Count);
        Assert.Equal(created[1], f.Sequence.VideoTracks[^1].Id);
        Assert.Equal("Texto", f.Sequence.VideoTracks[^1].Name);
        f.AssertValid();
    }

    [Fact]
    public void InsertTitle_SkipsLockedAndSubtitleTracks()
    {
        TrimFixture f = new TrimFixture().Lock("v2");
        string subtitles = TitleEdits.AddSubtitleTrack(f.Ctx, "Subtítulos");
        IReadOnlyList<string> created = TitleEdits.InsertTitle(f.Ctx, F(0), F(10), NewTitle(), "Texto");

        Assert.Equal(2, created.Count);
        Assert.NotEqual(subtitles, created[1]);
        Assert.Empty(f.Track(subtitles).Clips);
        Assert.Null(f.Track(created[1]).Role);
    }

    [Fact]
    public void InsertGenerator_RefusesAudioTracksAndOccupiedSpans()
    {
        TrimFixture f = new TrimFixture().AddTitle("v2", "t", 0, 50, 0);
        AssertFails(() => TitleEdits.InsertGenerator(f.Ctx, "a1", F(0), F(10), NewTitle()), "pista de video");
        AssertFails(() => TitleEdits.InsertGenerator(f.Ctx, "v2", F(10), F(10), NewTitle()), "ocupado");
        AssertFails(() => TitleEdits.InsertGenerator(f.Ctx, "v1", F(0), Time.Zero, NewTitle()), "duración");
    }

    [Fact]
    public void Typing_IsOneUndoStepUntilTheMergeBreaks()
    {
        TrimFixture f = new TrimFixture();
        var history = new UndoStack(f.Project);
        SequenceEditCommand insert = Edits.InsertTitle("seq", F(0), F(25), NewTitle(""));
        history.Execute(insert);
        string id = insert.CreatedIds[0];

        foreach (string text in new[] { "H", "Ho", "Hol", "Hola" })
        {
            history.Execute(Edits.SetTitleText("seq", id, text));
        }

        Assert.Equal(2, history.Count);
        history.BreakMerge();
        history.Execute(Edits.SetTitleText("seq", id, "Hola\nmundo"));
        Assert.Equal(3, history.Count);

        history.Undo();
        Assert.Equal("Hola", f.Clip(id).Generator!.Text);
        history.Undo();
        Assert.Equal("", f.Clip(id).Generator!.Text);
        history.Undo();
        Assert.Null(f.Sequence.FindClip(id));
    }

    [Fact]
    public void SetText_RejectsControlCharacters()
    {
        TrimFixture f = new TrimFixture().AddTitle("v2", "t", 0, 50, 0);
        AssertFails(() => TitleEdits.SetText(f.Ctx, "t", "a\u0007b"), "control");
        TitleEdits.SetText(f.Ctx, "t", "uno\tdos\ntres ‏");
        Assert.Equal("uno\tdos\ntres ‏", f.Clip("t").Generator!.Text);
    }

    [Fact]
    public void SetStyle_StoresACopy_AndSlidersMergeByKey()
    {
        TrimFixture f = new TrimFixture().AddTitle("v2", "t", 0, 50, 0);
        var history = new UndoStack(f.Project);
        TextStyle style = ModelSamples.FullStyle();
        style.Size = 120;
        history.Execute(Edits.SetTitleStyle("seq", "t", style, "size"));
        style.Size = 140;
        history.Execute(Edits.SetTitleStyle("seq", "t", style, "size"));

        Assert.Equal(1, history.Count);
        Assert.Equal(140, f.Clip("t").Generator!.Style!.Size);
        Assert.NotSame(style, f.Clip("t").Generator!.Style);
        style.Size = 1;
        Assert.Equal(140, f.Clip("t").Generator!.Style!.Size);

        style.Size = 5000;
        AssertFails(() => TitleEdits.SetStyle(f.Ctx, "t", style), "tamaño");
    }

    [Fact]
    public void TitleEdits_RefuseMediaClipsAndLockedTracks()
    {
        TrimFixture f = new TrimFixture().Add("v1", "c", "m1", 0, 50, 0).AddTitle("v2", "t", 0, 50, 0).Lock("v2");
        AssertFails(() => TitleEdits.SetText(f.Ctx, "c", "x"), "no es un título");
        AssertFails(() => TitleEdits.SetText(f.Ctx, "t", "x"), "bloqueada");
    }

    [Fact]
    public void Edges_AndBox_AreEditable()
    {
        TrimFixture f = new TrimFixture().AddTitle("v2", "t", 0, 50, 0);
        TitleEdits.SetEdges(f.Ctx, "t", false, null);
        Assert.False(f.Clip("t").Generator!.Edges.In);
        Assert.True(f.Clip("t").Generator!.Edges.Out);

        TitleEdits.SetBox(f.Ctx, "t", new TextFrame { WidthFraction = 0.8 });
        Assert.Equal(0.8, f.Clip("t").Generator!.Box!.WidthFraction);
        AssertFails(() => TitleEdits.SetBox(f.Ctx, "t", new TextFrame { WidthFraction = 2 }), "ancho");
    }

    [Fact]
    public void ConvertAnimation_BakesTheEdgesIntoTransformKeyframes()
    {
        // Título de 2 s en v2 desde el segundo 1 (cuadro 25), con el origen virtual desde 0.
        TrimFixture f = new TrimFixture().AddTitle("v2", "t", 0, 50, 25);
        f.Clip("t").Effects.Add(new EffectInstance
        {
            Id = "fx",
            Type = BuiltInEffects.Transform,
            Parameters = { ["y"] = new Parameter { Value = 380 }, ["opacity"] = new Parameter { Value = 0.5 } },
        });

        TitleEdits.ConvertAnimationToKeyframes(f.Ctx, "t");

        Clip clip = f.Clip("t");
        Assert.Null(clip.Generator!.Style!.In);
        Assert.Null(clip.Generator.Style.Out);
        EffectInstance transform = Assert.Single(clip.Effects);
        List<Keyframe> y = transform.Parameters["y"].Keyframes;
        Assert.Equal([new Keyframe(Time.Zero, 420), new Keyframe(Time.FromSeconds(0.4), 380)], y);

        // La opacidad se multiplica: entrada 0 → 0,5 y salida 0,5 → 0 en los últimos 0,3 s.
        List<Keyframe> opacity = transform.Parameters["opacity"].Keyframes;
        Assert.Equal(4, opacity.Count);
        Assert.Equal(new Keyframe(Time.Zero, 0), opacity[0]);
        Assert.Equal(new Keyframe(Time.FromSeconds(0.4), 0.5, Interpolation.EaseOut), opacity[1]);
        Assert.Equal(new Keyframe(Time.FromSeconds(2) - Time.FromSeconds(0.3), 0.5), opacity[2]);
        Assert.Equal(new Keyframe(Time.FromSeconds(2), 0), opacity[3]);
        f.AssertValid();
    }

    [Fact]
    public void ConvertAnimation_ShrinksBothWhenTheClipIsShort_AndHonorsTheEdges()
    {
        // 0,35 s de clip para 0,4 + 0,3 s de animación: se encogen a la mitad y se tocan en 0,2 s. Sin la salida activa,
        // solo la entrada.
        TrimFixture f = new TrimFixture().AddTitle("v2", "t", 0, 50, 0);
        f.Clip("t").SourceOut = Time.FromSeconds(0.35);
        TitleEdits.ConvertAnimationToKeyframes(f.Ctx, "t");
        List<Keyframe> opacity = f.Clip("t").Effects[0].Parameters["opacity"].Keyframes;
        Assert.Equal([Time.Zero, Time.FromSeconds(0.2), Time.FromSeconds(0.35)], opacity.Select(k => k.Time));
        Assert.Equal([0, 1, 0], opacity.Select(k => k.Value));

        TrimFixture g = new TrimFixture().AddTitle("v2", "t", 0, 50, 0);
        TitleEdits.SetEdges(g.Ctx, "t", null, false);
        TitleEdits.ConvertAnimationToKeyframes(g.Ctx, "t");
        Assert.NotNull(g.Clip("t").Generator!.Style!.Out);
        Assert.Equal(2, g.Clip("t").Effects[0].Parameters["opacity"].Keyframes.Count);

        TitleEdits.SetEdges(g.Ctx, "t", false, null);
        AssertFails(() => TitleEdits.ConvertAnimationToKeyframes(g.Ctx, "t"), "no tiene animación");
    }

    [Fact]
    public void ConvertAnimation_ScalesOffsetsFromTheStyleReference()
    {
        TrimFixture f = new TrimFixture().AddTitle("v2", "t", 0, 50, 0);
        f.Sequence.Width = 1080;
        f.Sequence.Height = 1920;
        TitleEdits.ConvertAnimationToKeyframes(f.Ctx, "t");
        Assert.Equal(40 * 1080.0 / 1920, f.Clip("t").Effects[0].Parameters["y"].Keyframes[0].Value, 9);
    }

    [Fact]
    public void SubtitleTrack_IsAVideoTrackWithItsRole_AndUndoes()
    {
        TrimFixture f = new TrimFixture();
        var history = new UndoStack(f.Project);
        SequenceEditCommand add = Edits.AddSubtitleTrack("seq");
        history.Execute(add);
        Track track = f.Track(add.CreatedIds[0]);
        Assert.Equal(TrackKind.Video, track.Kind);
        Assert.Equal(TrackRoles.Subtitles, track.Role);
        Assert.Same(track, f.Sequence.VideoTracks[^1]);

        history.Execute(Edits.SetTrackRole("seq", track.Id, null));
        Assert.Null(f.Track(track.Id).Role);
        history.Undo();
        Assert.Equal(TrackRoles.Subtitles, f.Track(track.Id).Role);
        history.Undo();
        Assert.Null(f.Sequence.FindTrack(track.Id));

        AssertFails(() => TitleEdits.SetTrackRole(f.Ctx, "a1", TrackRoles.Subtitles), "");
    }
}
