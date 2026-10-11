using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Editing.Speed;

public sealed class SpeedPresetTests
{
    public static TheoryData<SpeedPreset> Presets => [.. SpeedPresets.All];

    [Theory]
    [MemberData(nameof(Presets))]
    public void Every_preset_is_a_valid_curve_with_a_name_and_is_recognized(SpeedPreset preset)
    {
        SpeedCurve curve = SpeedPresets.Curve(preset);

        Assert.Null(RetimeMath.CurveProblem(curve));
        Assert.False(string.IsNullOrWhiteSpace(SpeedPresets.Name(preset)));
        Assert.Equal(preset, SpeedPresets.Match(curve));
        Assert.Equal(preset, SpeedPresets.Match(new SpeedCurve { Span = F(77), Points = [.. curve.Points] }));
    }

    [Fact]
    public void The_shapes_have_the_documented_mean_speeds_and_names()
    {
        Assert.Equal(["Montaje", "Héroe", "Bala", "Entrada rápida", "Salida rápida"], SpeedPresets.All.Select(SpeedPresets.Name));
        Assert.Equal(1.78, SpeedPresets.MeanRate(SpeedPresets.Points(SpeedPreset.Montage)), 6);
        Assert.Equal(0.824, SpeedPresets.MeanRate(SpeedPresets.Points(SpeedPreset.Hero)), 6);
        Assert.Equal(1.7904, SpeedPresets.MeanRate(SpeedPresets.Points(SpeedPreset.Bullet)), 6);
        Assert.Equal(1.825, SpeedPresets.MeanRate(SpeedPresets.Points(SpeedPreset.FastIn)), 6);
        Assert.Equal(1.825, SpeedPresets.MeanRate(SpeedPresets.Points(SpeedPreset.FastOut)), 6);
        Assert.Null(SpeedPresets.Match(new SpeedCurve { Span = F(1), Points = [new(0, 100), new(10_000, 200)] }));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeedPresets.Name((SpeedPreset)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeedPresets.Points((SpeedPreset)99));
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void Applying_a_preset_keeps_the_material_and_lasts_by_its_mean_speed(SpeedPreset preset)
    {
        var f = new TrimFixture().Add("v1", "v", "m2", 10, 190, 0, link: "L").Add("a1", "a", "m2", 10, 190, 0, link: "L");
        var stack = new UndoStack(f.Project);
        string original = ProjectSerializer.Serialize(f.Project);

        stack.Execute(Edits.ApplySpeedPreset("seq", "v", preset, ids: new SequentialIdGenerator()));

        f.AssertValid();
        foreach (string id in (string[])["v", "a"])
        {
            Clip clip = f.Clip(id);
            Assert.Equal(preset, SpeedPresets.Match(clip.Speed!.Curve!));
            Assert.Equal(F(10), clip.SourceIn);
            // El final va al cuadro más cercano: a 4× eso mueve hasta dos cuadros de material.
            Assert.InRange(clip.SourceOut.Ticks, F(188).Ticks, F(192).Ticks);
            Assert.Equal(clip.End, clip.End.SnapToFrame(Rational.Fps25));
            double expected = F(180).Ticks / SpeedPresets.MeanRate(SpeedPresets.Points(preset));
            Assert.InRange(clip.Duration.Ticks, expected - F(1).Ticks, expected + F(1).Ticks);
        }

        Assert.True(stack.Undo());
        Assert.Equal(original, ProjectSerializer.Serialize(f.Project));
    }

    [Fact]
    public void A_preset_merges_with_curve_changes_of_the_same_clip_and_shortening_joins_the_next()
    {
        var f = new TrimFixture().Add("v1", "c", "m1", 0, 90, 0).Add("v1", "n", "m2", 0, 10, 90);
        var stack = new UndoStack(f.Project);
        var ids = new SequentialIdGenerator();

        stack.Execute(Edits.ApplySpeedPreset("seq", "c", SpeedPreset.Hero, ids: ids));
        Assert.True(f.Clip("c").End > F(90));
        Assert.Equal(f.Clip("c").End, f.Clip("n").Start);

        stack.Execute(Edits.ApplySpeedPreset("seq", "c", SpeedPreset.Bullet, ids: ids));
        Assert.True(f.Clip("c").End < F(90));
        Assert.Equal(f.Clip("c").End, f.Clip("n").Start);

        Assert.Equal(1, stack.Count);
        Assert.True(stack.Undo());
        Assert.Null(f.Clip("c").Speed);
        Assert.Equal(F(90), f.Clip("n").Start);
    }
}
