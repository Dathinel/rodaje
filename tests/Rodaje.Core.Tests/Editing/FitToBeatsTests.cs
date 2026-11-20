using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Editing;

/// <summary>«Montaje al ritmo»: cada clip elegido termina en el pulso que toca y lo que sigue se corre.</summary>
public sealed class FitToBeatsTests
{
    private static readonly Time[] Every20 = [.. Enumerable.Range(1, 12).Select(i => F(i * 20))];

    /// <summary>Tres clips pegados de 50 cuadros en v1 (del medio de 200) y la música aparte en a2.</summary>
    private static TrimFixture Montage() => new TrimFixture()
        .Add("v1", "a", "m2", 0, 50, 0)
        .Add("v1", "b", "m2", 50, 100, 50)
        .Add("v1", "c", "m2", 100, 150, 100)
        .Add("a2", "music", "m3", 0, 50, 0);

    [Fact]
    public void Each_clip_ends_on_the_next_beat_and_the_following_ones_slide_to_stay_glued()
    {
        TrimFixture f = Montage();

        IReadOnlyList<(string ClipId, Time End)> changed = AnalysisEdits.FitToBeats(f.Ctx, ["a", "b", "c"], Every20, step: 1);

        Assert.Equal([("a", F(20)), ("b", F(40)), ("c", F(60))], changed);
        f.AssertClip("a", 0, 20, 0);
        f.AssertClip("b", 50, 70, 20);
        f.AssertClip("c", 100, 120, 40);
        f.AssertClip("music", 0, 50, 0);
        f.AssertValid();
    }

    [Fact]
    public void Every_two_beats_makes_each_clip_last_two_beats()
    {
        TrimFixture f = Montage();

        AnalysisEdits.FitToBeats(f.Ctx, ["c", "a", "b"], Every20, step: 2);

        f.AssertClip("a", 0, 40, 0);
        f.AssertClip("b", 50, 90, 40);
        f.AssertClip("c", 100, 140, 80);
    }

    [Fact]
    public void A_clip_grows_to_the_beat_but_never_past_its_material_and_a_beatless_one_stays()
    {
        // «short» tiene solo 10 cuadros de material por delante (90–100 de un medio de 100); «last» no tiene pulso después de su inicio.
        TrimFixture f = new TrimFixture()
            .Add("v1", "short", "m1", 90, 95, 0)
            .Add("v1", "last", "m2", 0, 30, 5)
            .Add("v1", "tail", "m2", 100, 110, 400);

        IReadOnlyList<(string ClipId, Time End)> changed = AnalysisEdits.FitToBeats(f.Ctx, ["short", "last", "tail"], [F(30), F(200)], step: 1);

        // Crece hasta donde hay material (10 cuadros), no hasta el pulso del 30; «last» corre con el ripple, se acorta hasta el
        // pulso del 30 y «tail», que no tiene ningún pulso después de su inicio, solo se corre.
        Assert.Equal([("short", F(10)), ("last", F(30))], changed);
        f.AssertClip("short", 90, 100, 0);
        f.AssertClip("last", 0, 20, 10);
        f.AssertClip("tail", 100, 110, 395);
    }

    [Fact]
    public void Titles_and_stills_have_no_limit_and_a_reversed_clip_only_shrinks()
    {
        TrimFixture f = new TrimFixture()
            .AddTitle("v2", "title", 0, 10, 0)
            .Add("v1", "back", "m1", 0, 90, 0);
        // Un clip en reversa lleva su mapa de tiempo ajustado, como lo deja la pestaña Velocidad.
        Clip back = f.Clip("back");
        back.Speed = new ClipSpeed { Reverse = true };
        ClipTimeMap.Fit(back.Start, back.SourceIn, back.SourceOut, back.Speed).ApplyTo(back);

        AnalysisEdits.FitToBeats(f.Ctx, ["title", "back"], [F(60), F(120)], step: 1);

        f.AssertClip("title", 0, 60, 0);
        // En reversa el final de la secuencia es el principio del material: acortar por la derecha corre SourceIn.
        f.AssertClip("back", 30, 90, 0);
    }

    [Fact]
    public void Linked_audio_follows_and_the_whole_montage_undoes_in_one_step()
    {
        TrimFixture f = new TrimFixture()
            .Add("v1", "v", "m2", 0, 50, 0, "L")
            .Add("a1", "a", "m2", 0, 50, 0, "L")
            .Add("v1", "second", "m2", 50, 100, 50);
        var undo = new UndoStack(f.Project);
        SequenceSnapshot before = SequenceSnapshot.Capture(f.Sequence);

        undo.Execute(Edits.FitClipsToBeats("seq", ["v", "second"], Every20, step: 1));

        f.AssertClip("v", 0, 20, 0);
        f.AssertClip("a", 0, 20, 0);
        f.AssertClip("second", 50, 70, 20);
        Assert.Equal(1, undo.Count);
        Assert.True(undo.Undo());
        Assert.True(before.Matches(f.Sequence));
    }

    [Fact]
    public void Beats_off_the_frame_grid_snap_and_a_clip_already_on_a_beat_does_not_change()
    {
        TrimFixture f = new TrimFixture().Add("v1", "a", "m2", 0, 20, 0).Add("v1", "b", "m2", 20, 40, 20);

        IReadOnlyList<(string ClipId, Time End)> changed = AnalysisEdits.FitToBeats(f.Ctx, ["a", "b"], [F(20) + Time.FromSeconds(0.004), F(40)], step: 1);

        Assert.Empty(changed);
        f.AssertClip("a", 0, 20, 0);
        f.AssertClip("b", 20, 40, 20);
    }
}
