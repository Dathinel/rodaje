using Rodaje.Core.Commands;
using Rodaje.Core.Editing;
using Rodaje.Core.Model;
using Rodaje.Core.Serialization;
using Rodaje.Core.Tests.Editing.Trimming;
using Rodaje.Core.Timing;
using static Rodaje.Core.Tests.Editing.Trimming.TrimFixture;

namespace Rodaje.Core.Tests.Editing.Speed;

/// <summary>Alt + borde sobre un clip con curva: la forma se estira, el material queda y lo que sigue se une o se empuja.</summary>
public sealed class CurveStretchTests
{
    [Fact]
    public void Stretching_a_curve_reaches_the_length_divides_every_speed_and_keeps_the_material()
    {
        var f = new TrimFixture().Add("v1", "c", "m2", 10, 110, 0, link: "L").Add("a1", "a", "m2", 10, 110, 0, link: "L")
            .Add("v1", "n", "m1", 0, 10, 150);
        TimelineEdits.SetCurve(f.Ctx, "c", SpeedPresets.Curve(SpeedPreset.Hero));
        Clip clip = f.Clip("c");
        Time before = clip.Duration;
        Time sourceOut = clip.SourceOut;
        SpeedCurve shape = clip.Speed!.Curve!.Clone();

        TimelineEdits.RateStretch(f.Ctx, "c", TrimEdge.Out, before);

        f.AssertValid();
        Assert.Equal(before + before, clip.Duration);
        Assert.Equal(F(10), clip.SourceIn);
        Assert.InRange(clip.SourceOut.Ticks, sourceOut.Ticks - F(1).Ticks, sourceOut.Ticks + F(1).Ticks);
        Assert.Equal(shape.Span + shape.Span, clip.Speed.Curve!.Span);
        Assert.Equal(shape.Points.Select(p => p.At), clip.Speed.Curve.Points.Select(p => p.At));
        Assert.Equal(shape.Points.Select(p => (p.Hundredths + 1) / 2), clip.Speed.Curve.Points.Select(p => p.Hundredths));
        Assert.Equal(clip.Speed.Curve.Points, f.Clip("a").Speed!.Curve!.Points);
        Assert.Equal(clip.End, f.Clip("n").Start);
    }

    [Fact]
    public void Shortening_a_curve_joins_the_next_clip_and_undoes_in_one_step()
    {
        var f = new TrimFixture().Add("v1", "c", "m2", 0, 100, 0).Add("v1", "n", "m1", 0, 10, 100);
        TimelineEdits.SetCurve(f.Ctx, "c", SpeedPresets.Curve(SpeedPreset.FastOut));
        Assert.Equal(f.Clip("c").End, f.Clip("n").Start);
        string original = ProjectSerializer.Serialize(f.Project);
        var stack = new UndoStack(f.Project);
        var ids = new SequentialIdGenerator();

        stack.Execute(Edits.RateStretch("seq", "c", TrimEdge.Out, -F(5), ids: ids));
        stack.Execute(Edits.RateStretch("seq", "c", TrimEdge.Out, -F(5), ids: ids));

        Clip clip = f.Clip("c");
        f.AssertValid();
        Assert.Equal(Time.Zero, clip.Start);
        Assert.Equal(clip.End, f.Clip("n").Start);
        Assert.Equal(1, stack.Count);
        stack.Undo();
        Assert.Equal(original, ProjectSerializer.Serialize(f.Project));
    }

    [Fact]
    public void Stretching_stops_where_a_point_would_leave_the_speed_range()
    {
        var f = new TrimFixture().Add("v1", "c", "m2", 0, 100, 0);
        TimelineEdits.SetCurve(f.Ctx, "c", SpeedPresets.Curve(SpeedPreset.Bullet));
        Clip clip = f.Clip("c");
        Time before = clip.Duration;

        TimelineEdits.RateStretch(f.Ctx, "c", TrimEdge.Out, F(5000));

        f.AssertValid();
        Assert.Equal(RetimeMath.MinHundredths, clip.Speed!.Curve!.Points.Min(p => p.Hundredths));
        Assert.InRange(clip.Duration.Ticks, (before.Ticks * 12 / 10) - F(1).Ticks, (before.Ticks * 12 / 10) + F(1).Ticks);

        TimelineEdits.SetCurve(f.Ctx, "c", new SpeedCurve { Points = [new(0, 10), new(10_000, 1000)] });
        Time full = clip.Duration;
        TimelineEdits.RateStretch(f.Ctx, "c", TrimEdge.Out, F(5));
        TimelineEdits.RateStretch(f.Ctx, "c", TrimEdge.Out, -F(5));
        Assert.Equal(full, clip.Duration);
    }

    [Fact]
    public void Stretching_the_second_half_of_a_split_curve_scales_its_offset_and_keeps_its_first_frame()
    {
        var f = new TrimFixture().Add("v1", "c", "m2", 0, 120, 0);
        TimelineEdits.SetCurve(f.Ctx, "c", SpeedPresets.Curve(SpeedPreset.Montage));
        Time at = f.Clip("c").Start + Time.FromTicks(f.Clip("c").Duration.Ticks / 3);
        string right = TimelineEdits.Split(f.Ctx, "c", at)[0];
        Clip clip = f.Clip(right);
        Time offset = clip.Speed!.Offset;
        Time first = clip.ToSourceTime(clip.Start);
        Time sourceOut = clip.SourceOut;

        TimelineEdits.RateStretch(f.Ctx, right, TrimEdge.Out, clip.Duration);

        f.AssertValid();
        Assert.Equal(offset + offset, clip.Speed.Offset);
        Assert.Equal(first, clip.ToSourceTime(clip.Start));
        Assert.InRange(clip.SourceOut.Ticks, sourceOut.Ticks - F(1).Ticks, sourceOut.Ticks + F(1).Ticks);
        Assert.Equal(f.Clip("c").End, clip.Start);
    }
}
