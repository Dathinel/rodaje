using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Editing;

/// <summary>Las ediciones con clips generadores (títulos): origen virtual sin límites, bordes al dividir y bloqueos.</summary>
public class GeneratorEditsTests
{
    /// <summary>Video m1 en v1 [0, 50), título t en v2 [10, 30) con origen [0, 20) y un keyframe de opacidad en su cuadro 5.</summary>
    private static TrimFixture Fixture()
    {
        TrimFixture f = new TrimFixture().Add("v1", "c", "m1", 0, 50, 0).AddTitle("v2", "t", 0, 20, 10);
        f.Clip("t").Effects.Add(new EffectInstance
        {
            Id = "fx",
            Type = BuiltInEffects.Transform,
            Parameters = { [BuiltInEffects.TransformParameters.Opacity] = new Parameter { Keyframes = [new Keyframe(F(5), 0), new Keyframe(F(6), 1)] } },
        });
        f.AssertValid();
        return f;
    }

    private static ClipGenerator G(TrimFixture f, string id) => f.Clip(id).Generator!;

    [Fact]
    public void TrimIn_PastTheVirtualStart_LeavesANegativeSourceIn()
    {
        TrimFixture f = Fixture();
        TimelineEdits.Trim(f.Ctx, "t", TrimEdge.In, F(-8));

        f.AssertClip("t", -8, 20, 2);
        Assert.True(G(f, "t").Edges.In);
        Assert.Equal(F(5), f.Clip("t").Effects[0].Parameters["opacity"].Keyframes[0].Time);
        f.AssertValid();

        // Un clip de medio no puede.
        AssertFails(() => TimelineEdits.Trim(f.Ctx, "c", TrimEdge.Out, F(51)), "más allá del final del archivo");
        AssertFails(() => TimelineEdits.Trim(f.Ctx, "c", TrimEdge.In, F(-1)), "antes del inicio del archivo");
    }

    [Fact]
    public void TrimOut_HasNoMaterialLimit()
    {
        TrimFixture f = Fixture();
        TimelineEdits.Trim(f.Ctx, "t", TrimEdge.Out, F(100_000));
        f.AssertClip("t", 0, 100_020, 10);
        f.AssertValid();
    }

    [Fact]
    public void RippleTrimRollAndSlide_WorkOnTitles()
    {
        TrimFixture f = new TrimFixture().AddTitle("v1", "a", 0, 10, 0).AddTitle("v1", "b", 0, 10, 10).AddTitle("v1", "c", 0, 10, 20);

        TimelineEdits.RippleTrim(f.Ctx, "a", TrimEdge.In, F(-5), RippleScope.Track);
        f.AssertClip("a", -5, 10, 0);
        f.AssertClip("b", 0, 10, 15);

        TimelineEdits.Roll(f.Ctx, "b", F(-3));
        f.AssertClip("b", 0, 7, 15);
        f.AssertClip("c", -3, 10, 22);

        TimelineEdits.Slide(f.Ctx, "b", F(3));
        f.AssertClip("a", -5, 13, 0);
        f.AssertClip("b", 0, 7, 18);
        f.AssertClip("c", 0, 10, 25);
        f.AssertValid();
        Assert.All(f.Track("v1").Clips, c => Assert.True(c.Generator!.Edges.In && c.Generator.Edges.Out));
    }

    [Fact]
    public void Split_FarFromTheAnimation_TurnsOffTheInnerEdges_AndKeepsContentAndKeyframes()
    {
        // Título de 4 s (100 cuadros): entrada de 10 cuadros y salida de 7,5; el corte en el cuadro 50 no toca ninguna.
        TrimFixture f = new TrimFixture().AddTitle("v2", "t", 0, 100, 10);
        string right = Assert.Single(TimelineEdits.Split(f.Ctx, "t", F(60)));

        f.AssertClip("t", 0, 50, 10);
        f.AssertClip(right, 50, 100, 60);
        Assert.True(G(f, "t").Edges is { In: true, Out: false, Before.Ticks: 0, After.Ticks: 0 });
        Assert.True(G(f, right).Edges is { In: false, Out: true, Before.Ticks: 0, After.Ticks: 0 });
        Assert.True(G(f, "t").SameContent(G(f, right)));
        Assert.NotSame(G(f, "t").Style, G(f, right).Style);

        // Dividir otra vez el derecho deja el trozo del medio sin entrada ni salida; recortar no los vuelve a encender.
        string third = Assert.Single(TimelineEdits.Split(f.Ctx, right, F(100)));
        Assert.False(G(f, right).Edges.In || G(f, right).Edges.Out);
        Assert.True(G(f, third).Edges.Out);
        TimelineEdits.Trim(f.Ctx, right, TrimEdge.Out, F(-2));
        Assert.False(G(f, right).Edges.Out);
        f.AssertValid();
    }

    [Fact]
    public void Split_InsideTheEntrance_KeepsWhereTheWholeTitleWas()
    {
        // Título de 20 cuadros: el corte en el cuadro 4 cae en la entrada (10 cuadros).
        TrimFixture f = Fixture();
        string right = Assert.Single(TimelineEdits.Split(f.Ctx, "t", F(14)));

        f.AssertClip("t", 0, 4, 10);
        f.AssertClip(right, 4, 20, 14);
        // La izquierda no ve la salida, pero sin el resto del título su entrada se encogería: guarda los 16 cuadros.
        Assert.True(G(f, "t").Edges is { In: true, Out: false });
        Assert.Equal(F(16), G(f, "t").Edges.After);
        // La derecha sigue la entrada desde su cuadro 4.
        Assert.True(G(f, right).Edges is { In: true, Out: true });
        Assert.Equal(F(4), G(f, right).Edges.Before);
        Assert.Equal(F(5), f.Clip(right).Effects[0].Parameters["opacity"].Keyframes[0].Time);

        // El trozo del medio de un segundo corte (cuadros 4 a 10 del original) aún lleva el final de la entrada.
        string third = Assert.Single(TimelineEdits.Split(f.Ctx, right, F(20)));
        Assert.True(G(f, right).Edges is { In: true, Out: false, After.Ticks: 0 });
        Assert.Equal(F(4), G(f, right).Edges.Before);
        Assert.True(G(f, third).Edges is { In: false, Out: true, Before.Ticks: 0 });
        f.AssertValid();

        var history = new UndoStack(f.Project);
        history.Execute(Edits.SetTitleEdges("seq", third, true, null));
        Assert.True(G(f, third).Edges.In);
        history.Undo();
        Assert.False(G(f, third).Edges.In);
    }

    [Fact]
    public void SetEdges_ValidatesTheProjectLikeTheOtherTitleEdits()
    {
        TrimFixture f = Fixture();
        G(f, "t").Edges.Before = Time.FromTicks(-1);
        AssertFails(() => TitleEdits.SetEdges(f.Ctx, "t", false, null), "desfase");
    }

    [Fact]
    public void Split_KeepsATransitionBetweenTitlesOnItsCut()
    {
        TrimFixture f = new TrimFixture().AddTitle("v1", "a", 0, 20, 0).AddTitle("v1", "b", 0, 20, 20);
        G(f, "b").Text = "Otro";
        string transition = TransitionEdits.AddTransition(f.Ctx, "a", BuiltInTransitions.Crossfade, F(4));

        TimelineEdits.Split(f.Ctx, "a", F(5));

        TransitionInstance kept = f.Track("v1").FindTransition(transition)!;
        Assert.Equal("b", kept.To);
        Assert.NotEqual("a", kept.From);
        f.AssertValid();
    }

    [Fact]
    public void OverwriteInTheMiddle_SplitsTheTitleWithInnerEdgesOff()
    {
        TrimFixture f = new TrimFixture().AddTitle("v1", "t", 0, 30, 0);
        TimelineEdits.InsertMedia(f.Ctx, "m1", F(0), F(10), "v1", F(10), PlacementMode.Overwrite);

        List<Clip> titles = f.Track("v1").Clips.FindAll(c => c.Generator is not null);
        Assert.Equal(2, titles.Count);
        Assert.False(titles[0].Generator!.Edges.Out);
        Assert.False(titles[1].Generator!.Edges.In);
        Assert.Equal(F(20), titles[1].SourceIn);
        f.AssertValid();
    }

    [Fact]
    public void MoveAndRippleDelete_CarryTitles()
    {
        TrimFixture f = Fixture();
        TimelineEdits.Move(f.Ctx, "t", F(60), "v1", PlacementMode.Strict);
        Assert.Contains(f.Track("v1").Clips, c => c.Id == "t" && c.Start == F(60));

        TimelineEdits.RippleDelete(f.Ctx, ["c"], RippleScope.Track);
        f.AssertClip("t", 0, 20, 10);
        f.AssertValid();
    }

    [Fact]
    public void MoveToAnAudioTrack_IsRejected()
    {
        TrimFixture f = Fixture();
        AssertFails(() => TimelineEdits.Move(f.Ctx, "t", F(0), "a1", PlacementMode.Strict), "audio");
    }

    public static TheoryData<string, Action<EditContext>> SpeedEdits => new()
    {
        { "deslizar", ctx => TimelineEdits.Slip(ctx, "t", F(2)) },
        { "velocidad", ctx => TimelineEdits.SetSpeed(ctx, "t", new Rational(2, 1)) },
        { "velocidad", ctx => TimelineEdits.RateStretch(ctx, "t", TrimEdge.Out, F(4)) },
        { "velocidad", ctx => TimelineEdits.SetCurve(ctx, "t", SpeedPresets.Curve(SpeedPresets.All[0])) },
        { "velocidad", ctx => TimelineEdits.SetReverse(ctx, "t", true) },
        { "velocidad", ctx => TimelineEdits.SetPreservePitch(ctx, "t", false) },
        { "congelar", ctx => TimelineEdits.FreezeFrame(ctx, "t", F(15)) },
    };

    [Theory]
    [MemberData(nameof(SpeedEdits))]
    public void SlipAndSpeed_AreBlockedOnTitles(string expected, Action<EditContext> edit)
    {
        TrimFixture f = Fixture();
        string before = Commands.ProjectDump.Of(f.Project);
        EditException ex = AssertFails(() => edit(f.Ctx), "es un título");
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Equal(before, Commands.ProjectDump.Of(f.Project));
    }

    [Fact]
    public void SlipOnAMediaClipLinkedToATitle_IsBlocked()
    {
        TrimFixture f = new TrimFixture().Add("v1", "c", "m1", 10, 30, 0, link: "L").AddTitle("v2", "t", 0, 20, 0, link: "L");
        AssertFails(() => TimelineEdits.Slip(f.Ctx, "c", F(2)), "es un título");
        TimelineEdits.Trim(f.Ctx, "c", TrimEdge.Out, F(-5));
        f.AssertClip("t", 0, 15, 0);
    }

    [Fact]
    public void Undo_SeesTextAndEdgeChanges_AndRestoresThem()
    {
        TrimFixture f = Fixture();
        var stack = new UndoStack(f.Project);
        string original = Commands.ProjectDump.Of(f.Project);

        stack.Execute(new SequenceEditCommand("Escribir", "seq", ctx => ctx.Sequence.GetClip("t").Clip.Generator!.Text = "Nuevo"));
        stack.Execute(new SequenceEditCommand("Estilo", "seq", ctx => ctx.Sequence.GetClip("t").Clip.Generator!.Style!.Size = 50));
        stack.Execute(Edits.Split("seq", "t", F(20)));
        Assert.Equal(3, stack.Count);

        stack.Undo();
        Assert.True(G(f, "t").Edges.Out);
        Assert.Equal(50, G(f, "t").Style!.Size);
        stack.Undo();
        stack.Undo();
        Assert.Equal("Título", G(f, "t").Text);
        Assert.Equal(original, Commands.ProjectDump.Of(f.Project));

        stack.Redo();
        Assert.Equal("Nuevo", G(f, "t").Text);
    }

    [Fact]
    public void Snapshot_ComparesGeneratorsAndRoles()
    {
        TrimFixture f = Fixture();
        SequenceSnapshot snapshot = SequenceSnapshot.Capture(f.Sequence);
        Assert.True(snapshot.Matches(f.Sequence));

        G(f, "t").Style!.In!.Curves["opacity"][0] = new StyleKey(0, 0.5);
        Assert.False(snapshot.Matches(f.Sequence));
        snapshot.Restore(f.Sequence);
        Assert.True(snapshot.Matches(f.Sequence));

        f.Track("v2").Role = TrackRoles.Subtitles;
        Assert.False(snapshot.Matches(f.Sequence));
        snapshot.Restore(f.Sequence);
        Assert.Null(f.Track("v2").Role);

        Clip media = f.Clip("c").Clone();
        Clip title = f.Clip("t").Clone();
        title.Generator = null;
        Assert.False(SequenceSnapshot.ClipStateEquals(f.Clip("t"), title));
        Assert.True(SequenceSnapshot.ClipStateEquals(f.Clip("c"), media));
    }

    [Fact]
    public void RemoveMedia_IgnoresTitles()
    {
        TrimFixture f = Fixture();
        new RemoveMediaCommand("m2").Apply(f.Project);
        Assert.Null(f.Project.FindMedia("m2"));
        Assert.Throws<EditException>(() => new RemoveMediaCommand("m1").Apply(f.Project));
    }

    [Fact]
    public void Copy_IsIndependent()
    {
        TrimFixture f = Fixture();
        Project copy = f.Project.Clone();
        copy.Sequences[0].GetClip("t").Clip.Generator!.Style!.Font.Family = "Arial";
        copy.Sequences[0].GetTrack("v2").Role = TrackRoles.Subtitles;

        Assert.Equal("Inter", G(f, "t").Style!.Font.Family);
        Assert.Null(f.Track("v2").Role);
    }
}
